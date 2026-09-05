# Lot 5 — Mises à jour à distance

> Plan d'implémentation. Fait suite aux lots 1 à 4 ([PR #1](https://github.com/touredri/gestion-boutique-fashion/pull/1)).

## Le problème

Pousser un changement vers une machine qui est **une caisse en service**, à plusieurs heures de route, sans personne de technique devant, dont la **base de données** est couplée au code, et qui peut avoir été hors réseau une semaine.

Chacune de ces quatre propriétés élimine une solution différente. Le téléchargement n'est pas la partie difficile.

## Décisions actées

| Sujet | Décision |
|---|---|
| Qui décide qu'une version part | **Le développeur**, côté serveur. La propriétaire ne fait que constater. |
| Distribution | **Velopack**, paquets servis par **notre serveur** (pas GitHub Releases directement). |
| Signature Authenticode | **Non.** HTTPS + jeton d'appareil + empreinte de paquet. Risque assumé, voir §7. |
| Numéro de version | Dérivé du tag git dans la CI. |
| Retour arrière piloté à distance | **Hors périmètre.** Republier N-1 en N+1 fait le même travail. |

## Ce qui existe déjà

- [build-windows.yml](../.github/workflows/build-windows.yml) : sur tag `v*`, publish win-x64 → installeur Inno Setup → GitHub Release.
- Terminal appairé : jeton d'appareil, `shopId`, canal HTTPS authentifié, cycle de sync ~30 s.
- `IBackupService.CreateAsync()` — instantané de la base.
- Migrations EF Core avec baseline : le schéma avance seul au démarrage.
- `AppPaths.Root` = `%LocalAppData%\BoutiqueFashion`, Velopack installera dans `%LocalAppData%\BanaShop`. **Les données sont déjà hors du dossier d'installation** — rien à déplacer. C'est la condition préalable de tout le lot ; elle est remplie par accident heureux.

## Le point dur : la base de données

Velopack sait revenir au binaire précédent quand la nouvelle version ne démarre pas. **Rien ne revient sur le schéma.** Si N migre la base, échoue, et qu'on retombe en N-1, N-1 tourne contre un schéma N.

EF Core ignore les lignes de `__EFMigrationsHistory` qu'il ne connaît pas — il n'applique que ce qui manque. Donc ça marche, *à condition que la migration soit additive*. Une colonne `NOT NULL` sans défaut fait échouer tous les `INSERT` de N-1 ; une colonne supprimée fait échouer tous ses `SELECT`.

D'où la règle, qui est le vrai livrable de ce lot :

> **N-1 doit pouvoir lire et écrire le schéma de N.**
> Colonne ajoutée : nullable, ou avec valeur par défaut. Aucune suppression ni renommage de colonne ou de table dans la même version que le code qui cesse de s'en servir. On supprime **une version plus tard**, quand plus aucun terminal ne peut retomber dessus.

C'est le motif *expand / contract*. Une règle écrite dans un fichier tient trois mois ; celle-ci sera tenue par un test (§5).

---

## §1 — Versionnage (prérequis bloquant)

Aujourd'hui aucun `<Version>` n'existe dans les `.csproj` : le tag git n'entre nulle part dans le binaire. Rien ne peut se comparer, donc rien ne peut se mettre à jour.

**[Directory.Build.props](../Directory.Build.props)**
```xml
<Version>0.0.0-local</Version>
```
Valeur volontairement absurde : un binaire compilé à la main ne doit jamais ressembler à une version publiée.

**CI** — le tag `v1.4.2` donne `-p:Version=1.4.2` au `dotnet publish`.

La version affichée dans l'application vient de `UpdateManager.CurrentVersion` quand l'app est gérée par Velopack, et de l'`AssemblyInformationalVersion` sinon.

## §2 — Empaquetage Velopack dans la CI

`build-windows.yml` est étendu, sur tag uniquement :

1. `dotnet publish` → `artifacts/win-x64` *(existant)*
2. `curl` du paquet complet précédent depuis le serveur vers `releases/` — sans lui, `vpk` ne peut pas calculer de delta et produit un paquet complet à chaque fois
3. `vpk pack --packId BanaShop --packVersion $VERSION --packDir artifacts/win-x64 --outputDir releases`
4. `curl` d'envoi de `releases/*` vers le serveur, avec le ciblage de départ (§3)
5. Publication du `Setup.exe` de Velopack sur la GitHub Release

**L'installeur Inno Setup disparaît.** Une application installée par Inno n'est pas gérée par Velopack : elle n'a ni `Update.exe`, ni la structure `current/` + `packages/`, et ne peut donc pas se mettre à jour. Les deux terminaux n'étant pas encore déployés, la bascule ne coûte rien. Si l'un l'avait déjà été, il faudrait une réinstallation manuelle — raison de plus pour faire ce lot avant la mise en service.

