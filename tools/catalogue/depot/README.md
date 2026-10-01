# Catalogue d'outils de PCPerfSuite

Ce dépôt publie la liste des versions que la Boîte à outils de PCPerfSuite propose de télécharger :
pour chaque outil, la version, l'adresse versionnée chez l'éditeur, l'empreinte SHA-256 et la taille.
Il ne contient aucun binaire et n'en rehéberge aucun.

- `catalogue-outils.json` : le catalogue lu par l'app (format 1, numéro `sequence` croissant).
- `catalogue-outils.json.sig` : sa signature ECDSA P-256, faite en local par Denis.
- `sources.json` : où trouver la dernière version de chaque outil (winget-pkgs, GitHub Releases, page de l'éditeur).
- `outils/generer.cs` : régénère le catalogue (lancé chaque jour par l'Action).
- `outils/signer.cs` : crée la clé, signe, vérifie.

## Confiance

L'app ne prend un catalogue que si sa signature est valide pour la clé publique qu'elle embarque, et si son
numéro dépasse celui qu'elle connaît déjà. Même signé, un catalogue ne peut changer que versions, adresses,
empreintes et tailles : les hôtes autorisés et l'éditeur Authenticode de chaque outil sont figés dans l'app.

La clé privée ne quitte jamais le poste de Denis (ni secret de dépôt, ni CI). Une Action compromise peut
ouvrir une PR, pas publier un catalogue que l'app accepterait.

## Chaque jour

1. L'Action `Catalogue d'outils` régénère le catalogue. Pour chaque nouveau fichier, elle le télécharge et le
   hache elle-même. Si l'empreinte de sa source ne correspond pas au fichier réel, elle garde l'ancienne entrée.
2. S'il a changé, elle ouvre ou met à jour la PR `maj-catalogue`, sans signature.
3. Denis relit la PR (versions, adresses), puis signe en local :

   ```
   git switch maj-catalogue && git pull
   dotnet run outils/signer.cs -- signer catalogue-outils.json <dossier de la clé>/cle-privee-catalogue.pem
   git add catalogue-outils.json.sig && git commit -m "Signe le catalogue" && git push
   ```

4. Il fusionne. Une PR fusionnée sans signature laisse l'app sur son dernier catalogue connu.

## Mise en place (une fois)

1. Créer ce dépôt public `Denis-GT/PCPerfSuite-catalogue`, branche `main`, avec le contenu de ce dossier.
2. Paramètres du dépôt › Actions › General : autoriser GitHub Actions à créer des pull requests.
3. Créer la clé, hors de tout dépôt (clé USB, coffre) :

   ```
   dotnet run outils/signer.cs -- nouvelle-cle E:\cle-catalogue
   ```

   Inscrire la clé publique affichée dans `ToolCatalogTrust.PublicKey`
   (`src/PCPerfSuite.Core/Installations/ToolCatalogStore.cs` de PCPerfSuite).
4. Signer le premier catalogue (n° 1, identique à la copie livrée avec l'app), puis pousser.

`dotnet run fichier.cs` demande le SDK .NET 10.
