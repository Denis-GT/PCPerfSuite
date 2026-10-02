# Navigation de PCPerfSuite

À lire avant d'ajouter une page ou un sous-onglet. Les décisions transverses et les briques partagées sont dans
`docs/decisions.md`.

## Structure

- **Sections** : une seule liste dans la barre latérale, avec des en-têtes Surveiller, Régler, Diagnostiquer, Outils
  (`NavSection`). Une section sans page n'affiche pas d'en-tête.
- **Pages** : une entrée par page, décrite par `NavigationMenu.Pages`
  (`src/PCPerfSuite.App/ViewModels/NavigationMenu.cs`), dans l'ordre d'affichage. Chaque page a une clé stable
  (`PageKeys`) ; sa vue dans `MainWindow.xaml` se montre d'après cette clé, jamais d'après le titre.
- **Pages à venir** : déjà dans le menu, avec leur clé, et marquées « bientôt disponible ». Elles sont atténuées dans la
  barre et ouvrent `ComingSoonView`, dont le texte vient de `ComingSoonPages`.
- **Sous-onglets** : une page qui grossit reçoit des sous-onglets en pastilles plutôt qu'une entrée de plus. Le modèle
  est `AppSettingsViewModel.Sections` + le style `PillSelector` : une `AppSettingsSection(clé, titre)` par sous-onglet,
  et un panneau dont la `Visibility` compare `SelectedSection.Key`.
- **Page réservée à certains PC** : `NavPage.LaptopOnly` retire la page du menu sur un PC de bureau avéré ; sur un
  châssis indéterminé, elle reste et dit elle-même ce qu'elle trouve. N'ajoute une
  condition de ce genre que pour une page **sans objet** sur ce type de PC ; une fonction absente sur un PC qui pourrait
  l'avoir reste dans le menu, et sa page dit pourquoi elle est absente (règle 3 de CLAUDE.md).
- **Paramètres** : bouton du pied de la barre, hors de la liste (`NavigationMenu.Settings`).

### Hauteur de la barre latérale

Estimations en DIP, à confirmer à l'écran :

- **Hauteurs des blocs**
  - barre de titre et cadre de Windows : environ 39 ;
  - marque : environ 66 (icône de 36, marges 16 et 14) ;
  - pied en administrateur : environ 75 (Paramètres, et la ligne « Administrateur ») ;
  - pied sans administrateur : environ 152, avec le bandeau « Droits limités » ;
  - entrée : 30 (NavListItem en padding `12,5`) ;
  - en-tête de section : environ 28.
- **Liste complète** : 17 entrées et 4 en-têtes sur un PC de bureau, soit environ 622 ; 18 entrées sur un portable,
  soit environ 652.
- **À la taille par défaut (820)** : environ 640 disponibles en administrateur. Tout tient sur un PC de bureau ; un
  portable défile d'une quinzaine de pixels. Sans administrateur, environ 560 disponibles : la liste défile.
- **À la hauteur minimale (600)** : environ 420 disponibles. La liste défile au pixel (`CanContentScroll=False`) et
  toutes les entrées restent accessibles.
- **À 150 %**, le calcul est le même en DIP. Une fenêtre fait au plus environ 670 DIP sur un écran 1080p : la liste
  défile.

Une page de plus coûte 30 px. Avant d'en ajouter une, demande-toi si ce n'est pas un sous-onglet d'une page existante.

## Où va chaque fonctionnalité

