# Catalogue de la Boîte à outils

`depot/` est le contenu du dépôt public dédié `Denis-GT/PCPerfSuite-catalogue` (décision D8). Ce dépôt n'existe pas
encore : copie ce dossier tel quel dans un nouveau dépôt, puis suis « Mise en place » de `depot/README.md`.

- `depot/catalogue-outils.json` : catalogue n° 1, identique à la copie livrée avec l'app
  (`src/PCPerfSuite.Core/Installations/catalogue-outils.json`).
- `depot/sources.json` : où l'Action trouve chaque version. Ses hôtes répètent ceux de `ToolCatalog.cs`, qui fait foi.
- `depot/outils/generer.cs` : régénération (Action quotidienne `depot/.github/workflows/catalogue-outils.yml`).
- `depot/outils/signer.cs` : clé, signature, vérification. La clé privée ne va jamais dans un dépôt ni dans la CI.

Tant que `ToolCatalogTrust.PublicKey` est vide, l'app ignore le catalogue en ligne et n'utilise que sa copie intégrée.
Elle le dit dans la page et dans Paramètres › Compatibilité de ce PC.

## Ajouter un outil ou changer une source

Un nouvel outil passe d'abord par l'app : définition figée dans `ToolCatalog.cs` (hôtes, éditeur relevé sur le vrai
fichier, type, détection), tests, puis entrée dans `sources.json` et dans la copie intégrée. Le catalogue en ligne ne
peut jamais ajouter d'hôte ni changer d'éditeur.

## Vérifié le 01/10/2026

- Générateur lancé sur ce poste : chaque source redonne les adresses et empreintes relevées à la main ; un changement
  forcé produit le n° 2, au même format octet pour octet.
- Signataire : clé de test, signature, vérification ; la signature est acceptée par `CatalogSignature` de l'app.
- L'Action elle-même n'a pas encore tourné : à lancer à la main (« Run workflow ») une fois le dépôt créé.
