# Boutique Fashion POS

Application native Windows, tactile et hors ligne pour gérer une boutique de vêtements, chaussures et accessoires.

## Fonctions disponibles

- caisse rapide avec lecteur code-barres, paiements simples ou mixtes, crédit, remises et clôture avec écart ;
- catalogue par produit, variante, taille, couleur, SKU et code-barres ;
- mouvements de stock, réceptions, inventaires comptés, historique, ajustements protégés et stock négatif visible ;
- clients, plafonds et versements de crédit, contre-écritures, dépenses et rapports de pilotage ;
- retours, échanges, annulations, avoirs, proformas, duplicatas et historique documentaire ;
- modification, archivage, photos et promotions des produits ;
- tickets ESC/POS 58/80 mm via file Windows ou port Bluetooth COM ;
- factures A4/PDF avec PDFsharp/MigraDoc ;
- PIN responsable PBKDF2, audit et sauvegardes SQLite quotidiennes.

Les paramètres conservent également l'identité documentaire (adresse, contacts, NIF/RCCM, slogan, logo, cachet, signature, mentions) et l'imprimante thermique sélectionnée.

## Prérequis de développement

- Windows 10 ou 11 x64 ;
- Visual Studio 2026 avec la charge « Développement Desktop .NET », ou SDK .NET 10 ;
- pilotes Windows du POSIKEX et des imprimantes ;
- Inno Setup 6 pour produire l'installateur.

La solution WPF ne peut pas être exécutée ni validée matériellement sous macOS. La logique métier et les tests sont inclus, mais la recette finale doit être réalisée sur le terminal Windows.

## Démarrage

```powershell
dotnet restore .\BoutiqueFashion.slnx
dotnet test .\BoutiqueFashion.slnx
dotnet run --project .\src\BoutiqueFashion.App\BoutiqueFashion.App.csproj
```

Au premier lancement :

1. ouvrir **Paramètres**, saisir le nom de la boutique et définir un PIN responsable ;
2. choisir l'imprimante thermique et lancer un ticket de test ;
3. ajouter les produits manuellement ou utiliser le modèle `samples/import-produits.csv` ;
4. ouvrir la caisse avant la première vente.

## Données et sauvegardes

Les données sont stockées sous `%LOCALAPPDATA%\BoutiqueFashion` :

- `data\boutique.db` : base SQLite ;
- `backups\` : 30 sauvegardes locales au maximum ;
- `documents\` : PDF générés ;
- `assets\` : logos et images gérés par l'application.

Une sauvegarde est créée avec `VACUUM INTO`, ce qui garantit une copie SQLite cohérente. Une copie sur le même disque ne protège pas contre la panne ou le vol du terminal.

## Impression thermique

- **Intégrée/USB** : installer le pilote pour que l'imprimante apparaisse dans Windows, puis sélectionner sa file dans l'application.
- **Bluetooth** : appairer d'abord l'imprimante dans Windows. Utiliser la file créée par le pilote ou son port COM SPP.
- Le service envoie des commandes ESC/POS brutes. Vérifier sur chaque modèle la page de codes CP858, la coupe et la largeur papier.

## Publication

```powershell
.\scripts\publish.ps1
iscc .\installer\BoutiqueFashion.iss
```

Le premier script restaure, teste et publie une application autonome dans `artifacts\win-x64`. Inno Setup produit ensuite l'installateur dans `installer\output` sans toucher aux données stockées dans `%LOCALAPPDATA%` lors d'une mise à jour.

## Architecture

- `Domain` : entités et règles sans dépendance technique ;
- `Application` : DTO et contrats des cas d'usage ;
- `Infrastructure` : EF Core/SQLite, sécurité, impressions, PDF et sauvegardes ;
- `App` : interface WPF MVVM ;
- `Tests` : règles métier et scénarios d'intégration SQLite.

## Recette matérielle obligatoire

Tester sur le POSIKEX : vente en moins d'une minute, double appui, scanner, accents, ticket long, formats 58/80 mm, imprimante éteinte, USB, Bluetooth, A4 et duplicata.