`vpk` s'installe dans le job par `dotnet tool install -g vpk`.

## §3 — Ciblage par boutique, côté serveur

C'est ici que vit la décision « qui reçoit quoi », et nulle part ailleurs.

`SimpleWebSource` demande `GET {base}/releases.win.json`. Le serveur **sert ce fichier dynamiquement**, filtré selon le jeton d'appareil qui accompagne la requête. Le terminal n'a donc aucune logique d'échelonnement à embarquer : il demande simplement « qu'est-ce qu'il y a pour moi ».

**Modèle serveur**
```
Release       Version, Channel, PublishedAt, Notes, PackageHash, SizeBytes, IsWithdrawn
ReleaseTarget ReleaseId, ShopId?      -- ShopId null = toutes les boutiques
```

**Routes**

| Route | Auth | Rôle |
|---|---|---|
| `GET /updates/releases.win.json` | jeton d'appareil | Flux Velopack, filtré par boutique |
| `GET /updates/{fichier}.nupkg` | jeton d'appareil | Paquet complet ou delta |
| `POST /admin/releases` | `ADMIN_API_KEY` | Dépôt d'une version + ciblage initial |
| `POST /admin/releases/{v}/promote` | `ADMIN_API_KEY` | Élargir le ciblage (`shopId` ou toutes) |
| `POST /admin/releases/{v}/withdraw` | `ADMIN_API_KEY` | Retirer : les terminaux qui ne l'ont pas prise ne la prendront pas |

`ADMIN_API_KEY` est une variable d'environnement du serveur, distincte de l'authentification de la propriétaire. Elle n'a pas d'interface : la CI l'appelle, et `curl` suffit pour le reste.

**Déroulé type**
```
git tag v1.4.2 && git push --tags
  → CI empaquette et dépose, ciblage « Banankabougou » seulement
  → Banankabougou se met à jour le soir même
  → deux jours d'observation
  → workflow_dispatch « promouvoir 1.4.2 » → toutes les boutiques
```

L'échelonnement est la seule protection réelle contre une version qui *démarre* mais qui est fonctionnellement cassée — Velopack ne détecte que l'échec au démarrage. Une boutique d'abord, c'est ce qui remplace les tests qu'on ne peut pas écrire.

## §4 — Le client dans le terminal

**Amorçage** — WPF génère son `Main`, il faut le reprendre pour que `VelopackApp` s'exécute avant tout le reste :

```xml
<!-- BoutiqueFashion.App.csproj -->
<StartupObject>BoutiqueFashion.App.App</StartupObject>
<ItemGroup>
  <ApplicationDefinition Remove="App.xaml"/>
  <Page Include="App.xaml"/>
</ItemGroup>
```
```csharp
[STAThread]
private static void Main(string[] args)
{
    VelopackApp.Build().Run();   // avant toute autre chose : gère les relances post-installation
    var app = new App();
    app.InitializeComponent();
    app.Run();
}
```

**`UpdateAgent`**, service hébergé aux côtés de `SyncAgent` :

