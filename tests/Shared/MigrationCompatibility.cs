using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BoutiqueFashion.Testing;

/// <summary>
/// Vérifie que chaque migration reste lisible par la version précédente du code.
///
/// Le problème qu'elle traite : Velopack sait revenir au binaire précédent quand une version ne
/// démarre pas, mais rien ne revient sur le schéma. Si N migre la base puis échoue et qu'on
/// retombe en N-1, N-1 tourne contre un schéma N. EF Core ignore les lignes d'historique qu'il ne
/// connaît pas, donc cela fonctionne — à condition que la migration soit purement additive.
///
/// Une colonne « NOT NULL » sans défaut fait échouer tous les INSERT de N-1 ; une colonne
/// supprimée fait échouer tous ses SELECT. Ces deux cas sont, en pratique, la totalité des
/// régressions de ce type — d'où une vérification sur les opérations plutôt qu'une reconstruction
/// du code de la version précédente, qui serait lente, fragile et sans meilleur rendement.
///
/// Voir docs/lot5-mises-a-jour-a-distance.md, « Le point dur : la base de données ».
/// </summary>
public static class MigrationCompatibility
{
    /// <summary>
    /// Contractions délibérées, différées d'au moins une version après le code qui a cessé de se
    /// servir de la colonne. Chaque entrée demande d'écrire pourquoi : c'est exactement la
    /// friction voulue, et elle vaut mieux qu'un test qu'on désactive.
    ///
    /// Format de clé : « NomDeLaMigration:NomDeTable.NomDeColonne » (la colonne est omise pour
    /// une table entière).
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedContractions = new()
    {
        // Aucune à ce jour. Exemple de ce à quoi une entrée ressemblerait :
        // ["20261115_RetirerAncienChamp:Sales.LegacyNote"] = "Plus écrit depuis la 1.3.0, livrée en octobre ; aucun terminal ne peut retomber avant."
    };

    /// <summary>Une infraction trouvée, formulée telle qu'elle sera lue dans le journal de CI.</summary>
    public sealed record Violation(string Migration, string Rule, string Detail)
    {
        public override string ToString() => $"{Migration} — {Rule} : {Detail}";
    }

    /// <summary>
    /// Inspecte toutes les migrations de l'assembly. Retourne les infractions, vide si tout va bien.
    /// </summary>
    /// <param name="activeProvider">
    /// Nom du fournisseur EF. Certaines migrations générées interrogent
    /// <c>migrationBuilder.ActiveProvider</c> pour émettre du SQL spécifique ; le laisser à null
    /// changerait la branche prise et donc les opérations qu'on examine.
    /// </param>
    public static IReadOnlyList<Violation> Inspect(Assembly assembly, string activeProvider)
    {
        var violations = new List<Violation>();

        var migrations = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Migration).IsAssignableFrom(t))
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<MigrationAttribute>()))
            .Where(x => x.Attribute is not null)
            .OrderBy(x => x.Attribute!.Id, StringComparer.Ordinal);

        foreach (var (type, attribute) in migrations)
        {
            var migration = (Migration)Activator.CreateInstance(type)!;
            migration.ActiveProvider = activeProvider;
            var name = attribute!.Id;

            foreach (var operation in migration.UpOperations)
                Check(name, operation, violations);
        }

        return violations;
    }

    private static void Check(string migration, MigrationOperation operation, List<Violation> violations)
    {
        switch (operation)
        {
            // Une colonne obligatoire sans défaut : la version précédente ignore cette colonne,
            // donc ses INSERT ne la renseignent pas, donc ils échouent tous.
            case AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null } add:
                violations.Add(new Violation(migration, "colonne obligatoire sans valeur par défaut",
                    $"{add.Table}.{add.Name} — rendez-la nullable, ou donnez-lui un défaut."));
                break;

            // Rendre obligatoire une colonne qui ne l'était pas revient au même pour N-1.
            case AlterColumnOperation { IsNullable: false, OldColumn.IsNullable: true, DefaultValue: null, DefaultValueSql: null } alter:
                violations.Add(new Violation(migration, "colonne rendue obligatoire sans valeur par défaut",
                    $"{alter.Table}.{alter.Name}"));
                break;

            case DropColumnOperation drop when !Approved(migration, drop.Table, drop.Name):
                violations.Add(new Violation(migration, "colonne supprimée",
                    $"{drop.Table}.{drop.Name} — attendez une version de plus, puis inscrivez-la dans ApprovedContractions."));
                break;

            case DropTableOperation drop when !Approved(migration, drop.Name, null):
                violations.Add(new Violation(migration, "table supprimée", drop.Name));
                break;

            case RenameColumnOperation rename when !Approved(migration, rename.Table, rename.Name):
                violations.Add(new Violation(migration, "colonne renommée",
                    $"{rename.Table}.{rename.Name} → {rename.NewName} — ajoutez la nouvelle, recopiez, supprimez plus tard."));
                break;

            case RenameTableOperation rename when !Approved(migration, rename.Name, null):
                violations.Add(new Violation(migration, "table renommée", $"{rename.Name} → {rename.NewName}"));
                break;
        }
    }

    private static bool Approved(string migration, string table, string? column) =>
        ApprovedContractions.ContainsKey(column is null ? $"{migration}:{table}" : $"{migration}:{table}.{column}");
}