État au 30/09/2026 : « livrée », « bientôt disponible » (page d'attente en place), « prévue » (sous-onglet ou bloc
d'une page livrée, pas encore fait). La conversation qui livre met sa ligne à jour.

| Fonctionnalité | Section | Page (clé) | Sous-onglet ou emplacement | Conversation | État |
|---|---|---|---|---|---|
| Monitoring en temps réel | Surveiller | Monitoring (`monitoring`) | — | existant | livrée |
| Signaux : bridage, journaux Windows, liens PCIe | Surveiller | Monitoring (`monitoring`) et diagnostic | pas de page ; lignes du diagnostic par fournisseur ; carte « Bridage » dans Monitoring | #4 | en partie : bridage CPU, journaux Windows et journal de session livrés au diagnostic ; bridage GPU, liens PCIe, contrôles et carte Monitoring prévus |
| Gestionnaire de processus | Surveiller | Processus (`processes`) | — | existant | livrée |
| Limites CPU / RAM / disque / réseau par processus | Surveiller | Processus (`processes`) | « Limiter… » dans le menu contextuel et le détail ; sous-onglet **Limites** pour la liste des règles | #21 | prévue |
| Overlay en jeu | Surveiller | Overlay (`overlay`) | — | existant | livrée |
| Choix de l'écran de l'overlay | Surveiller | Overlay (`overlay`) | bloc « Canal fenêtre » (`OverlayScreenPicker`) | #3 | livrée |
| Limites du processeur | Régler | Processeur (`cpu`) | sous-onglet **Réglages** | existant | livrée |
| Core parking et usage par cœur | Régler | Processeur (`cpu`) | sous-onglet **Cœurs** (`CoreParkingViewModel`) | #5 | livrée |
| Fiabilisation du tuning (garde-fous, veille, relecture) | Régler | Processeur, GPU, Ventilateurs | dans les pages existantes | #2 | prévue |
| Overclocking GPU | Régler | GPU (`gpu`) | — | existant | livrée |
| OC GPU AMD Radeon et Intel Arc vérifié | Régler | GPU (`gpu`) | dans la page | #14 | prévue |
| Courbes de ventilation | Régler | Ventilateurs (`fans`) | — | existant | livrée |
| Groupes de profils CPU + GPU + ventilation | Régler | Profils (`profiles`) | sous-onglet **Groupes** | #8 | livrée (expérimental) |
| Profils automatiques selon l'usage | Régler | Profils (`profiles`) | sous-onglet **Automatique** (`AutoProfilesViewModel`) | #9 | livrée (expérimental) |
| OC automatique GPU (sûr, classique, agressif) | Régler | OC automatique (`auto-overclock`) | sous-onglet **GPU** | #15 | bientôt disponible |
| Curve Optimizer AMD automatique | Régler | OC automatique (`auto-overclock`) | sous-onglet **CPU** | #16 | bientôt disponible |
| OC de l'écran (fréquence) | Régler | Écrans (`displays`) | — | #17 | bientôt disponible |
| Éclairage RGB : socle et OpenRGB | Régler | Éclairage (`lighting`) | page minimale (inventaire, couleur fixe) | #23 | bientôt disponible |
| Éclairage RGB : Dynamic Lighting et effets | Régler | Éclairage (`lighting`) | page complète | #24 | bientôt disponible |
| GPU dédié des portables | Régler | GPU portable (`laptop-gpu`), absente d'un PC de bureau avéré | — | #22 | bientôt disponible |
| Bench CPU / RAM / disque | Diagnostiquer | Bench et diagnostic (`bench-diagnostic`) | sous-onglet **Bench** | #10 | bientôt disponible |
| Bench GPU et test combiné « alimentation » | Diagnostiquer | Bench et diagnostic (`bench-diagnostic`) | sous-onglet **Bench** | #11 | bientôt disponible |
| Diagnostic déterministe et rapport | Diagnostiquer | Bench et diagnostic (`bench-diagnostic`) | sous-onglet **Diagnostic** | #12 | bientôt disponible |
| Mode technicien et rédaction IA | Diagnostiquer | Bench et diagnostic (`bench-diagnostic`) | sous-onglet **Technicien** | #13 | bientôt disponible |
| Réglages de performance de Windows | Outils | Optimisation Windows (`optimization`) | — | existant | livrée |
| Animations Windows | Outils | Optimisation Windows (`optimization`) | sous-onglet **Animations** (carte « Animations et effets ») ; l'autre sous-onglet s'appelle **Réglages** | #6 | livrée |
| Nettoyage des caches | Outils | Nettoyage (`cleanup`) | — | existant | livrée |
| Carte de l'espace disque | Outils | Stockage (`storage`) | — | existant | livrée |
| Gestionnaire de disques | Outils | Stockage (`storage`) | sous-onglet **Disques** | #19 | prévue |
| Périphériques et pilotes | Outils | Périphériques (`devices`) | sous-onglets **Périphériques** et **Pilotes** | #18 | bientôt disponible |
| Boîte à outils (téléchargements directs) | Outils | Boîte à outils (`toolbox`) | — | #7 | livrée (expérimental) |
| RAM : répartition, fichier d'échange, mémoire du GPU intégré | Outils | Mémoire (`memory`) | — | #20 | bientôt disponible |
| Réglages de l'app, installations, diagnostic | pied | Paramètres (`settings`) | sous-onglets Général, Installations, Compatibilité de ce PC, Thèmes | existant | livrée |

## Recette : livrer une page « bientôt disponible », ou en ajouter une

1. **Clé** : reprends la constante de `PageKeys` (ne la renomme jamais). Pour une page nouvelle, ajoutes-en une, en
   kebab-case anglais.
2. **Menu** : la ligne de `NavigationMenu.Pages` existe déjà pour une page à venir ; ajuste son titre ou son glyphe si
   besoin. Le titre fait 20 caractères au plus (un test le vérifie), et le glyphe doit exister dans Segoe Fluent Icons.
   Pour une page nouvelle, ajoute sa ligne dans sa section, à sa place. Retire la ligne de la page dans
   `ComingSoonPages`.
3. **ViewModel** : champ dans `MainViewModel`, construit dans son constructeur avec les services partagés injectés,
   puis ajouté au dictionnaire passé à `NavigationMenu.Build` (`[PageKeys.X] = _x`). Une clé inconnue du menu lève dès
   le lancement.
4. **Vue** : une ligne dans la grille de contenu de `MainWindow.xaml` :
   `<views:XView DataContext="{Binding X}" Visibility="{Binding DataContext.CurrentPage.Key, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource StringEqualsToVisibility}, ConverterParameter={x:Static vm:PageKeys.X}}"/>`.
   Squelette d'une vue : ScrollViewer `PagePadding` > StackPanel `PageMaxWidth` > `controls:PageHeader`, puis des
   `Border Style=Card`. Une fonction indisponible affiche un `CalloutWarn` avec un message adapté au PC.
5. **Cycle de vie** : si la page charge ou relève quelque chose, implémente `IPageLifecycle`
   (`[ObservableProperty] private bool isPageShown;` + `OnIsPageShownChanged`).
   - Charge à la **première** ouverture (drapeau « déjà demandé »), jamais dans le constructeur : `MainViewModel`
     construit tout avant d'afficher la fenêtre, y compris au démarrage dans la zone de notification.
   - Pendant le chargement, montre un état en attente (grisé, « Analyse… », `--`), jamais un faux état.
   - Un relevé en direct ne tourne que tant que la page est affichée.
   - La même valeur peut arriver plusieurs fois : ne réagis qu'aux changements.
   - Un **sous-onglet** qui a son propre cycle de vie reçoit `IsPageShown` de sa page : page affichée ET sous-onglet
     choisi (modèle : `AppSettingsViewModel.UpdateSectionShown` → `CompatibilityViewModel`).
6. **Fenêtre cachée** : `IsPageShown` ne remplace pas le mode éco, car une vue masquée reçoit toujours
   `SnapshotUpdated` et `MetricsUpdated`. Si la page doit travailler fenêtre cachée, implémente
   `IBackgroundSensorConsumer` et ajoute-la au tableau d'`UpdateEcoMode`. Sinon, sors tout de suite de ton abonné si
   `MonitoringViewModel.IsBackgroundMode`.
7. **Fermeture** : si le ViewModel est `IDisposable`, ajoute-le à `MainViewModel.Dispose` par `DisposeSafely`, à sa
   place dans l'ordre imposé : Processus et Installations d'abord, puis le relevé (Monitoring), les ventilateurs, le
   GPU, le CPU, l'overlay, et enfin les services (GPU, CPU, capteurs). Ne réordonne pas les étapes existantes.
8. **Diagnostic** : les lignes de la fonction viennent d'un fournisseur `ICompatibilityRowProvider`, rangé dans le
   dossier de la fonction et ajouté à `MainViewModel._compatibilityRows`, jamais d'une méthode de
   `CompatibilityViewModel`.
9. **Modifications de Windows** : si la page modifie Windows durablement, son service implémente
   `ISystemChangeOwner` et s'inscrit à `MainViewModel.SystemChanges`.
10. **README** : une section pour la page, et une mise à jour de « Organisation de l'app » (la page n'est plus
    « bientôt disponible »).