- `UpdateManager` sur `SimpleWebSource(baseUrl, downloader)`, où `downloader` est un `IFileDownloader` maison qui pose l'en-tête `Authorization: Bearer <jeton d'appareil>`.
- **Garde de développement** : si `mgr.IsInstalled` est faux (exécution depuis `bin/`), on ne fait rien. Sans cette garde, `CheckForUpdatesAsync` lève une exception à chaque démarrage en debug.
- Vérification toutes les 6 h, et au démarrage. Téléchargement en tâche de fond, sans rien demander : il n'y a aucune raison d'interrompre une vendeuse pour un transfert de fichier.
- `MaximumDeltasBeforeFallback` laissé à sa valeur par défaut (10) : un terminal absent longtemps bascule seul sur le paquet complet.

**Le moment de l'application** — c'est la décision la plus importante du lot.

> `WaitExitThenApplyUpdates` : l'application s'installe **à la fermeture de l'application**, sans relance.

La vendeuse ferme la caisse en fin de journée, l'échange de fichiers se fait derrière, elle rouvre le lendemain matin sur la nouvelle version. Aucune interruption visible, aucun écran « veuillez patienter » devant une cliente.

Trois conditions avant d'armer :

1. **Aucune vacation de caisse ouverte.** Une mise à jour pendant une vacation, même à la fermeture de la fenêtre, laisserait une session ouverte sur une version et close sur une autre.
2. **File de synchronisation vide**, ou une tentative de vidage faite. Les données survivent — l'outbox est en base, hors du dossier d'installation — mais partir à jour évite d'avoir à distinguer un retard de sync d'un problème de mise à jour.
3. **Sauvegarde prise** via `IBackupService.CreateAsync()`. C'est le seul retour arrière possible sur les données.

Si l'une des trois manque, on ne fait rien et on retente à la fermeture suivante. Une mise à jour qui attend un jour de plus ne coûte rien ; une mise à jour appliquée au mauvais moment coûte une journée de caisse.

**Remontée d'état** — à chaque cycle de sync, le terminal joint sa version, la version téléchargée en attente, et le dernier échec s'il y en a eu un. Sans cette remontée, « est-ce que ça s'est installé ? » est une question sans réponse depuis ici.

## §5 — Le test qui tient la règle de schéma

Un test unitaire qui inspecte les opérations de chaque migration par réflexion. EF Core expose `UpOperations` comme une liste typée, donc la vérification est exacte et non textuelle :

```csharp
// Chaque migration doit rester lisible par la version précédente du code.
// Voir la règle expand/contract en tête de ce document.
foreach (var op in migration.UpOperations)
{
    if (op is AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null })
        → échec : « colonne NOT NULL sans défaut »
    if (op is DropColumnOperation or DropTableOperation or RenameColumnOperation or RenameTableOperation)
        → échec : « contraction interdite dans la même version »
}
```

Une liste nommée d'exceptions approuvées permet la contraction quand elle est délibérée et différée — chaque entrée demande d'écrire pourquoi, ce qui est exactement la friction voulue.

Le test tourne sur les deux contextes, terminal et serveur.

*Alternative écartée* : compiler le tag précédent en CI et faire tourner son `DbContext` contre le schéma neuf. Plus fidèle, mais lent, fragile, et sans rien attraper de plus dans les faits — les régressions de ce type sont toutes des ajouts non-nullables ou des suppressions.

## §6 — Côté PWA

**Pas de sixième onglet** — la barre reste à cinq. L'information va dans **Boutiques**, où vit déjà la gestion des appareils :

- version en service par boutique, et depuis quand ;
- « mise à jour 1.4.2 téléchargée, s'installera à la fermeture » quand c'est le cas ;
- dernier échec de mise à jour, s'il y en a eu un ;
- notification quand une boutique passe à une nouvelle version.

Aucune action. La propriétaire n'a pas à juger si un build est sûr — c'est la même logique que le catalogue, dont elle n'a pas la main sur la structure.

## §7 — Risques assumés

**Pas de certificat Authenticode.** SmartScreen avertira à la **première** installation seulement ; les mises à jour en place ne le déclenchent pas. Comme les deux postes sont installés une fois à la main, le coût réel est un clic. Un certificat OV coûte 200 à 400 € par an pour supprimer ce clic.

**Serveur injoignable = pas de mise à jour.** Acceptable : une mise à jour n'est jamais urgente. À ne pas confondre avec la caisse, qui elle continue de fonctionner hors ligne.

**Version qui démarre mais qui est cassée.** Velopack ne revient en arrière que sur un échec de démarrage. La parade est l'échelonnement (§3), pas la technique.

**Une seule chaîne de confiance.** Sans signature, c'est TLS et le jeton d'appareil qui garantissent la provenance. Quiconque prendrait le contrôle du serveur pourrait pousser un binaire. C'est déjà vrai de la base de données qu'il héberge.

## Vérification

**Automatisée** — le test de compatibilité de schéma (§5) ; un test du filtrage de `releases.win.json` par boutique, avec jeton croisé attendu en `403` ; un test de `UpdateAgent` refusant d'armer avec une vacation ouverte.

**Recette manuelle, sur Windows**
1. Installer 1.0.0 par le `Setup.exe` de Velopack.
2. Publier 1.0.1 ciblée sur une seule boutique — vérifier que l'autre terminal ne la voit pas.
3. Ouvrir une vacation, fermer l'application : **rien ne doit s'installer**.
4. Clôturer la caisse, fermer l'application : la mise à jour s'applique, la réouverture donne 1.0.1.
5. Vérifier la sauvegarde datée d'avant la bascule, et les données intactes.
6. Couper le réseau, publier 1.0.2, rebrancher : le terminal rattrape.
7. Publier une 1.0.3 qui plante au démarrage : Velopack doit revenir en 1.0.2.

## Hors périmètre

Retour arrière piloté depuis le téléphone ; mise à jour du serveur lui-même (c'est du `docker compose pull`, pas le même problème) ; mise à jour de la PWA et de la vitrine (le navigateur et le service worker s'en chargent déjà).

## Effort

| | |
|---|---|
| §1 Versionnage | ½ j |
| §2 Empaquetage CI | 1 j |
| §3 Ciblage serveur | 1 j |
| §4 Client terminal | 1 j |
| §5 Test de schéma | ½ j |
| §6 PWA | ½ j |
| Recette Windows | 1 j |

Environ **5 jours et demi**, dont une journée de recette qui exige un poste Windows réel — le reste passe par la CI.