11. **Ce document** : mets à jour l'état de la ligne dans « Où va chaque fonctionnalité ».
12. **Tests** : `NavigationMenuTests` vérifie les clés, les sections et les titres. La logique de la page va en classes
    pures testées.

## Recette : ajouter un sous-onglet à une page

1. Dans le ViewModel de la page, une liste de sections (même forme que `AppSettingsViewModel.Sections`) et
   `[ObservableProperty] SelectedSection`.
2. Dans la vue, un `ListBox Style="{StaticResource PillSelector}"` lié à la liste, puis un panneau par sous-onglet, dont
   la `Visibility` compare `SelectedSection.Key` par `StringEqualsToVisibility`.
3. Un ViewModel à part par sous-onglet qui a sa propre logique : ne fais pas grossir un fichier déjà gros (voir
   « Remarques » de `docs/etudes/cartographie-code-2026-09.md`).
4. Si le sous-onglet charge ou relève quelque chose : `IPageLifecycle`, posé par sa page (étape 5 de la recette
   précédente).
5. Nouveaux styles : dans un ResourceDictionary à part (modèle `Styles/Navigation.xaml`, `Styles/Menus.xaml`),
   fusionné dans `App.xaml` après `Theme.xaml`, qui dépasse déjà 1500 lignes.
