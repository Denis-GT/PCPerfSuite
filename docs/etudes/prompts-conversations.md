# Feuille de route PCPerfSuite en conversations (28/09/2026)

Les 19 fonctionnalités demandées, réparties en 24 conversations Claude Code. Pour chacune : le modèle et l’effort à choisir, ce dont elle dépend, et le prompt à coller. Chaque prompt a été rédigé à partir d’une étude de faisabilité et d’une cartographie du code, puis vérifié contre le code et mis en cohérence avec les autres.

## À trancher une fois pour toutes

- **Licence de PCPerfSuite** (#1) : Le dépôt n’a pas de fichier LICENSE. Or #2 embarque des binaires sous LGPL-2.1 (PawnIO.Modules), et d’autres conversations ajoutent ComputeSharp (#11), le SDK Anthropic (#13) et OpenRGB.NET (#23). La licence choisie décide aussi si une publication en fichier unique reste possible. Recommandation : la choisir maintenant, et ajouter LICENSE et THIRD-PARTY-NOTICES, que chaque conversation complète.
- **Signature de code : quelle entité, quel certificat, quand** (#1) : Sans signature, Smart App Control et SmartScreen bloquent l’app sur le PC d’un client. La décision dit aussi s’il faut un exe séparé pour le bench (#10) et pour le chien de garde (#15, #17). D’après l’étude, un particulier en France n’a pas accès à Azure Artifact Signing en confiance publique. Recommandation : signer au nom d’une société ; d’ici là, un seul exe avec des modes secondaires, donc un seul binaire à signer.
- **Ce que fait l’« IA »** (#1) : La question revient pour la bascule de profils (#9), les verdicts (#12), le compte rendu (#13) et l’OC (#15, #16). Un verdict ou une valeur d’OC choisis par un LLM ne sont ni reproductibles ni testables. Recommandation : un moteur déterministe et explicable décide de tout. Un LLM facultatif rédige seulement le compte rendu à partir des constats, sans jamais changer un verdict ni une valeur.
- **Portée de la règle 5 (contrôleur embarqué des portables en lecture seule)** (#1) : Toutes les vraies bascules GPU dédié / intégré des portables (MUX, mode Eco) passent par une écriture dans le firmware du constructeur, de même que le rétroéclairage des claviers et les modes Silence / Turbo. La mémoire du GPU intégré (#20) est un cas limite. Recommandation : lecture seule en V1. F9 se fait alors en « endormant » le GPU dédié, sans aucune écriture firmware. Toute exception, tu l’écris toi-même dans CLAUDE.md.
- **Ce que contient le mode technicien** (#1) : Un technicien en boutique fait un usage commercial : HWiNFO, OCCT, y-cruncher, Cinebench et FurMark l’interdisent ou demandent une licence payante. Il faut aussi dire ce qu’on s’interdit de lancer sur le PC d’un client. Recommandation : inventaire, bench, diagnostic et rapport. Ni OC, ni scan SMBus pour le RGB, ni opération destructive sur les disques ; pilotes installés un par un.
- **Que se passe-t-il à la fermeture** (#1) : La question est posée par les groupes de profils (#8), la bascule (#9) et les limites par processus (#21). Aujourd’hui les watts, l’OC GPU et les ventilateurs sont rendus d’origine en quittant, alors que les réglages du plan d’alimentation restent. Recommandation : rendre l’origine de tout ce qui est volatil, comme aujourd’hui ; les réglages permanents restent, et l’app le dit.
- **Hébergement du catalogue d’outils et de la table de référence** (#1) : Pour des liens qui téléchargent directement (F10), il faut un catalogue régénéré depuis winget et signé (#7). La table des scores de référence du diagnostic (#12) doit aussi être hébergée quelque part. Recommandation : un dépôt GitHub public et une GitHub Action ; décider qui garde la clé de signature du catalogue.
- **Plateformes et TFM** (#1) : Dynamic Lighting (#24) exige une API WinRT, donc un autre TFM, décision qui touche toute la solution. Recommandation : Windows 10 22H2 et 11 en x64, ARM64 hors V1, aucune télémétrie ; net8.0-windows partout, et le code WinRT dans un assemblage isolé.
- **F6 : « OC sur toutes les marques de cartes AMD, Intel »** (#14) : L’OC des cartes graphiques Radeon et Arc est déjà codé mais n’a jamais été vérifié sur une vraie carte. Si « cartes » veut dire cartes mères, c’est de l’OC CPU, traité en #16. Recommandation : lire « cartes graphiques ». Il faut alors des Radeon et des Arc de test, achetées, prêtées ou confiées à des testeurs.
- **F13 : ce que veut dire « allocation de RAM »** (#20) : L’étude voit cinq sens possibles : répartition de la mémoire façon RAMMap, purge du cache, fichier d’échange, mémoire réservée au GPU intégré, limite par processus. Recommandation : livrer la répartition, le fichier d’échange et la mémoire du GPU intégré AMD, plus la purge en option experte ; la limite par processus relève de #21.

## Vue d'ensemble

| # | Conversation | Fonctionnalités | Modèle | Effort | Taille | Relecture | Dépend de |
|---|---|---|---|---|---|---|---|
| 1 | Navigation par sections et cycle de vie des pages | socle pour toutes les nouvelles pages | Opus 5.5 | xhigh | L | /review | aucune |
| 2 | Fiabiliser le tuning avant toute automatisation | prérequis de F4, F6, F18, F19 (et de la limite de puissance Intel existante) | Opus 5.5 | xhigh | M | /review-max | #1 |
| 3 | Choix de l'écran de l'overlay et service des écrans | F17 (+ service des écrans réutilisé par #17 et #22) | Opus 5.5 | high | M | /review | #1 |
| 4 | Socle de signaux : bridage, journaux Windows, liens PCIe | prérequis de F1, F2, F3, F4 (et utile à F18) | Opus 5.5 | xhigh | M | /review | #1, #2 ; #3 conseillé |
| 5 | Core parking et visuel d'usage par cœur | F5 | Opus 5.5 | high | M | /review | #1 |
| 6 | Gérer les animations Windows | F15 | Sonnet 5 | high | S | /review | #1 |
| 7 | Boîte à outils : liens de téléchargement direct | F10 | Opus 5.5 | xhigh | M | /review-max | #1 |
| 8 | Groupes de profils CPU + GPU + ventilation | F19 | Opus 5.5 | xhigh | L | /review-max | #1, #2, #4 |
| 9 | Profils automatiques selon l'usage | F18 | Opus 5.5 | xhigh | L | /review-max | #8, #2, #4 |
| 10 | Moteur de charge et bench CPU / RAM / disque | F1 (partie 1) | Fable 5.1 | xhigh | XL | /review-max | #1, #4 ; #5, #7, #8 conseillés |
| 11 | Bench GPU et test combiné « alimentation » | F1 (partie 2 : GPU et power) | Opus 5.5 | xhigh | L | /review-max | #10, #4 |
| 12 | Moteur de diagnostic déterministe et rapport | F2 (verdict, causes, refroidissement, alimentation, rapport) | Fable 5.1 | xhigh | L | /review | #4, #10 ; #11 pour le GPU et l’alimentation |
| 13 | Mode technicien et rédaction IA du compte rendu | F3 + partie rédaction IA de F2 | Opus 5.5 | xhigh | L | /review-max | #12, #10, #11 |
| 14 | Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc | F6 | Opus 5.5 | xhigh | L | /review-max | #2 ; #8 conseillé |
| 15 | OC automatique GPU : trois profils sûr / classique / agressif | F4 (partie GPU) | Fable 5.1 | xhigh | XL | /review-max | #2, #8, #4, #11 ; #14 pour AMD et Intel |
| 16 | Curve Optimizer AMD automatique (OC CPU) | F4 (partie CPU AMD) ; OC CPU Intel exclu en V1 | Fable 5.1 | max | L | /review-max | #15, #10, #4, #5 |
| 17 | OC de l'écran (fréquence de rafraîchissement) | F16 | Opus 5.5 | xhigh | L | /review-max | #3, #1 ; #14 et #15 (phase 1) conseillés |
| 18 | Gestionnaire de périphériques et de pilotes | F12 puis F7 | Opus 5.5 | xhigh | XL (deux étapes) | /review-max | #1 ; #3, #7, #4 conseillés |
| 19 | Gestionnaire de disques | F11 | Opus 5.5 | xhigh | L | /review-max | #1 ; #10 conseillé |
| 20 | RAM : répartition, fichier d'échange, mémoire du GPU intégré | F13 | Opus 5.5 | xhigh | M | /review-max | #1 ; #14 conseillé |
| 21 | Limiter CPU / RAM / disque / réseau par processus | F14 | Opus 5.5 | xhigh | L | /review-max | #1, #4 ; #5, #9 conseillés |
| 22 | GPU dédié des portables : l'endormir pour la batterie et la chauffe | F9 | Opus 5.5 | xhigh | L | /review-max | #3, #18 ; #9 conseillé |
| 23 | Éclairage RGB : socle et OpenRGB | F8 (partie 1 : ventilateurs ARGB, carte mère, RAM, GPU, périphériques via OpenRGB) | Opus 5.5 | xhigh | L | /review-max | #1 ; #7 conseillé |
| 24 | Éclairage RGB : Dynamic Lighting, effets et page Éclairage | F8 (partie 2) | Opus 5.5 | high | M | /review | #23 ; #8 conseillé |

## Lot A · Fondations

À faire en premier et dans l’ordre. Ces quatre conversations posent ce que les vingt autres réutilisent : les décisions transverses et docs/decisions.md, la navigation, les garde-fous du tuning, le service des écrans, le journal de session et les signaux de bridage.

### 1. Navigation par sections et cycle de vie des pages

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review
- Dépend de : aucune
- Branche : `feature/navigation-sections`
- Matériel pour vérifier : aucune

````text
# Navigation par sections et cycle de vie des pages (socle)

## 1. Objectif
La feuille de route ajoute une dizaine de pages ou sous-onglets (bench, profils, écrans, pilotes, RGB, GPU portable, boîte à outils…) : la barre latérale ne les tiendra pas. Pose le socle : structure de navigation, clé stable par page, cycle de vie commun, chargement différé, dette de persistance. Pose aussi ce que les 23 conversations suivantes partageront, pour qu'aucune ne le réinvente ni ne me repose la même question : docs/decisions.md (décisions transverses et briques partagées), un dossier de données unique, des lignes de diagnostic ajoutées sans grossir CompatibilityViewModel, un registre des modifications de Windows à rétablir. Seulement la structure, les interfaces et les emplacements, pas le contenu des autres conversations.

## 2. Avant de coder
Lis, si présentes (Grep, pas en entier) : dans docs/etudes/cartographie-code-2026-09.md, la section « Coquille de l'app » (Architecture, Conventions, Remarques) et les « Remarques » des trois autres zones ; dans docs/etudes/etude-faisabilite-2026-09.md, les « Remarques transverses ». Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier : d'abord les décisions transverses (§6 B), puis la navigation (§6 A).

## 3. Ce qui existe
- `MainViewModel` : `NavEntry(Title, Icon, ViewModel)`, 9 entrées dans `NavItems` (≈ l.99-110), `AppSettingsNav` hors liste, `UpdateAttention` (≈ l.163) qui pose `AppSettings.IsPageShown` et `_processes.IsActive`.
- `MainWindow.xaml` (≈ l.107-128) : vues créées une fois, visibles par `CurrentPage.Title` + `StringEqualsToVisibility` + `ConverterParameter` = titre exact ; renommer un titre casse la page sans erreur.
- `NavListItem` (Theme.xaml ≈ l.533, ~40 px par entrée), fenêtre `MinHeight=600`. Sous-onglets : `PillSelector` (Theme.xaml ≈ l.1358), modèle `AppSettingsViewModel.Sections`. Regroupement `CollectionViewSource` + `GroupStyle` : OptimizationView.xaml. Theme.xaml dépasse 1500 lignes : nouveaux styles dans un ResourceDictionary à part (modèle Styles/Menus.xaml).
- `OptimizationViewModel` (LoadCommand, donc powercfg, dans le constructeur) et `StorageViewModel` (`LoadDrivesAsync`) chargent au démarrage, même lancés vers la zone de notification.
- `ComingSoonView` : code mort ; `ComingSoonViewModel` sert encore à l'onglet Thèmes.
- Dossier de données codé en dur (%LOCALAPPDATA%\PCPerfSuite) à quatre endroits : `AppSettingsStore.FilePath` (≈ l.343, static readonly), `CrashLog.FilePath` (App, ≈ l.22), `FanChipDiagnostic.FilePath` (≈ l.21), `AdlxProbeGuard` (≈ l.37).
- `CompatibilityViewModel` (693 lignes, 7 dépendances au constructeur ≈ l.57) : une méthode privée par ligne ; `CompatibilityRow(Title, Status, Detail, IsSupported, IsPersonal)` est un record côté App (≈ l.28 ; double <summary> ≈ l.23-24). Une vingtaine de conversations vont y ajouter des lignes.
- `AppSettings.OriginalPowerValues` : seule mémoire d'origine ; le plan d'alimentation et les tweaks d'Optimisation sont des changements permanents, jamais rendus.

## 4. Approche retenue
1. Sections à en-têtes dans une seule ListBox (grouping sur `NavEntry.Section`) + sous-onglets PillSelector dans les pages qui s'agrandissent. Proposition à me faire valider :
   - Surveiller : Monitoring, Processus (+ limites #21), Overlay (+ écran #3) ;
   - Régler : Processeur (+ Cœurs #5), GPU, Ventilateurs, Profils (#8, #9), Écrans (#17), Éclairage (#23, #24), GPU portable (#22, portables seulement) ;
   - Diagnostiquer : Bench et diagnostic (#10 à #13) ;
   - Outils : Optimisation Windows (+ Animations #6), Nettoyage, Stockage (+ disques #19), Périphériques et pilotes (#18), Boîte à outils (#7) ; Mémoire (#20) à placer.
   Calcule la hauteur disponible à 600 px (marque, pied, bandeau « Droits limités ») : densité compacte, défilement en dernier recours, entrées masquées quand le PC n'a pas la fonction.
2. Clé stable : `NavEntry.Key` (constantes d'une classe `PageKeys`), comparée par `x:Static`. Vues toujours créées une seule fois (historique des Sparkline).
3. `IPageLifecycle { bool IsPageShown { set; } }` posée pour chaque entrée, Paramètres compris, dans `UpdateAttention` (page courante ET fenêtre visible) : Paramètres et Processus y passent, Optimisation et Stockage chargent à la première ouverture.
4. docs/navigation.md : tableau fonctionnalité → page ou sous-onglet → n° de conversation, et recette « ajouter une page » (clé, NavEntry, vue, IPageLifecycle, `UpdateEcoMode` si elle tourne fenêtre cachée, place dans `MainViewModel.Dispose`, fournisseur de lignes de diagnostic, README), que les conversations suivantes liront.
5. `AppSettingsStore.Update()` au lieu de Load()+Save() : `OverlayViewModel.Persist`, `AppSettingsViewModel.OnMinimizeToTrayOnCloseChanged`, setter `MonitoringViewModel.RefreshMs`, `OnMyMetricsSelectionChanged`, `SensorGroupCadenceViewModel.ApplyAndSave`, `MainWindow.ShowTrayHintOnce`, `CleanupViewModel.OnIncludeRecycleBinInCleanAllChanged`, `FanCurvesViewModel.Persist`, `ProcessesViewModel.Persist`.
6. docs/decisions.md, que chaque conversation lira avant de coder :
   - « Décisions transverses » : mes réponses au §6 B, datées, avec les conversations concernées ;
   - « Briques partagées » : brique → conversation propriétaire → emplacement et nom proposés → état (prévue, livrée) ; chaque conversation mettra sa ligne à jour. Contenu de départ :
     - #1 : PageKeys, IPageLifecycle, AppDataPaths, ICompatibilityRowProvider, ISystemChangeOwner ;
     - #2 : GpuIdentity (marque, nom, identifiants PCI si lus, LUID facultatif ; Core/Hardware/Gpu) et sécurité thermique en logique pure (Core/Safety) ;
     - #5 : CpuTopology (CPU sets : cœur, fils SMT, L3/CCD, classe d'efficacité ; logique pure, Core/Hardware/Cpu) ;
     - #3 : DisplayTopology (Core/Hardware/Displays), avec le filtre des fenêtres du shell pour « l'écran du jeu » ;
     - #7 : OfficialInstaller paramétré par source, dossier %ProgramData%\PCPerfSuite sécurisé (ACL restreinte, jonctions refusées) ;
     - #8 : API appliquer/capturer des 3 onglets, orchestrateur, bail de réglage TuningLease (un seul pilote automatique des réglages CPU/GPU/ventilateurs à la fois) ;
     - #9 : ApplicationMatch (règle par application : chemin complet normalisé, éditeur facultatif) et lecteur du premier plan (Core/Processes) ;
     - #4 : SessionJournal et qualification d'incident au lancement (Core/Safety), étape StartupRecovery d'App.OnStartup, lecteur CfgMgr32 (Core/Devices, graine du service de #18), bail de cadence ;
     - #10 : modes secondaires de l'exe, lus avant le mutex d'instance unique (liste fermée ; --demarrage-windows inchangé), moteur de charge, noyaux CPU vérifiés ;
     - #11 : noyaux GPU vérifiés ; #12 : DiagnosticReport ;
     - #14 : sessions NVAPI, ADLX et IGCL partagées, à compteur de références ;
     - #15, phase 1 : chien de garde (--watchdog) et essai à retour automatique TrialGuard (Core/Safety), repris par #17 (ou créés par #17 s'il passe avant, mêmes noms et emplacement) ;
     - #18 : service des périphériques (Core/Devices) ; #23 : socle d'éclairage (Core/Hardware/Lighting).
7. Dossier de données unique : `AppDataPaths` (Core/Environment, namespace PCPerfSuite.Core.SystemInfo comme ses voisins), racine injectable (%LOCALAPPDATA%\PCPerfSuite aujourd'hui) et sous-dossiers nommés ; les quatre emplacements du §3 y passent. Aucun champ statique ne doit figer le chemin avant que la racine soit connue : #13 y ajoutera la racine portable (clé USB).
8. Lignes de diagnostic : `CompatibilityRow` passe dans Core (Core/Compatibility), avec `ICompatibilityRowProvider` et une liste de fournisseurs que CompatibilityViewModel affiche après ses lignes actuelles. Celles-ci ne bougent pas (#12 les déplacera). Une nouvelle ligne = un fournisseur dans le dossier de sa fonction, jamais une 8e dépendance ni une méthode de plus. Un fournisseur qui lève donne une ligne « lecture impossible » (règle 2) ; « Copier le rapport » inclut ces lignes, IsPersonal respecté.
9. Registre des modifications de Windows : `ISystemChangeOwner` (Id, titre, HasChanges, Describe(), RestoreAll() → résultat typé) et son registre (Core/SystemChanges). Chaque fonction qui modifie Windows durablement (#5, #6, #8, #17 à #22) s'y inscrira et gardera elle-même son origine ; #13 s'en servira pour « tout rétablir » avant de rendre un PC client. Ici : l'interface, le registre, leurs tests ; rien d'inscrit.

## 5. Pièges
- `Update()` garde le verrou pendant le lambda : seulement des mutations dedans. `RefreshMs` : `SetManualMsWithoutSaving(clamped, settings)` passe par `ApplyTo`, qui touche aussi le matériel (`SetManualInterval`) et l'interface ; sépare l'inscription du réglage des appels matériels (`SetBaseInterval`, `cadence.Apply`), faits hors du verrou.
- `IsPageShown` ne remplace pas le mode éco : une vue repliée reçoit toujours `SnapshotUpdated`.
- Ne change ni l'ordre de `MainViewModel.Dispose` ni la liste d'`UpdateEcoMode` (#2 y ajoutera le GPU).
- Propriété sans setter : liaison `Mode=OneWay`, sinon exception WPF.
- `AppSettingsStore.FilePath` est un static readonly lu tôt : AppDataPaths doit être prêt avant, et testable (racine injectée).
- Les fournisseurs de lignes tournent dans `Refresh` (appelé sur MetricsUpdated) : rapides, sans WMI ni réseau sur le thread d'interface.

## 6. Questions à me poser
A. Navigation
1. Structure : sections + sous-onglets (recommandé), sous-onglets seuls, ou sections repliables ?
2. Pages « bientôt disponible » : non, supprimer ComingSoonView (recommandé pour une app publique qui ne promet rien), ou la brancher ?
3. Emplacement de chaque future fonctionnalité (liste du §4), surtout Mémoire et Overlay.
B. Décisions transverses (consignées dans docs/decisions.md ; chacune évite une question répétée plus loin)
4. Licence de PCPerfSuite : #2 embarque des binaires LGPL-2.1 (PawnIO.Modules), puis viennent ComputeSharp (#11), le SDK Anthropic (#13), OpenRGB.NET (#23) ; le dépôt n'a pas de LICENSE. Recommandé : trancher maintenant, ajouter LICENSE et un THIRD-PARTY-NOTICES que chaque conversation complète.
5. Signature de code (entité, certificat, calendrier) : elle conditionne le worker de bench (#10), le chien de garde (#15, #17) et le mode technicien (#13). Recommandé : via une société ; d'ici là, un seul exe à modes secondaires, donc un seul binaire à signer.
6. Sens de « IA » : recommandé, un moteur déterministe et explicable décide de tout (bascule #9, verdicts #12, OC #15/#16) ; un LLM facultatif ne fait que rédiger (#13), sans jamais changer un verdict ni une valeur.
7. Portée de la règle 5 : modes de performance constructeur des portables (Silence/Turbo, pour #8, #9, #10), mode GPU Eco/MUX (#22), rétroéclairage clavier par le contrôleur embarqué (#23), taille de la mémoire du GPU intégré écrite par le pilote AMD (#20). Recommandé : lecture seule en V1 ; toute exception, c'est moi qui l'écris dans CLAUDE.md.
8. Public et mode technicien : particuliers et techniciens (F3), donc badge « licence pro requise » sur les outils tiers (#7). Fonctions du mode technicien : recommandé, inventaire, bench, diagnostic et rapport ; ni OC (#15, #16), ni RGB ni scan SMBus (#23), ni opération destructive sur les disques (#19) ; pilotes un par un (#18).
9. À la fermeture : recommandé, rendre l'origine des réglages volatils (watts, OC GPU, ventilateurs, limites par processus), comme aujourd'hui ; les réglages permanents restent, annoncés.
10. Hébergement : un dépôt GitHub public pour le catalogue d'outils signé (#7) et la future table de scores de référence (#12) ? Qui garde la clé de signature ?
11. Plateformes : recommandé, Windows 10 22H2 et 11 en x64, ARM64 hors V1, aucune télémétrie ; TFM net8.0-windows partout, WinRT dans un assemblage isolé chargé à la demande (#24).

## 7. Périmètre
Dans : structure, clé, cycle de vie, chargement différé, persistance, docs/navigation.md, docs/decisions.md, AppDataPaths, fournisseurs de lignes, registre des modifications (interfaces), README. Hors : contenu des nouvelles pages (#2 à #24) ; CpuControlViewModel et GpuControlViewModel (#2 « Fiabiliser le tuning avant toute automatisation ») ; WindowsPerformanceSettingsService, y compris son `RememberOriginalValue` (#5 « Core parking et visuel d'usage par cœur », #6 « Gérer les animations Windows ») ; déplacement des lignes existantes du diagnostic (#12 « Moteur de diagnostic déterministe et rapport »).

## 8. Livrables et méthode
Branche feature/navigation-sections depuis develop ; commits en français au présent, comme l'historique ; tests xUnit sur la logique pure (page affichée, regroupement, clés uniques, AppDataPaths, fournisseur de lignes qui lève, registre des modifications) ; `dotnet build -c Release` et `dotnet test` verts. README : présentation pour tous les PC (l.3-5 parle de ta config), contrôle GPU qui n'est plus « NVIDIA seulement » (≈ l.324 : AMD et Intel expérimentaux), section « Organisation de l'app ». Ni ligne de diagnostic ni « expérimental » : rien de matériel. Relecture `/review` avant fusion dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
- Fenêtre à 600 px de haut, échelle 100 % et 150 % : toutes les entrées accessibles.
- Changer de page garde les courbes du Monitoring ; Processus cesse son relevé page quittée.
- Démarrage vers la zone de notification pas plus lent ; Optimisation et Stockage se remplissent à la première ouverture.
- Réglages, journal de plantage et diagnostic des ventilateurs lus et écrits au même endroit qu'avant.
````

### 2. Fiabiliser le tuning avant toute automatisation

- Modèle / effort : **Opus 5.5 / xhigh** · taille M · relecture /review-max
- Dépend de : #1
- Branche : `fix/fiabilisation-tuning`
- Matériel pour vérifier : Machine de jeu de Denis (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI) indispensable

````text
# Fiabiliser le tuning avant toute automatisation (prérequis de F4, F6, F18, F19)

## 1. Objectif
Avant l'OC automatique (F4), l'OC AMD/Intel (F6) et les profils (F18, F19), le réglage manuel doit être sûr et vérifiable : limite de puissance Intel qui marche vraiment, garde-fous GPU au niveau du CPU, relecture de ce que la carte a retenu.

## 2. Avant de coder
Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées. Lis, si présentes : la section F4 et les « Remarques transverses » qui suivent F6 dans docs/etudes/etude-faisabilite-2026-09.md ; la section « Tuning » de docs/etudes/cartographie-code-2026-09.md (Architecture, Conventions, F4 › Pièges, Remarques). Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.

## 3. Ce qui existe
- `PawnIoDriver.TryLoadModule`/`ReadModuleBlob` : modules lus dans les ressources de LibreHardwareMonitorLib 0.9.6 (`LibreHardwareMonitor.Resources.PawnIo.<nom>.bin`) ; `PawnIoModule.TryExecute`, `DescribeError`.
- `IntelPowerLimitBackend` : écrit 0x610 par `ioctl_write_msr`, puis relit ; `CpuCapability.ReadOnly(raison)` sert déjà au verrou BIOS.
- `CpuControlService.NoteTemperature` (98 °C pendant 15 s → `TryRestoreDefaults` + `EmergencyRestored`) ; `CpuControlViewModel` (`IBackgroundSensorConsumer`, `OnPowerModeChanged` ≈ l.352).
- `GpuControlService` : Try*, `GetOverclock`, `RestoreOverclockDefaults` (void), `KeepOverclockOnExit`. `GpuControlViewModel` : `OnSnapshotUpdated`, `ShouldReapplyAtStartup`. À 88 °C le ventilateur GPU passe à 100 %, mais l'OC reste. Températures : `HardwareSnapshot.Gpu.CoreTempC`, `HotSpotTempC`.

## 4. Approche retenue
(a) Vérifié en lisant les ressources de LHM 0.9.6 : `IntelMSR.bin` n'expose que `ioctl_read_msr` (RyzenSMU, lui, écrit). PL1/PL2 échoue donc très probablement sur tout Intel (à confirmer sur ta machine). L'écriture MSR (0x610, mailbox OC 0x150) n'arrive qu'avec PawnIO.Modules 0.2.4 (17/03/2026, LGPL-2.1, signés par namazso) ; aucune release LHM depuis 0.9.6.
- Fonctions d'un module = chaînes ASCII `ioctl_[a-z0-9_]+` du blob (méthode qui a donné ce constat) : fonction pure, testée. Sans `ioctl_write_msr`, le backend Intel passe en `CpuCapability.ReadOnly`, avec une raison qui accuse le module, pas l'app ni le BIOS.
- Si on embarque : .bin officiels ≥ 0.2.4 en EmbeddedResource de Core, pour IntelMSR seulement (le RyzenSMU de LHM convient ; ne touche pas aux modules que LHM charge pour ses capteurs), repli sur LHM, SHA-256 figé dans un test, licence et sources au README. Vérifie la liste blanche d'écriture dans IntelMSR.p du tag retenu avant de t'y fier. PawnIO ne charge que des modules signés : impossible d'écrire les nôtres. Ne télécharge rien sans me le demander.
- Corrige les commentaires faux : PawnIoDriver (« liste fermée de registres », « rien de plus à livrer »), IntelPowerLimitBackend (0x601 « le module en autorise l'écriture »).
(b) Sécurité thermique GPU : logique pure dans Core/Safety (seuil, durée, heure injectée), dans de nouveaux fichiers, partageable avec le CPU et réutilisée par #10, #11, #15, #16 ; armée seulement si l'app a relevé quelque chose (décalage positif, puissance > 100 %, température ou tension) : elle appelle `RestoreOverclockDefaults`, relit `GetOverclock` et prévient ; `Gpu` peut être null : traite le cas « température absente ». `GpuControlViewModel` devient `IBackgroundSensorConsumer` (groupe Gpu tant qu'elle est armée), entre dans `UpdateEcoMode` et nourrit la sécurité avant de sortir en mode éco. Au réveil (`PowerModeChanged` Resume, à ajouter côté GPU) : relire `GetOverclock`, réappliquer seulement si `ShouldReapplyAtStartup` ; désabonnement dans Dispose. `SettingsMatchCurrentGpu` ne compare que la marque : introduis `GpuIdentity` (Core/Hardware/Gpu : marque, nom, identifiants PCI si lus ; LUID facultatif) et fais comparer `SettingsMatchCurrentGpu` sur elle (le nom de la carte s'ajoute ainsi à la marque) ; #8, #11, #14, #15 et #22 la compléteront.
(c) `CpuControlViewModel.OnPowerModeChanged` ne fait rien si « Appliquer au démarrage » est décoché : toujours relire les limites au réveil (sous `_suppressApply`), réappliquer seulement si coché.
(d) Méthode Core « appliquer puis relire `GetOverclock` » qui rend demandé / retenu par réglage (comparaison pure, testée) ; l'instantané signale les plages de repli de NVAPI (`ReadDelta`, -500/+1000 et -1000/+2000 MHz, pas les vraies limites). Passe les Load()+Save() de CpuControlViewModel et GpuControlViewModel à `AppSettingsStore.Update()`.

## 5. Pièges
- Les Try* GPU (NVAPI `SetPerformanceStates20`, ADLX `TrySetInt`) renvoient true sans vérifier.
- RTX 50 : hot spot probablement absent (à vérifier) ; la sécurité doit tenir sur la température cœur.
- Ne jamais contourner `RiskAccepted` ni `IntelOverclockWaiverAccepted`.
- Au réveil, le pilote GPU peut ne pas être prêt : court délai avant relecture. Incertains : décalages NVAPI après un TDR, persistance ADLX et IGCL.
- 13e/14e gén. Intel (dont le 14600K) : tensions et températures élevées aggravent le « Vmin shift » ; teste en baissant PL1, pas en la relevant.

## 6. Questions à me poser
1. Embarquer PawnIO.Modules ≥ 0.2.4 nous-mêmes (recommandé) ou attendre une LHM plus récente ?
2. Seuils de la sécurité GPU (proposé : cœur ≥ 90 °C pendant 15 s, le ventilateur étant à 100 % dès 88 °C ; hot spot ≥ 105 °C s'il est lu) ?
3. PL1/PL2 non modifiables : lecture seule avec message (recommandé) ou carte masquée ?

## 7. Périmètre
Hors : validation de l'OC Radeon et Arc (#14 « Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc ») ; témoin de plantage et prudence après un arrêt inattendu : journal de session (#4), prudence d'« Appliquer au démarrage » des groupes (#8) et des profils d'OC automatique (#15 « OC automatique GPU : trois profils sûr / classique / agressif ») ; #16 « Curve Optimizer AMD automatique (OC CPU) » ; API appliquer / capturer des profils (#8 « Groupes de profils CPU + GPU + ventilation ») ; #4 « Socle de signaux : bridage, journaux Windows, liens PCIe », qui réutilisera la liste d'ioctl.

## 8. Livrables et méthode
Branche fix/fiabilisation-tuning depuis develop ; commits en français au présent, comme l'historique ; tests xUnit (liste d'ioctl, choix du module, sécurité thermique, `GpuIdentity`, demandé / retenu) ; `dotnet build -c Release` et `dotnet test` verts. Diagnostic « Paramètres › Compatibilité de ce PC », par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : ligne « Modules PawnIO » (source, version, fonctions de chaque module chargé), ligne « Sécurité thermique GPU » (armée, seuil, température suivie). docs/decisions.md : lignes `GpuIdentity` (Core/Hardware/Gpu) et sécurité thermique (Core/Safety), avec ce qu'elles exposent. README : Processeur, Overclocking GPU, pilote PawnIO. Règles 2, 3, 4 et 6 : PL1/PL2 par le nouveau module reste « expérimental » tant que ce n'est pas vérifié sur une vraie machine. Relecture `/review-max` avant fusion dans develop, jamais sur master.

## 9. À vérifier sur ta machine (i5-14600K, RTX 5070 Ti, B760)
1. Avant correctif : baisser PL1 dans Processeur, noter le message exact.
2. Après : PL1/PL2 appliqués et relus identiques dans HWiNFO ; « Limites d'origine » les rend ; le diagnostic liste les fonctions d'IntelMSR. Toujours refusé : noter le code d'erreur et l'état VBS.
3. OC GPU léger, veille, réveil : l'onglet affiche les vraies valeurs.
4. OC actif, app dans la zone de notification : la sécurité GPU reçoit toujours la température.
````

### 3. Choix de l'écran de l'overlay et service des écrans

- Modèle / effort : **Opus 5.5 / high** · taille M · relecture /review
- Dépend de : #1
- Branche : `feature/overlay-ecran`
- Matériel pour vérifier : PC à deux écrans d'échelles différentes (ex. 100 % et 150 %)

````text
# Choix de l'écran de l'overlay et service des écrans (F17)

## 1. Objectif
Tes mots : « Pouvoir choisir l'écran pour l'overlay (canal fenêtre pcperfsuite). » Au passage, créer dans Core le service d'inventaire des écrans que réutiliseront l'OC d'écran (#17), le GPU dédié des portables (#22) et, pour le premier plan, les profils automatiques (#9).

## 2. Avant de coder
Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées. Lis les sections « F17 » des deux études de docs/etudes/ si présentes, et docs/navigation.md s'il existe (#1). Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.

## 3. Ce qui existe
- `OverlayWindow` (sans bordure, Topmost, `ClickThroughWindow.Apply`) : `Reposition()` calcule Left/Top en DIP depuis `SystemParameters.PrimaryScreenWidth/Height` (origine supposée en 0,0), l'ancrage `OverlayAnchor` (3×3) et `MarginX/MarginY` ; appelé sur SizeChanged, Loaded et `OverlayViewModel.LayoutChanged`. Rien sur un changement d'écran ou de DPI, alors que l'app est PerMonitorV2.
- `TopmostKeeper` : hook `EVENT_SYSTEM_FOREGROUND` (`OnForegroundChanged`), P/Invoke `SetWindowPos`. `TrayIcon.DeviceToDip` : pixels vers DIP.
- `OverlayAppearanceSettings` (Anchor, MarginX, MarginY) ; `OverlayAppearanceViewModel.WriteTo`, `OnAnchorChanged → Changed()`. `OverlayViewModel.SyncWindow` crée la fenêtre à chaque activation de l'overlay ; `Persist` passe à `Update()` avec #1 (sinon fais-le). OverlayView.xaml : bloc « Canal fenêtre », libellé « Position sur l'écran principal » ; liste : style `GlassComboBox`.
- `CompatibilityRow.IsPersonal` ne masque que le Statut dans le rapport copié.

## 4. Approche retenue
1. Nouveau dossier Core/Hardware/Displays (`DisplayTopology` + modèles), best-effort, sans admin : `GetDisplayConfigBufferSizes` + `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` (réessayer sur ERROR_INSUFFICIENT_BUFFER) ; `DisplayConfigGetDeviceInfo` GET_SOURCE_NAME (viewGdiDeviceName), GET_TARGET_NAME (monitorFriendlyDeviceName, monitorDevicePath, edidManufactureId, edidProductCodeId, outputTechnology, connectorInstance), GET_ADAPTER_NAME ; LUID de l'adaptateur, résolution et fréquence du chemin ; `EnumDisplayMonitors` + `GetMonitorInfoW(MONITORINFOEXW)`, rapprochés par szDevice ; `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`. rcMonitor est en pixels physiques. Écrans dupliqués : un HMONITOR, plusieurs cibles, « A + B (dupliqués) ». Vérifie la disposition des structures avant de t'y fier. Pas de WinForms `Screen`. Liste relue à l'ouverture de l'onglet.
2. Identité enregistrée { chemin, EDID fabricant/produit, nom convivial }, jamais `\\.\DISPLAYn` (instable). Résolution : chemin exact, sinon EDID, deux écrans identiques départagés par `WmiMonitorID.SerialNumberID` (root\wmi) ; nom vide : « Écran 2 (1920×1080) ».
3. Trois modes, stockés en chaîne : écran principal ; cet écran (liste + bouton « Identifier » qui affiche un numéro sur chaque écran) ; écran du jeu (premier plan via le hook existant). Le filtre des fenêtres de PCPerfSuite et du shell (Shell_TrayWnd, Shell_SecondaryTrayWnd, Progman, WorkerW, menu Démarrer) et la résolution fenêtre → écran (`MonitorFromWindow(MONITOR_DEFAULTTONEAREST)`) vont dans `DisplayTopology` (Core), en fonctions publiques testées : le classifieur de #9 (plein écran, premier plan) les réutilisera.
4. Placement : calcul pur dans Core/Overlay (ancrage, marges en DIP × échelle de l'écran cible, taille ActualWidth × échelle, bornes en pixels, coordonnées négatives), appliqué par `SetWindowPos(HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE|SWP_NOACTIVATE)` après SourceInitialized. Replacer sur `DpiChanged` (arrêter dès que la position ne bouge plus) et `SystemEvents.DisplaySettingsChanged` (se désabonner dans OnClosed) ; retour automatique quand l'écran choisi revient.

## 5. Pièges
- Left/Top en DIP sont faux sur un écran d'une autre échelle (dotnet/wpf #4127) : la conversion suit le DPI de l'écran où la fenêtre se trouve encore.
- Boucle DpiChanged ↔ SizeChanged si la convergence n'est pas vérifiée ; overlay hors écran après un débranchement non géré.
- `OverlayAnchor` est persisté en nombre : n'insère rien ; nouveaux champs en chaînes (null = écran principal, anciens fichiers compatibles).
- Numéro de série EDID : ni dans le Statut ni dans le Détail d'une ligne du diagnostic.
- Code des écrans hors des fichiers Overlay déjà gros (OverlayView.xaml ≈ 450 lignes, OverlayViewModel.cs ≈ 430).
- Canal RTSS non concerné ; plein écran exclusif : overlay fenêtre invisible (limite connue).

## 6. Questions à me poser
1. Mode par défaut : écran principal (recommandé, comportement actuel) ou écran du jeu ?
2. Un seul overlay (recommandé) ou un par écran ?
3. Ancrage et marges communs (recommandé) ou par écran ?
4. Écran choisi débranché : repli sur le principal avec message (recommandé) ou overlay masqué ?

## 7. Périmètre
Hors : fréquence et OC d'écran (#17 « OC de l'écran (fréquence de rafraîchissement) »), #22 « GPU dédié des portables : l'endormir pour la batterie et la chauffe », #9 « Profils automatiques selon l'usage » (classifieur plein écran / premier plan), canal RTSS, structure de navigation (#1). Le service expose ce dont #17, #22 et #9 auront besoin (LUID, sortie, EDID ; filtre du shell, fenêtre → écran), sans leurs fonctions.

## 8. Livrables et méthode
Branche feature/overlay-ecran depuis develop ; commits en français au présent, comme l'historique ; tests xUnit du placement (9 ancrages, 100 et 150 %, écran à gauche en négatif), de la résolution d'identité et du filtre des fenêtres du shell ; `dotnet build -c Release` et `dotnet test` verts. Diagnostic : ligne « Écrans » par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel (nom, résolution, fréquence, échelle, sortie, GPU qui le pilote), règles 2 à 4 de CLAUDE.md ; règle 6 pour les cas non vérifiés (écrans dupliqués, docks sans EDID). docs/decisions.md : mets à jour la ligne `DisplayTopology` (Core/Hardware/Displays : inventaire, LUID, sortie, EDID, filtre des fenêtres du shell, résolution fenêtre → écran). README : section Overlay. Relecture `/review` avant fusion dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine (deux écrans d'échelles différentes)
1. Chaque ancrage sur l'écran secondaire, marges identiques à l'œil.
2. Écran secondaire placé à gauche du principal.
3. Débrancher puis rebrancher l'écran choisi.
4. Écran du jeu : jeu fenêtré sur l'écran 2, clic sur la barre des tâches sans saut.
5. Changer l'échelle dans Windows pendant que l'overlay tourne.
````

### 4. Socle de signaux : bridage, journaux Windows, liens PCIe

- Modèle / effort : **Opus 5.5 / xhigh** · taille M · relecture /review
- Dépend de : #1, #2 ; #3 conseillé
- Branche : `feature/signaux-bridage`
- Matériel pour vérifier : machine de Denis ; un AMD Ryzen pour la PM table

````text
# Socle de signaux : bridage, journaux Windows, liens PCIe (socle de F1 à F4)

## 1. Objectif
Prérequis en lecture seule de F1 à F4, et de la prudence au démarrage des profils (F18, F19). Ma demande : l'IA doit « identifier si c'est dû à un mauvais refroidissement, un manque de puissance fourni par l'alimentation ». D'abord savoir si le CPU ou le GPU se bride et pourquoi, ce que Windows a journalisé, et si les liens PCIe tournent à leur vitesse. S'y ajoutent le journal de session et la qualification d'incident au lancement : #8, #9, #10, #11, #14, #15, #16 et #17 les réutiliseront au lieu d'inventer chacun leur marqueur. Franchement : aucun signal ne mesure l'alimentation elle-même, on n'aura que des indices.
Ordre : cette conversation clôt le lot A, après #3 (service des écrans) et avant les groupes de profils (#8).

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis, si présents (Grep) : « ### F2 » et les deux « Remarques transverses » (F1-F3, F4-F6) de docs/etudes/etude-faisabilite-2026-09.md, et les deux « ### F2 » de docs/etudes/cartographie-code-2026-09.md.
- Regarde ce qu'ont livré :
  - #2 (Fiabiliser le tuning avant toute automatisation) : ioctl et version des modules PawnIO embarqués, GpuIdentity ;
  - #3 : DisplayTopology ;
  - #1 : AppDataPaths, fournisseurs de lignes.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.

## 3. Existant à réutiliser
- `PawnIoDriver.TryLoadModule`, `PawnIoModule.TryExecute` (verrouillé) ; `IntelPowerLimitBackend.TryReadMsr` (0x601, 0x606, 0x610, 0x614 déjà lus) ; `AmdSmuBackend.ReadLimitsFromPmTable` (PciGuard, IsPlausibleWatts ; 16 qwords lus, version de table jamais vérifiée).
- `PdhCounterSampler` (compteurs en anglais), `BatteryReader`, `MemoryModuleReader` (Speed, ConfiguredClockSpeed), `MachineInfo.Current`. TjMax n'est lu nulle part (« Distance to TjMax » exclu exprès, HardwareMonitorService ≈435).
- GPU : `IGpuTuningBackend.GetActiveLimit` (null chez ADLX et IGCL), lu 1 fois/s par `GpuControlViewModel.RefreshActiveLimit`, hors HardwareSnapshot. NvAPIWrapper 0.8.1.101 : `GPUPerformanceControl.CurrentPerformanceDecreaseReason`.
- `SensorReadSchedule` (ManualInterval = réglage utilisateur, plafond GPU 750 ms) ; modèle de lecteur dédié : Memory/SystemMemoryReader.cs.
- Aucun journal de session ni lecture du journal d'événements : seul AdlxProbeGuard pose un témoin disque autour de l'initialisation ADLX. App.OnStartup n'a aucune étape de récupération.

## 4. Approche retenue (rien d'écrit dans le matériel)
- Intel, liste blanche IntelMSR :
  - 0x1B1 (bits 0 thermique, 2 PROCHOT, 10 puissance) ;
  - 0x19C par cœur, sur un thread épinglé (bits 0, 2, 10, 12 courant, 14 autre domaine) ;
  - 0x1A2 TjMax, 0xCE, 0x1AD (hybrides : à vérifier), 0x613 (incertain), APERF 0xE8 / MPERF 0xE7 ;
  - 0x64F et 0x6B0/0x6B1 refusés par le module.
  Bits « log » jamais effacés, bits d'état seulement. Vérifie la liste blanche du module embarqué.
- AMD : PM table seulement pour les versions cartographiées (ryzen_monitor, ZenStates : PPT, TDC, EDC, THM), N/D ailleurs. ZenStates-Core est GPL-3.0 : reprendre les offsets, jamais le code.
- Sans pilote : PDH « % Processor Performance », « % Performance Limit », « Performance Limit Flags » (non documentés : indice), événement Kernel-Processor-Power 37.
- GPU :
  - NVAPI PerfDecreaseInfo (ThermalProtection, PowerControl, AC_BATT, InsufficientPower) + GetActiveLimit ;
  - NVML clocks event reasons en best-effort (limité sur GeForce) ;
  - IGCL : ctlEnumFrequencyDomains → ctlFrequencyGetState().throttleReasons (dont PSU_ALERT) et ctlPowerTelemetryGet ; bindings à ajouter dans IgclNative, branchés dans IgclGpuBackend.GetActiveLimit ;
  - ADLX : N/D « limite du pilote ».
- Bail de cadence par demandeur (IDisposable) dans HardwareMonitorService/SensorReadSchedule : la cadence la plus rapide l'emporte, y compris sur ManualInterval et le plafond GPU, sans les modifier. MSR par cœur à 2 Hz au plus. Lecteurs en fichiers dédiés, hors ReadCpu/ReadGpu.
- Journaux (EventLogReader + XPath, 30 jours) : Kernel-Power 41 (BugcheckCode 0 et PowerButtonTimestamp 0 = coupure brutale), BugCheck 1001, EventLog 6008, WHEA-Logger 17/18/19/47, Display 4101, disk 7/51/153. Classement pur `IncidentClassifier`, à partir d'une heure donnée :
  - arrêt brutal (41 avec BugcheckCode 0 et PowerButtonTimestamp 0) ;
  - écran bleu (41 avec code, 1001) ;
  - arrêt inattendu (6008), TDR (4101), erreur matérielle (WHEA), disque.
- Journal de session `SessionJournal` (Core/Safety) :
  - entrées { Id, composant, action, valeurs, heure UTC, état EnCours | Terminé | Échoué, cause } ;
  - ajoutées en fin de fichier avec WriteThrough et Flush(true), dans le dossier de données (AppDataPaths de #1) ;
  - lecture tolérante : une fin tronquée est ignorée.
- Étape `StartupRecovery` dans App.OnStartup, avant la construction de MainViewModel :
  - elle lit les entrées restées EnCours et les qualifie par IncidentClassifier depuis leur heure ;
  - elle appelle, dans un ordre fixe, les gestionnaires que les autres conversations inscriront : retour d'origine de l'OC (#15, #14) en premier, puis essai d'écran (#17), test combiné (#11), bench (#10), groupes (#8), bascule (#9).
  Ici : journal, classifieur, étape, tests ; aucun gestionnaire matériel. Documente nom et format dans docs/decisions.md : aucun autre « marqueur » ni « témoin de session » ne doit apparaître.
- Lien PCIe courant/max (DEVPKEY_PciDevice_CurrentLinkSpeed/Width, MaxLinkSpeed/Width, CM_Get_DevNode_PropertyW) du GPU et des NVMe. Le lecteur CfgMgr32 se place dans Core/Devices, comme graine du service des périphériques de #18, qui l'étendra sans le dupliquer. #10/#11 l'appelleront sous charge ; au repos, afficher « lien au repos », jamais un verdict. Radeon à commutateur interne : lire le pont amont (à vérifier).
- Contrôles :
  - écran d'un bureau branché sur l'iGPU, d'après DisplayTopology de #3 (adaptateur de chaque écran actif). Si #3 n'est pas fusionné : IDXGIAdapter::EnumOutputs, dans un lecteur que #3 remplacera ;
  - microcode Intel 13e/14e gen bureau < 0x12F (« Update Revision », format à vérifier) ;
  - portable sur secteur qui se décharge ; VBS (Win32_DeviceGuard) ;
  - RAM ConfiguredClockSpeed < Speed (XMP non détectable de façon fiable) ;
  - outils concurrents actifs.

## 5. Pièges
- Appels NVAPI de GpuControlService non verrouillés : verrou obligatoire depuis le thread de relevé (#14 le déplacera dans la session NVAPI partagée). Un seul appelant de GetSnapshot.
- Perf cap « Power » NVIDIA = limite de la carte, pas l'alimentation.
- Règle 3 : chaque N/D dit sa cause (matériel/pilote, marque non prise en charge, ou sans administrateur / PawnIO). Journaux : filtrer les noms d'applications.
- Le journal de session doit survivre à une coupure : écritures courtes en ajout, jamais de réécriture complète pendant un test, aucun nom d'application ni de fichier dedans.
- StartupRecovery tourne avant l'interface : aucune fenêtre modale. Un gestionnaire qui lève est journalisé et n'empêche ni les autres ni le lancement (règle 2).

## 6. Questions à me poser
1. Bridage en direct dans Monitoring et l'overlay, ou réservé au bench et au diagnostic ?
2. Proposer une PR à PawnIO.Modules pour lire 0x64F/0x6B0/0x6B1 ?
3. Périmètre des contrôles de configuration ?
4. Rétention du journal de session (recommandé : 30 jours) ?

## 7. Périmètre
Dans : lecteurs, bail de cadence, journal d'événements et IncidentClassifier, SessionJournal et StartupRecovery, liens PCIe, contrôles, lignes de diagnostic. Hors :
- bench (#10 Moteur de charge et bench CPU / RAM / disque, #11 Bench GPU et test combiné « alimentation ») ;
- verdicts et causes (#12 Moteur de diagnostic déterministe et rapport) ;
- vue par cœur (#5 Core parking et visuel d'usage par cœur) ;
- gestionnaires de retour d'origine (#8, #9, #10, #11, #14, #15, #17) ;
- service complet des périphériques (#18 Gestionnaire de périphériques et de pilotes).

## 8. Livrables et méthode
Branche feature/signaux-bridage depuis develop, commits en français au présent comme l'historique. Tests xUnit : décodage des bits, PM table par version (inconnue → null), classement des événements, qualification depuis une heure donnée, journal tronqué, ordre et isolement des gestionnaires de StartupRecovery, lien PCIe, bail. `dotnet build -c Release` et `dotnet test` verts. Lignes « Raisons de bridage CPU », « Raisons de bridage GPU », « Journaux Windows », « Liens PCIe », « Contrôles de configuration », « Journal de session » par des fournisseurs de lignes (#1). README. « expérimental » (règle 6). `/review` avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Sur mon i5-14600K / RTX 5070 Ti / ASUS TUF B760-PLUS WIFI (tests des agents faits sur un i5-13500T), plus un Ryzen :
- CPU sous charge : TjMax, bits 0x1B1/0x19C, fréquence APERF/MPERF plausible.
- GPU sous charge : raisons NVAPI, lien PCIe courant = maximum.
- Un événement 41 ou 4101 connu retrouvé.
- Ryzen : version de PM table affichée, N/D si inconnue.
- Sans PawnIO : repli PDH et raison affichée.
- Entrée EnCours factice, app tuée, relance : « interrompu », sans faux « arrêt brutal ».
````


## Lot B · Gains rapides et indépendants

Indépendantes entre elles une fois le lot A fusionné : on peut les mener en parallèle, chacune dans sa branche.

### 5. Core parking et visuel d'usage par cœur

- Modèle / effort : **Opus 5.5 / high** · taille M · relecture /review
- Dépend de : #1
- Branche : `feature/core-parking`
- Matériel pour vérifier : Machine de Denis (i5-14600K, Intel hybride) ; un AMD X3D bi-CCD serait utile

````text
# Core parking et visuel d'usage par cœur (F5)

## 1. Objectif
Tes mots : « Gérer le core parking sur les CPU avec un visuel d'usage par cœur. » Dans Processeur : charge et état parqué de chaque cœur logique, groupés comme le matériel, et réglages du parking du plan actif, restaurables.

## 2. Avant de coder
Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées. Lis les sections « F5 » des deux études de docs/etudes/ si présentes, et docs/navigation.md s'il existe (emplacement décidé par #1 « Navigation par sections et cycle de vie des pages »). Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.

## 3. Ce qui existe
- `CpuPlatformDetector.DetectHybrid` : lit à la main le tampon de `GetSystemCpuSetInformation` (EfficiencyClass à l'octet 18), ne rend qu'`IsHybrid`.
- `PdhCounterSampler` (Core/Hardware) : `PdhAddEnglishCounterW`, une seule instance.
- `CpuPowerTuningService` : catalogue `CpuPowerSetting`, `TryRead`/`TryWrite` par powrprof (voit les réglages masqués, ne mémorise pas l'origine ; `TryWrite` refait `PowerSetActiveScheme`, écriture lourde : garde le Debouncer) ; tout réglage du catalogue entre dans CpuProfile (question 1). `CpuPowerSettingViewModel` (AC/DC, relecture) dans CpuControlViewModel.cs.
- Tweak « core-parking » (`WindowsPerformanceSettingsService`) : écrit CPMINCORES = 100 par powercfg ; décocher rend l'origine de `AppSettings.OriginalPowerValues` (clés « sous-groupe/réglage/ac|dc », repli 5) ; lit par `powercfg /query`, qui peut rendre Unknown (réglage masqué, à vérifier). `RememberOriginalValue` fait Load()+Save() : passe-le à `AppSettingsStore.Update()`.
- `MeterBar`, `AdaptiveGrid`, `Styles/ThemeColors.cs` ; cycle de vie de page `IPageLifecycle` (#1) (sinon modèle `ProcessesViewModel.IsActive`). Le libellé du groupe de capteurs CPU (MonitoringViewModel ≈ l.436) promet déjà une « charge par cœur ».

## 4. Approche retenue
Lecture sans admin, pas dans HardwareMonitorService (≈ 1280 lignes), mais en deux briques séparées de Core/Hardware/Cpu, à inscrire dans docs/decisions.md :
- `CpuTopology` : logique pure sur le tampon des CPU sets (cœur, fils SMT, L3/CCD, classe), réutilisée par #10, #16 et #21. Entrées de taille variable (champ Size) : Group @12 (WORD), LogicalProcessorIndex @14, CoreIndex @15 (fils SMT d'un même cœur), LastLevelCacheIndex @16 (L3/CCD), EfficiencyClass @18, AllFlags @19 (bit 0 = Parked).
- `CoreActivityReader` (PDH), réutilisable par le bench #10 : une seule requête, noms anglais (en français « État de parcage ») : `\Processor Information(*)\% Processor Utility` (plafonné à 100 comme le Gestionnaire des tâches), `…\Parking Status`, `…\% Processor Performance`, via `PdhGetFormattedCounterArrayW` (appel taille puis données). Instances « groupe,index » ; ignorer `_Total` et `n,_Total`.
- X3D : L3 de tailles différentes (`GetLogicalProcessorInformationEx`, RelationCache, rattachées aux groupes L3/CCD de `CpuTopology`) ; service `amd3dvcache` présent = le pilote AMD gère le parking.
- 1 s, hors thread d'interface, page affichée et fenêtre visible seulement ; % du temps parqué sur quelques secondes (l'état bascule vite).
Visuel : tuiles par L3/CCD puis par classe (libellés P/E seulement s'il y a au moins 2 classes), fils SMT côte à côte, remplissage = charge, hachures = parqué, « CCD avec 3D V-Cache ». Nouveau ResourceDictionary ; pas de Sparkline par cœur.
Réglages en % de cœurs, sous-groupe 54533251-82be-4824-96c1-47b60b740d00, plan actif, AC et DC, par powrprof : CPMINCORES 0cc5b647-c1df-4637-891a-dec35c318583, CPMAXCORES ea062031-0e34-4ff1-9b6d-eb1059334028 ; sur hybride CPMINCORES1 0cc5b647-c1df-4637-891a-dec35c318584 et CPMAXCORES1 ea062031-0e34-4ff1-9b6d-eb1059334029 ; en avancé SCHEDPOLICY 93b8b6dc-0698-4d1c-9ee4-0644e900c85d (0 tous, 1 performants, 2 préférer performants, 3 efficaces, 4 préférer efficaces, 5 auto), SHORTSCHEDPOLICY bae08b81-2d5e-4688-ad6a-13243356654b, HETEROPOLICY 7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5. Confirme par `powercfg /qh` avant de t'y fier. Origine capturée avant la première écriture, sous les clés `OriginalPowerValues` (pour reprendre ce que le tweak a noté). Ces réglages du plan actif sont durables : inscris-les au registre des modifications (ISystemChangeOwner, #1), avec Describe() (réglage, AC/DC, origine, valeur actuelle) et RestoreAll() (rend l'origine). Préréglages : « Windows (origine) », « Tous les cœurs actifs » (CPMINCORES = CPMINCORES1 = 100 ; d'après Microsoft, 100 % désactive le parking), « Économie » (CPMAXCORES réduit). Si la question 4 est validée, le tweak devient une façade de ce service (écrit aussi CPMINCORES1, lit par powrprof) et passe donc lui aussi par le registre des modifications.

## 5. Pièges
- Plan actif seulement : changer de plan (dont « Performances ultimes ») le perd ; outils OEM (Armoury Crate, Vantage) et profils PPM internes peuvent l'écraser : le dire.
- PC de test des agents (i5-13500T) : CPMINCORES1 = 0 et CPMINCORES = 4, P-cores probablement parqués malgré le tweak (à confirmer).
- `% Processor Time` et la charge par thread de LHM ne collent pas au Gestionnaire des tâches.
- CPU sets ou Parking Status absents : N/D avec raison (règle 3). Snapdragon X, Meteor/Lunar Lake (LP-E), Strix Point : classes non vérifiées, donc « expérimental » (règle 6).
- X3D bi-CCD : dé-parquer fait probablement perdre en jeu (non documenté par AMD) ; n'écris jamais amd3dvcache\Preferences (format non documenté).
- « Tous les cœurs actifs » chauffe et consomme plus au repos, surtout sur portable.

## 6. Questions à me poser
1. Parking dans les groupes de profils (#8) et CpuProfile, ou réglage indépendant ? Consigne la réponse dans docs/decisions.md, pour #8.
2. X3D bi-CCD : avertir (recommandé) ou masquer « Tous les cœurs actifs » ?
3. Secteur et batterie séparés (recommandé si batterie) ou une seule valeur ?
4. Corriger le tweak existant tout de suite (recommandé) ?

## 7. Périmètre
Hors : #8 « Groupes de profils CPU + GPU + ventilation », #9 « Profils automatiques selon l'usage », #16 « Curve Optimizer AMD automatique (OC CPU) », structure de navigation (#1), autres tweaks d'Optimisation (#6 « Gérer les animations Windows », qui modifie aussi WindowsPerformanceSettingsService : reste local au tweak core-parking).

## 8. Livrables et méthode
Branche feature/core-parking depuis develop ; commits en français au présent, comme l'historique ; tests xUnit (instances PDH, tampon CPU sets synthétique pour `CpuTopology`, regroupement L3/classe/SMT, préréglages, origine) ; `dotnet build -c Release` et `dotnet test` verts. Diagnostic : ligne « Cœurs et parking » par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel (CPU sets, nombre de classes, Parking Status, CPMIN/MAX(1) AC/DC, amd3dvcache). docs/decisions.md : lignes `CpuTopology` et `CoreActivityReader` (Core/Hardware/Cpu, ce qu'elles rendent) et réponse à la question 1 (parking dans les profils), pour #8. README : section Processeur. Relecture `/review` avant fusion dans develop, jamais sur master.

## 9. À vérifier sur ta machine (i5-14600K)
1. 6 P-cores à 2 fils et 8 E-cores, bien étiquetés.
2. Au repos, hachures conformes au Moniteur de ressources (colonne « Parqué »).
3. Charge par cœur proche du Gestionnaire des tâches (vue processeurs logiques).
4. « Tous les cœurs actifs » : plus rien de parqué ; restauration (préréglage « Windows (origine) », puis RestoreAll du registre des modifications) relue dans `powercfg /qh`.
````

### 6. Gérer les animations Windows

- Modèle / effort : **Sonnet 5 / high** · taille S · relecture /review
- Dépend de : #1
- Branche : `feature/animations-windows`
- Matériel pour vérifier : aucune (vérification visuelle sur n'importe quel Windows 11 ; un second compte administrateur pour le cas « app élevée sous un autre compte »)

````text
# Gérer les animations Windows (F15)

## 1. Objectif
Tes mots : « Pouvoir gérer les animations Windows. » Une carte « Animations et effets » dans Optimisation Windows, restaurable à l'identique.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « F15 » des deux études de docs/etudes/ si présentes.
- Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.

## 3. Ce qui existe
`PerformanceTweak` (GetState/Apply, RequiresElevation ; pas de raison d'indisponibilité), `TweakItemViewModel` (applique à chaque `OnIsOnChanged`), une carte par `Category` dans OptimizationView.xaml ; `WindowsPerformanceSettingsService` (≈ 300 lignes, surtout la liste de `GetTweaks`) ; `RegistryHelper` (DWORD seulement, `WriteDword` lève) ; `AppSettings.OriginalPowerValues` (modèle de mémoire d'origine) ; `SessionUser.IsOtherProfile`. Le tweak `visual-effects-performance` écrit VisualFXSetting=2, puis 0 en décochant (pas l'origine) : probablement sans effet.

## 4. Approche retenue
Nouveau fichier Core (ex. PowerSettings/WindowsAnimationSettings.cs) : `SystemParametersInfoW` avec SPIF_UPDATEINIFILE|SPIF_SENDCHANGE (0x01|0x02), jamais d'écriture de UserPreferencesMask. GET/SET : CLIENTAREAANIMATION 0x1042/0x1043 (« Effets d'animation » de Windows, à confirmer), ANIMATION 0x0048/0x0049 (ANIMATIONINFO, cbSize 8), MENUANIMATION 0x1002/0x1003, MENUFADE 0x1012/0x1013, COMBOBOXANIMATION 0x1004/0x1005, LISTBOXSMOOTHSCROLLING 0x1006/0x1007, SELECTIONFADE 0x1014/0x1015, TOOLTIPANIMATION 0x1016/0x1017, TOOLTIPFADE 0x1018/0x1019, CURSORSHADOW 0x101A/0x101B, DROPSHADOW 0x1024/0x1025, UIEFFECTS 0x103E/0x103F (maître de tous les effets), DRAGFULLWINDOWS 0x0026/0x0025. Vérifie ces valeurs avant de t'y fier. SET de la plage 0x10xx : valeur dans pvParam (0/1), pas un pointeur ; DRAGFULLWINDOWS : dans uiParam. Registre + WM_SETTINGCHANGE : `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarAnimations` et `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency` ; si l'Explorateur ne relit pas à chaud, le dire. Origine mémorisée au premier changement (dictionnaire à clés chaînes, `AppSettingsStore.Update`), et carte inscrite au registre des modifications de Windows de #1 (`ISystemChangeOwner`) : `Describe()` liste chaque réglage changé avec sa valeur d'origine, `RestoreAll()` rend l'origine mémorisée. Bouton « Rétablir mes réglages d'origine » (même chemin que `RestoreAll()`), préréglage « Réactif » (tout coupé sauf le lissage des polices) appliqué en un seul passage.
Avec `SessionUser.IsOtherProfile`, HKCU et SPI visent le profil de l'administrateur : griser avec la raison (règle 3), y compris `game-mode` et `visual-effects-performance`.

## 5. Pièges
- Un préréglage ne doit pas déclencher N `OnIsOnChanged` successifs.
- UIEFFECTS coupé, les autres effets ne jouent plus : relis chaque état après écriture.
- RevealPanel suit `SystemParameters.ClientAreaAnimation` : l'app perd aussi ses animations.
- Redémarrer l'Explorateur ferme ses fenêtres : demander avant.
- Gain sur la réactivité du bureau, pas sur les FPS : ne rien promettre d'autre.

## 6. Questions à me poser
1. Réglage par réglage avec préréglages (recommandé) ou un interrupteur global ?
2. Inclure transparence et ombres (recommandé) ?
3. Remplacer l'ancien tweak (recommandé) ?

## 7. Périmètre
Hors : tweak core-parking (#5 « Core parking et visuel d'usage par cœur », qui modifie aussi WindowsPerformanceSettingsService : reste local), structure de navigation (#1 « Navigation par sections et cycle de vie des pages »).

## 8. Livrables et méthode
Branche feature/animations-windows depuis develop ; commits en français au présent, comme l'historique ; tests xUnit (préréglage, origine, restauration, sur un accès SPI simulé) ; `dotnet build -c Release` et `dotnet test` verts ; ligne « Animations et effets » dans Paramètres › Compatibilité de ce PC, ajoutée par un fournisseur de lignes (`ICompatibilityRowProvider`, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel (compte visé en IsPersonal, valeurs lues ; règle 4) ; README (paragraphe Optimisation Windows) ; « expérimental » pour ce qui n'est pas vérifié sur une vraie machine (règle 6) ; relecture `/review` avant fusion dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Chaque interrupteur agit tout de suite ; Paramètres › Accessibilité › Effets visuels suit l'interrupteur principal ; la restauration rend l'état exact ; app élevée sous un autre compte : carte grisée.
````

### 7. Boîte à outils : liens de téléchargement direct

- Modèle / effort : **Opus 5.5 / xhigh** · taille M · relecture /review-max
- Dépend de : #1
- Branche : `feature/boite-a-outils`
- Matériel pour vérifier : aucune (n'importe quel PC Windows connecté à Internet suffit pour la vérification finale)

````text
# Boîte à outils : liens de téléchargement direct (F10)

## 1. Objectif
Ma demande : « Dans un endroit, ajouter les liens de téléchargement des outils nécessaires (lien qui télécharge direct, pas de redirection vers la page du site). » Un catalogue d'outils téléchargés en un clic et vérifiés. Franchement : seuls PawnIO et OCCT ont un lien stable « dernière version » ; HWiNFO et GPU-Z resteront en « page officielle ».

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis « ### F10 » des deux études docs/etudes/ et le point OpenRGB des remarques de F8, si présents (Grep).
- Regarde ce qu'a livré #1 (Navigation par sections et cycle de vie des pages) : il fixe l'emplacement (PageKeys) et le cycle de vie de la page (IPageLifecycle).
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.

## 3. Existant à réutiliser
- src/PCPerfSuite.Core/Installations/OfficialInstaller.cs : OfficialInstallerSource(Url, AllowedHosts, FileName, Arguments, ExpectedPublisher), GitHubHosts, DownloadAsync (internal, RequireAllowed à chaque redirection), Verify (privé : MZ, Authenticode, éditeur), RestrictToElevatedProcesses. Limites : 100 Mo, 5 min, pas de téléchargement seul.
- ExternalLink.TryOpen, ExternalSoftwareViewModel (IsRequired), RtssInstallation.Detect, explorer.exe /select (ProcessesViewModel.cs ≈2090) qui ne transmet pas le jeton admin, OfficialInstallerTests.

## 4. Approche retenue
- Page : par outil rôle, licence, version, état ; Télécharger (lien copiable), Page officielle, Installer ou Lancer. Catalogue lu à la première ouverture (IPageLifecycle, #1), pas dans un constructeur.
- `src/PCPerfSuite.Core/Installations/ToolCatalog.cs` (nouveau) fige ce qui ne vient jamais du réseau : id, hôtes autorisés, éditeur attendu (relevé sur le vrai fichier) ou « non signé », type (exe, msi, zip), détection. En ligne, un JSON de mon dépôt GitHub ne donne que version, URL versionnée, SHA-256, taille ; une GitHub Action le régénère chaque jour depuis winget-pkgs (InstallerUrl, InstallerSha256, NestedInstallerFiles) et l'API GitHub Releases, et hache elle-même ce qui est hors winget (Prime95, Cinebench). Proposé : signature ECDSA P-256 (clé publique embarquée), numéro croissant (anti-retour), copie embarquée hors ligne.
- Brique partagée « OfficialInstaller paramétré par source » (inscris sa ligne dans docs/decisions.md : #10, #13, #18 et #23 la réutilisent) : taille (~300 Mo : OCCT 206, Cinebench 262) et délai, SHA-256 obligatoire, puis Authenticode + éditeur si signé. Verify exige « MZ » : .msi et .zip se traitent par type. Non signé : téléchargé, jamais exécuté.
- Portables : dans le dossier sécurisé ProgramDataFolder (%ProgramData%\PCPerfSuite, ACL restreinte ; brique partagée à inscrire dans docs/decisions.md, réutilisée par #10, #13, #18 et #23), sous Tools\<id>\<version>, intégrité High, zip-slip refusé, exe interne vérifié ; outils portables supprimables d'un clic (le mode portable de #13 s'en sert) ; « Lancer » via explorer.exe, l'outil demandant son propre UAC (vérifie avant de t'y fier). Repli `winget install --id X --exact` si winget est là (absent avant la première session, en Sandbox).
- Tout autre fichier écrit par l'app (cache du catalogue téléchargé, téléchargements en cours) passe par AppDataPaths (#1), jamais un chemin %LOCALAPPDATA%\PCPerfSuite codé en dur.
- Outils déposés ou installés : inscrits au registre des modifications (ISystemChangeOwner, #1) ; Describe() les liste, RestoreAll() supprime les portables ; un outil posé par son propre installeur ne se désinstalle pas d'office, et Describe() le dit.
- État au 28/09/2026 : RTSS et Afterburner ont un zip versionné sur le miroir Guru3D ftp.nluug.nl (sous réserve des conditions de Guru3D), contrairement au README (≈295) et à RtssInstallation, à corriger. OpenRGB : l'étude se contredit (GitHub en F10, MSI Codeberg en F8), vérifie. HWiNFO : winget figé en 7.72 ; GPU-Z : 404. MemTest86 = clé USB, ajouter mdsched.exe.
- Licences : un technicien = usage commercial, que HWiNFO, OCCT, y-cruncher, Cinebench et sans doute FurMark interdisent ou font payer (badge « licence pro requise »). À vérifier : Prime95, CrystalDisk. DDU : jamais rehébergé. Prime95 et y-cruncher probablement non signés.

## 5. Pièges
- Jamais assouplir Verify ni RequireAllowed globalement. IsRequired=false partout (sinon Paramètres clignote).
- Lien « latest » + SHA-256 de la veille = faux échec à chaque sortie : d'où l'URL versionnée.
- %ProgramData% est inscriptible par les utilisateurs : refuser un dossier ou une jonction créés par un non-admin.
- Clé privée dans un secret de l'Action : hôtes et éditeurs figés restent la vraie barrière.
- Lien mort ou hash différent : message + Page officielle (règle 3), jamais de contournement de page à jeton.
- Rien d'installé sans geste de l'utilisateur. Un fichier laissé dans Téléchargements vise un autre profil si SessionUser.IsOtherProfile.

## 6. Questions à me poser
1. Public : particuliers, ou aussi techniciens (licences) ? Tranché dans docs/decisions.md (F3 : particuliers et techniciens, donc badge « licence pro requise ») ; pose-la seulement s'il ne l'est pas.
2. J'héberge (dépôt public ?) et je signe le catalogue ? (seulement si docs/decisions.md ne la tranche pas)
3. Outil non signé : SHA-256 seul sans lancement, ou écarté ?
4. Installer les outils ou les garder portables ?
5. Liste finale : CPU-Z, GPU-Z, CrystalDiskInfo/Mark, OCCT, Prime95, FurMark, Cinebench, DDU, NVCleanstall, OpenRGB, RTSS/Afterburner, PawnIO, 7-Zip, MemTest86, ISLC ?

## 7. Périmètre
Dans : catalogue, extension d'OfficialInstaller, ProgramDataFolder, page, Action GitHub, correction README/RTSS. Hors : pilotes (#18 Gestionnaire de périphériques et de pilotes, qui réutilisera l'extension et lancera DDU d'ici) ; outils tiers dans les mesures (#10 Moteur de charge et bench CPU / RAM / disque) ; OpenRGB moteur d'éclairage (#23 Éclairage RGB : socle et OpenRGB).

## 8. Livrables et méthode
Effort xhigh (binaires téléchargés exécutés en administrateur). Branche feature/boite-a-outils depuis develop, commits en français au présent comme l'historique. Tests xUnit sans réseau : Theory sur tout le catalogue (HTTPS, hôte autorisé, éditeur ou « non signé », nom sans chemin), SHA-256, signature, anti-retour, JSON tolérant, zip-slip. `dotnet build -c Release` et `dotnet test` verts. Ligne « Boîte à outils » dans Paramètres › Compatibilité de ce PC, ajoutée par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel. Lignes OfficialInstaller et ProgramDataFolder de docs/decisions.md à jour. Section README. « expérimental » pour le non vérifié (règle 6). `/review-max` avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Sur mon i5-14600K / RTX 5070 Ti (tests des agents faits sur un i5-13500T) :
- CPU-Z, OCCT, RTSS : SHA-256 et éditeur acceptés.
- Prime95 : téléchargé, jamais lancé.
- Outil portable : « Lancer » passe par son UAC.
- Réseau coupé : catalogue embarqué, message clair.
````


## Lot C · Profils

Les groupes de profils, puis la bascule automatique qui s’appuie dessus. Il faut que le lot A soit fusionné, parce qu’il apporte le journal de session et le bail de réglage.

### 8. Groupes de profils CPU + GPU + ventilation

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #1, #2, #4
- Branche : `feature/groupes-profils`
- Matériel pour vérifier : machine de Denis (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI)

````text
# Groupes de profils CPU + GPU + ventilation (F19)

## 1. Objectif
Ma demande : « Créer des groupes de profils à appliquer (params CPU + GPU + ventilation). » Un groupe s'applique d'un clic et se modifie à la main. C'est aussi le socle de #9 (Profils automatiques selon l'usage), qui en générera trois et basculera entre eux ; ces trois-là, je dois pouvoir « également les régler à la main » (F18).

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F19 » (trois) et « ### F18 » de docs/etudes/cartographie-code-2026-09.md si présentes (Grep). L'étude de faisabilité n'en a pas.
- Regarde ce qu'ont livré #1 (Navigation par sections et cycle de vie des pages), #2 (Fiabiliser le tuning avant toute automatisation : GpuIdentity, sécurité thermique GPU, Update()), #5 (parking entré ou non dans le catalogue de CpuPowerTuningService) et #4 (Socle de signaux : SessionJournal, StartupRecovery, qualification d'incident). Appuie-toi sur leurs garde-fous.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille L : soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Existant à réutiliser
- Modèles : `CpuProfile` (src/PCPerfSuite.Core/Hardware/Cpu/CpuProfile.cs : PowerSettings Id→{Ac, Battery}, SustainedWatts?, BurstWatts?, null = ne pas toucher) ; `GpuOverclockProfile` (src/PCPerfSuite.Core/Hardware/GpuControlModels.cs : décalages cœur et mémoire NON nullables, PowerLimitPercent?, TemperatureLimitC?, VoltageValue/VoltageUnit, GetVoltage(), aucune marque ni identité de carte) ; `FanProfile` + `FanProfileMatcher.Match/Sanitize` (src/PCPerfSuite.Core/Hardware/Fans/FanProfile.cs).
- src/PCPerfSuite.Core/PowerSettings/AppSettingsStore.cs : FanProfiles, Gpu.OverclockProfiles, Gpu.OverclockVendor, Cpu.Profiles, Cpu.RiskAccepted, Gpu.IntelOverclockWaiverAccepted, Cpu.ApplyAtStartup, Gpu.ApplyOverclockAtStartup, `AppSettingsStore.Update()`.
- Application aujourd'hui privée et liée à l'interface : `CpuControlViewModel.ApplyProfile` (≈573) + `ApplyProfileWatts` (≈626, refuse sans RiskAccepted) + `CpuPowerSettingViewModel.ApplyFromProfile` / `FlushPendingWrite` ; `GpuControlViewModel.ApplyProfile` (≈556 : refus si !CanOverclock, bornes, tension seulement si même unité, écriture directe, Persist) ; `FanCurvesViewModel.ApplyProfile` (≈996 : Match, Sanitize, un seul Persist, LastProfileReport).
- `BuildSummary` (CPU), `BuildProfileSummary` (ventilateurs), `FanProfileViewModel.Rename`, `CompatibilityViewModel.FanProfilesRow`, `CpuControlService.EmergencyRestored` / `KeepLimitsOnExit`, `GpuControlService.KeepOverclockOnExit`.
- De #2 : `GpuIdentity`, sécurité thermique GPU, relecture « demandé / retenu ». De #4 : `SessionJournal`, `StartupRecovery`, qualification d'incident. De #1 : fournisseurs de lignes, registre des modifications de Windows (ISystemChangeOwner).

## 4. Approche retenue
- `src/PCPerfSuite.Core/Profiles/ProfileGroup.cs` (nouveau) : { Id (Guid en chaîne, stable), Name, Usage (chaîne : `bureautique`, `gaming-leger`, `gaming-intensif` ou null), Cpu: CpuProfile?, CpuIdentity (fabricant, famille, modèle, nom), Gpu: GpuOverclockProfile?, GpuIdentity (brique de #2 : marque, nom, identifiants PCI si lus), Fans: FanProfile?, Origin (`manuel` | `genere`), EditedByUser (bool) }. Copies intégrées, pas de références par nom : les profils se renomment ou sont remplacés à nom égal. Dimension null = ne pas toucher ; « ne pas toucher au GPU » = Gpu null, jamais un profil à 0. Prévoir aussi une valeur explicite « origine » (OC GPU retiré, watts d'usine), distincte de null : #9 en a besoin pour son groupe bureautique.
- Identité : une partie GPU capturée sur une autre carte n'est pas appliquée, avec la raison. Cela vaut aussi pour une carte de même marque : des décalages et une tension ne se transposent pas d'une carte à l'autre. Même règle pour les watts capturés sur un autre processeur. L'ApplyProfile actuel ne vérifie ni l'un ni l'autre.
- Dimensions futures facultatives : éclairage (#24), préférence GPU (#22). Parking (#5) : s'il est entré dans le catalogue de CpuPowerTuningService, il est déjà dans CpuProfile ; sinon, seulement la place. `[JsonExtensionData]` pour conserver ce qu'une version plus récente a écrit. Nouveaux champs en chaînes : sans JsonStringEnumConverter, les enums partent en nombres, donc ne rien insérer au milieu de FanControlMode ou FanTempSource.
- Sur les 3 VM, une API publique minimale : `Apply(modèle, options)` → rapport structuré (appliqué, ignoré + raison), `Capture()` → état courant. La RelayCommand existante l'appelle puis écrit son statut : aucune sécurité dupliquée, aucun MessageBox ni statut d'onglet dans le chemin automatique. Fichiers déjà trop gros (700 à 1500 lignes) : la logique pure (rapport, ordre, tolérance) va dans src/PCPerfSuite.Core/Profiles/.
- Chaque VM reste propriétaire de sa partie de settings.json (FanCurvesViewModel.Persist réécrit FanProfiles depuis sa copie en mémoire) : tout passe par les VM. Les groupes ont leur bloc `AppSettings.ProfileGroups`, écrit seulement par le nouveau ProfileGroupsViewModel via Update().
- Orchestrateur côté App : montée en performance = ventilateurs puis CPU/GPU ; descente = CPU/GPU puis ventilateurs. Rapport consolidé par dimension.
- Bail de réglage `TuningLease` (logique pure dans src/PCPerfSuite.Core/Profiles/, un seul détenteur, tenu par l'orchestrateur) : `Acquire(demandeur, raison)` → IDisposable, un seul détenteur à la fois. Tant qu'un autre demandeur le tient, aucune application de groupe, manuelle ou automatique. Les onglets Processeur, GPU et Ventilateurs affichent alors « Réglages pilotés par <demandeur> », et l'écriture manuelle est refusée avec la raison (question 4). Demandeurs prévus : bascule automatique (#9), bench (#10, #11), vérification de l'OC (#14), recherche d'OC (#15, #16). Les sécurités thermiques CPU et GPU agissent toujours, bail ou non. C'est le seul moyen de suspendre #9 : pas d'API de pause propre à #9.
- Jamais contourner RiskAccepted ni la renonciation Intel : la partie concernée est annoncée non appliquée, avec sa raison. Portable : la ventilation se réduit au ventilateur du GPU (pilote graphique, règle 5), le dire. Les modes de performance constructeur suivent la décision « règle 5 » de docs/decisions.md (lecture seule par défaut). Intel : l'écriture PL1/PL2 dépend de #2 (le module IntelMSR de LibreHardwareMonitor 0.9.6 n'expose probablement que la lecture) ; le rapport relit, il ne suppose pas.
- Les réglages du plan d'alimentation (CpuPowerTuningService) sont PERMANENTS, jamais rendus à la fermeture, contrairement aux watts, à l'OC GPU et aux ventilateurs. Le dire dès qu'un groupe en contient. Mémoriser leur origine avant la première écriture d'un groupe et s'inscrire au registre des modifications de Windows de #1 (ISystemChangeOwner).
- Ventilateurs connus seulement après le premier relevé (`_hasSnapshot` de FanCurvesViewModel) : une application au lancement attend ce relevé.
- Décider ce qui est réappliqué au démarrage (aujourd'hui : dernières valeurs CPU/GPU si « Appliquer au démarrage », GPU seulement si même marque, ventilateurs toujours). Prévoir une option « appliquer sans en faire l'état de démarrage », dont #9 aura besoin.
- Prudence au démarrage : chaque application d'un groupe contenant de l'OC GPU ou des watts au-delà de l'origine est une entrée du SessionJournal de #4. Au lancement, ton gestionnaire StartupRecovery lit la qualification d'incident : si un arrêt brutal, un écran bleu ou un TDR survient moins de 30 min après l'application, ce groupe n'est pas réappliqué et je suis prévenu. #15 ajoutera ses règles pour les profils d'OC automatique.
- Page « Profils » (emplacement selon #1) : liste avec résumé, « Enregistrer l'état actuel » (cases par dimension), importer un profil d'onglet, appliquer, renommer, dupliquer, supprimer (confirmation dans la page), groupe actif, bail en cours (qui, depuis quand).
- « Modifier » un groupe, puisque F18 exige que les groupes générés se règlent à la main. Pour chaque dimension, choisir : ne pas toucher, origine, état actuel, ou un profil existant de l'onglet. Plus « Régler dans l'onglet » : le groupe est appliqué, j'ajuste dans Processeur, GPU ou Ventilateurs, puis « Mettre à jour le groupe » capture la dimension choisie. Toute modification d'un groupe `genere` passe EditedByUser à vrai : #9 ne le régénérera jamais.

## 5. Pièges
- ApplyFromProfile renvoie vrai avant l'écriture (Debouncer de 250 ms, puis un PowerSetActiveScheme par réglage) : le rapport attend l'écriture réelle (FlushPendingWrite) et relit. Vider aussi le Debouncer GPU, sinon un curseur touché juste avant écrase le groupe.
- Profil venu d'un autre PC : entrées inconnues ignorées et comptées.
- Après `EmergencyRestored` (sécurité thermique CPU), ne pas reposer les limites dans la foulée.
- Après une veille, le firmware rétablit ses limites CPU : appuie-toi sur la relecture au réveil ajoutée par #2, et n'affiche pas un « groupe actif » mensonger.
- Load() puis Save() hors Update() perd les réglages écrits en parallèle : vérifie que #2 a passé CpuControlViewModel.PersistProfiles à Update(), sinon fais-le.
- Un bail peut durer des heures (recherche d'OC). Il tombe si son détenteur meurt (processus enfant, IDisposable jamais rendu), et l'interface dit toujours qui le tient.

## 6. Questions à me poser
1. Inclure les réglages du plan d'alimentation (permanents) dans un groupe ? Le parking suit ce que #5 a tranché.
2. Groupe actif mémorisé et « Appliquer au démarrage » au niveau du groupe, ou on garde les cases par onglet ?
3. Critère de montée/descente pour l'ordre : l'usage du groupe, ou la limite de puissance visée ?
4. Écriture manuelle dans un onglet pendant un bail : refusée avec la raison (recommandé), ou confirmation qui interrompt le détenteur ?
Ce qu'on rend à la fermeture est tranché dans docs/decisions.md ; si ce n'est pas le cas, pose-le aussi.

## 7. Périmètre
Dans : modèle, identités, API, orchestrateur, bail de réglage, modification des groupes, prudence au démarrage, page, diagnostic. Hors : génération et bascule automatiques (#9 Profils automatiques selon l'usage), profils OC calculés (#15 OC automatique GPU, #16 Curve Optimizer AMD automatique), parking (#5 Core parking et visuel d'usage par cœur), éclairage (#23 et #24 Éclairage RGB), GPU dédié des portables (#22) : seulement la place pour ces dimensions.

## 8. Livrables et méthode
Branche feature/groupes-profils depuis develop, commits en français au présent comme l'historique. Tests xUnit : sérialisation tolérante (propriété absente, inconnue conservée, dimension null, enums en nombres), rapport consolidé, ordre d'application, groupe d'un autre PC, d'une autre carte graphique (même marque comprise) ou d'un autre processeur, bail (un seul détenteur, refus, libération, détenteur mort), EditedByUser, prudence au démarrage sur des entrées de journal synthétiques. `dotnet build -c Release` et `dotnet test` verts. Ligne « Groupes de profils » par un fournisseur de lignes (#1) : nombre, dernier rapport, bail en cours. Section README. « expérimental » pour le non vérifié (règle 6). `/review-max` avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Sur mon i5-14600K / RTX 5070 Ti / ASUS TUF B760-PLUS WIFI (les tests des agents ont tourné sur un i5-13500T) :
- Enregistrer l'état, tout changer à la main, réappliquer : PL1/PL2 (si #2 a rendu l'écriture possible), décalages GPU et courbes relus identiques.
- Modifier seulement la ventilation d'un groupe depuis l'état actuel : les autres dimensions restent intactes.
- Groupe sans GPU : l'OC GPU en place reste intact.
- Avertissement CPU non accepté : le rapport dit « watts ignorés ».
- Bail tenu (outil de test) : appliquer un groupe est refusé avec la raison.
- Fermeture puis relance avec « appliquer au démarrage » : état conforme à l'annonce, ventilateurs réglés après le premier relevé.
````

### 9. Profils automatiques selon l'usage

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #8, #2, #4
- Branche : `feature/profils-auto`
- Matériel pour vérifier : machine de Denis, idéalement aussi un portable

````text
# Profils automatiques selon l'usage (F18)

## 1. Objectif
Ma demande : « L'app analyse l'utilisation du PC pour générer 3 profils de ventilation et OC (bureautique, gaming léger, gaming grande ressources) et puis l'app va switcher entre les trois profils que l'IA a générés selon l'utilisation du PC. Il faut que les trois profils puissent être également réglés à la main. » Ici : l'analyse, la génération (ventilation et limites), la bascule. L'OC viendra de #15 et #16, qui rempliront l'emplacement que tu prévois. Le réglage à la main passe par « Modifier » dans la page de #8.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1), notamment : sens de « IA », règle 5 sur les portables, état à la fermeture. Ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F18 » (quatre) et « ### F19 » de docs/etudes/cartographie-code-2026-09.md si présentes (Grep). L'étude de faisabilité n'en a pas.
- Regarde ce qu'ont livré :
  - #8 (Groupes de profils CPU + GPU + ventilation : ProfileGroup, API, bail de réglage, « Modifier », EditedByUser, prudence au démarrage) ;
  - #2 (Fiabiliser le tuning avant toute automatisation) ;
  - #4 (Socle de signaux : SessionJournal, StartupRecovery, qualification d'incident, bridage) ;
  - #3 (DisplayTopology, filtre des fenêtres du shell).
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille L : soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Existant à réutiliser
- De #8 : ProfileGroup (Usage, Origin, EditedByUser, valeur « origine »), API appliquer/capturer avec rapport, orchestrateur, TuningLease, option « appliquer sans en faire l'état de démarrage ».
- De #4 : SessionJournal, StartupRecovery et qualification des incidents (Kernel-Power 41, 6008, 1001, 4101, WHEA) ; raisons de bridage. De #3 : DisplayTopology (bornes de l'écran d'une fenêtre) et filtre des fenêtres de PCPerfSuite et du shell.
- `MonitoringViewModel.SnapshotUpdated` (thread UI, aussi en mode éco) ; `HardwareSnapshot` : Cpu.LoadPercent, Gpu.LoadPercent, Game (RtssFrameStats, seulement si RTSS tourne, sans nom de processus), Battery (PowerOnline, Discharging), GroupsRead, CapturedAtUtc ; `CpuPlatform.HasBattery` (CpuPlatformDetector.cs).
- Premier plan : SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT) déjà écrit dans src/PCPerfSuite.App/Interop/TopmostKeeper.cs (délégué gardé en champ) ; `RtssFrameStatsReader.GetForegroundProcessId` (privé).
- `IBackgroundSensorConsumer` + `BackgroundSensorNeeds` (src/PCPerfSuite.App/ViewModels/IBackgroundSensorConsumer.cs) ; `MainViewModel.UpdateEcoMode` ({ _overlay, _fans, _cpu } et ce que #2 a ajouté) et `MainViewModel.Dispose` (ordre imposé).
- Anti-oscillation : `FanCurveRegulator` (hystérésis), `CpuControlService` (condition tenue 15 s) ; préréglages `FanCurveMath.SilencieuxPoints/EquilibrePoints/PerfPoints` ; `TrayIcon.ShowHint` ; `RunningStats`.

## 4. Approche retenue
- « IA » : ce que tranche docs/decisions.md (recommandé : heuristique locale, déterministe et explicable). Aucun LLM ne décide d'une écriture matérielle.
- `src/PCPerfSuite.Core/Profiles/UsageClassifier` (pur) : échantillons → bureautique / gaming léger / gaming intensif. Signaux :
  - processus au premier plan : chemin d'exe via QueryFullProcessImageName, sur un handle PROCESS_QUERY_LIMITED_INFORMATION refermé aussitôt. Lecteur léger dans Core/Processes (brique « lecteur du premier plan »), réutilisable par #21 et #22, qui filtre les fenêtres de PCPerfSuite et du shell comme #3 ;
  - plein écran : SHQueryUserNotificationState = QUNS_RUNNING_D3D_FULL_SCREEN, ou fenêtre qui couvre son écran d'après DisplayTopology (vérifie avant de t'y fier) ;
  - charges CPU/GPU soutenues, FPS RTSS s'il est là, secteur ou batterie.
  Délais de maintien et hystérésis, à calibrer et marqués « expérimental » : entrer en gaming après ~30 s tenues, en sortir après ~2 min, au plus une bascule toutes les ~2 min.
- Règles par application (application → usage ou → groupe), prioritaires sur le classifieur. Elles reposent sur le modèle partagé `ApplicationMatch` (Core/Processes) : chemin complet normalisé par défaut, éditeur facultatif, nom d'exe seul seulement si je le choisis. #21 (limites) et #22 (préférence GPU) le reprendront.
- Historique agrégé par tranches de 1 à 5 min, dans usage.json. Ce fichier va dans le dossier de données (AppDataPaths de #1), pas dans settings.json (réécrit en entier à chaque Save). Il garde : temps, charges, températures CPU/GPU p50/p95 par usage, applications vues ; ~30 jours.
- Générateur pur des 3 ProfileGroup (Origin `genere`), chacun avec son explication lisible :
  - courbes dérivées des préréglages, décalées d'après les températures observées ;
  - limites de puissance et EPP (bureautique économe) ;
  - bureautique = GPU d'origine, par la valeur explicite « origine » de #8, jamais Gpu null (« ne pas toucher » laisserait en place l'OC du groupe gaming) ;
  - emplacement OC nommé et documenté dans docs/decisions.md, laissé vide ici : #15 (GPU) et #16 (CPU AMD) le rempliront. N'invente pas de valeurs d'OC.
  Les groupes se règlent à la main par « Modifier » de #8 ; régénérer n'écrase jamais un groupe EditedByUser.
- `src/PCPerfSuite.App/ViewModels/AutoProfileSwitcher` :
  - abonné au relevé : travail O(1) sur le thread UI, dédoublonné par CapturedAtUtc, GroupsRead testé ;
  - IBackgroundSensorConsumer (CpuLoad, Gpu, Battery, Fps ; Cpu seulement si l'historique garde la température CPU, groupe coûteux), ajouté à UpdateEcoMode ;
  - arrêté en tête de MainViewModel.Dispose, avant _monitoring, donc avant _fans, _gpu et _cpu.
- Garde-fous :
  - verrouillage après `CpuControlService.EmergencyRestored` et après la sécurité thermique GPU de #2 ;
  - pause quand je règle quelque chose à la main ;
  - pause tant qu'un autre demandeur tient le bail de réglage de #8 (bench, vérification ou recherche d'OC), sans API de suspension propre ; la bascule prend elle-même le bail le temps d'appliquer ;
  - jamais contourner RiskAccepted ni la renonciation Intel ;
  - portable : ventilation = ventilateur du GPU seulement (règle 5), modes constructeur selon docs/decisions.md (lecture seule par défaut).
- Une bascule n'est pas l'état de démarrage : utilise l'option de #8. Chaque réglage d'alimentation écrit coûte un PowerSetActiveScheme (CpuPowerTuningService.TryWrite) : n'écris que ce qui change.
- Prudence au démarrage : chaque bascule vers un groupe avec OC ou watts relevés est une entrée du SessionJournal de #4. Au lancement, ton gestionnaire StartupRecovery lit la qualification. Arrêt brutal, écran bleu ou TDR peu après : pas de rebascule d'emblée vers ce groupe, note au journal des bascules, et je suis prévenu.
- Notification discrète et désactivable, journal des bascules (heure, usage, raison, parties non appliquées). Page : interrupteur, état courant, les 3 groupes (modifiés via la page de #8), règles par application.

## 5. Pièges
- Sans inscription dans UpdateEcoMode, le mode éco suspend CpuLoad et Gpu : l'analyse ne voit plus rien fenêtre cachée.
- ProcessesViewModel ne relève que page affichée, et un relevé ProcessService coûte trop cher en permanence : ne pas s'y appuyer.
- Plan d'alimentation permanent : à la fermeture, le PC reste sur le dernier profil de plan. Le dire.
- Noms d'exécutables = données personnelles : jamais dans le diagnostic ni dans le journal de session (IsPersonal ne sait masquer que le nom de compte).
- Debouncers de l'OC GPU et des réglages d'alimentation : la bascule attend l'écriture réelle avant d'annoncer.

## 6. Questions à me poser
1. Apprentissage avant de générer les 3 groupes : quelques jours, ou tout de suite puis affinage ?
2. Règles par application dès la V1 ?
3. Notification : à chaque bascule, la première seulement, ou jamais ?
Sens de « IA » et état à la fermeture : dans docs/decisions.md ; pose-les seulement s'ils n'y sont pas.

## 7. Périmètre
Dans : classifieur, ApplicationMatch, historique, générateur (sans valeurs d'OC), bascule, prudence au démarrage, page, diagnostic. Hors : modèle, API, modification et bail des groupes (#8 Groupes de profils CPU + GPU + ventilation) ; profils OC et leur branchement dans le générateur (#15 OC automatique GPU, #16 Curve Optimizer AMD automatique) ; signaux et journal (#4 Socle de signaux) ; préférence GPU par application et GPU dédié sur batterie (#22 GPU dédié des portables) ; limites par processus (#21 Limiter CPU / RAM / disque / réseau par processus).

## 8. Livrables et méthode
Branche feature/profils-auto depuis develop, commits en français au présent comme l'historique. Tests xUnit :
- classifieur sur séquences construites (entrée, maintien, sortie, oscillation, menu de jeu, batterie) ;
- ApplicationMatch ;
- générateur (EditedByUser respecté) ;
- politique de bascule (verrouillage, pause manuelle, pause pendant le bail d'un autre demandeur, prudence après incident) ;
- lecture tolérante d'usage.json.
`dotnet build -c Release` et `dotnet test` verts. Ligne « Bascule automatique » par un fournisseur de lignes (#1) : activée, dernier changement, raison, parties non appliquées, sans nom d'exe. Section README. Seuils marqués « expérimental » (règle 6). `/review-max` avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Sur mon i5-14600K / RTX 5070 Ti (tests des agents faits sur un i5-13500T), idéalement aussi sur un portable :
- Bureautique → jeu plein écran → retour : bascules au bon moment, rien en menu de jeu.
- Sans RTSS : détection par premier plan et charge GPU.
- Retour en bureautique : OC GPU retiré.
- Fenêtre réduite en mode éco : la bascule continue. Sécurité thermique CPU : bascule verrouillée. Réglage manuel en jeu : pause. Bench lancé : aucune bascule tant qu'il tourne.
- Groupe généré modifié à la main, puis « Régénérer » : ma modification reste.
- Portable : secteur/batterie, ventilation limitée au GPU.
````


## Lot D · Bench et diagnostic

C’est le cœur de l’objectif d’une app pour technicien. Dans l’ordre : le moteur de charge, le bench GPU et le test d’alimentation, le moteur de diagnostic, puis le mode technicien avec la rédaction par IA.

### 10. Moteur de charge et bench CPU / RAM / disque

- Modèle / effort : **Fable 5.1 / xhigh** · taille XL · relecture /review-max
- Dépend de : #1, #4 ; #5, #7, #8 conseillés
- Branche : `feature/bench-cpu-ram-disque`
- Matériel pour vérifier : machine de Denis + au moins un AMD et un portable pour calibrer

````text
# Moteur de charge et bench CPU / RAM / disque (F1, partie 1)

## 1. Objectif
Ma demande : « ajouter une possibilité de bench cpu/gpu/ram/disque/power etc sélectionnables indépendamment ». Ici : le moteur de charge commun (réutilisé par #11 et #15/#16) et les benchs CPU, RAM et disque. Mesures reproductibles, comparables et sûres.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1), notamment signature de code et licence. Ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis « ### F1 » des deux études docs/etudes/ et « ### F4 » de l'étude de faisabilité (charge vérifiée), si présents (Grep).
- Regarde ce qu'ont livré, et n'en duplique aucune brique :
  - #1 : navigation, AppDataPaths, fournisseurs de lignes ;
  - #4 (Socle de signaux) : bail de cadence, bridage, SessionJournal, StartupRecovery ;
  - #5 : CpuTopology ;
  - #2 : sécurité thermique en logique pure ;
  - #7 : dossier %ProgramData% sécurisé ;
  - #8 : bail de réglage.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille XL : soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Existant à réutiliser
- `MonitoringViewModel.SnapshotUpdated` / `MetricsUpdated` : s'abonner, jamais `HardwareMonitorService.GetSnapshot` (un seul appelant). `HardwareSnapshot` immuable (CapturedAtUtc, GroupsRead) ; `MetricCatalog.All` pour extraire des séries nommées.
- De #4 : bail de cadence (relevé à 4-10 Hz pendant un test), raisons de bridage, TjMax, lien PCIe, SessionJournal et StartupRecovery.
- Autres briques : CpuTopology (#5) ; sécurité thermique en logique pure : seuil, durée, heure injectée (#2) ; dossier %ProgramData%\PCPerfSuite sécurisé (#7) ; TuningLease (#8).
- `CpuPlatformDetector.Detect()` → CpuPlatform (Family, Model, IsHybrid), `SystemMemoryReader`, `MemoryModuleReader`, `DiskHealthService` (MSFT_PhysicalDisk ; BusType et MediaType pas encore lus), `DiskVolumeReader`, `BatteryReader`, `PowerPlanService` ; GetActiveProcessorCount(ALL_PROCESSOR_GROUPS), P/Invoke privé de `ProcessService` ; `SamplingLoop` (échéances absolues).
- Coupure thermique historique : `CpuControlService` (98 °C tenus 15 s) ; témoin anti-plantage : `AdlxProbeGuard` ; `CrashLog`.
- App.OnStartup : mutex d'instance unique (Local\PCPerfSuite.SingleInstance) ; seul argument lu aujourd'hui : --demarrage-windows (StartupTask).
- Affichage : src/PCPerfSuite.App/Controls/Sparkline.cs, `RunningStats`. Arrière-plan : `IBackgroundSensorConsumer` + `MainViewModel.UpdateEcoMode`.

## 4. Approche retenue
**Moteur**
- Processus de charge séparé : `PCPerfSuite.BenchWorker` dans la solution, ou mode du même exe (question 2, d'après la décision de signature de docs/decisions.md). Avantages : interface fluide, un plantage ne tue pas l'app, réglages propres. Noyaux en `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`, aucune allocation dans les boucles, TieredPGO désactivé si utile. Lancé seulement depuis le dossier de l'app, par chemin complet.
- Modes secondaires : crée `SecondaryModes` (App), lu avant le mutex d'instance unique.
  - Liste fermée d'arguments : le worker s'il est dans le même exe ; #15 y ajoutera --watchdog et --reprendre-oc.
  - Argument inconnu refusé, aucune fenêtre ; --demarrage-windows inchangé.
  - Inscris-le dans docs/decisions.md.
- Tube nommé (`NamedPipeServerStream`, nom aléatoire, PipeOptions.CurrentUserOnly), messages JSON versionnés : démarrer, progression, résultat, erreur.
  - Battement de cœur : le worker coupe toute charge et quitte s'il n'a plus de nouvelles de l'app (~2 s).
  - L'app tue le worker si elle ne reçoit plus de capteurs.
  - Job Object avec JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
- `BenchVersion` + version du runtime dans chaque résultat : un score ne se compare qu'à la même version. L'app est framework-dependent (SelfContained=false), contrairement à ce que supposait l'étude.
- Pour #15/#16 dès maintenant : noyaux à résultat vérifié (somme de contrôle à graine fixe, erreur de calcul remontée), charge sur un cœur choisi, cycles charge/repos.

**Protocole commun**
- Avant le test, noter dans le résultat :
  - secteur ; sur batterie, refus ou « non représentatif » (à trancher) ;
  - charge de fond < 5 % pendant 10 s ;
  - mode d'alimentation (PowerGetEffectiveOverlayScheme), plan actif, limites CPU/GPU ;
  - courbes de ventilation et mode constructeur du portable (WMI de la marque, lecture seule, par les modules LaptopFans existants).
- Worker hors EcoQoS : SetProcessInformation(ProcessPowerThrottling, ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED, StateMask = 0) ; priorité High, jamais Realtime.
- Préchauffe 5-10 s non comptée (tiered JIT, Dynamic PGO). Puis 3 passes, médiane + coefficient de variation (au-delà de ~3 % : « instable »). Retour au repos entre tests (±3 °C de la base, 60 s max). Seuils à calibrer, « expérimental ».
- Arrêts de sécurité, sur la logique pure de #2 (pas une copie) :
  - CPU à TjMax (0x1A2 via #4, sinon seuil fixe prudent) pendant 10 s, par famille : Zen 4/5 à 95 °C et X3D à 89 °C sont normaux ;
  - ventilateur CPU identifié à 0 tr/min sous charge, capteurs muets, batterie < 30 %.
  Durée des noyaux FMA plafonnée (profil « power virus »). Bouton Arrêter global toujours visible. Jamais de lancement automatique ni planifié.
- Bail de réglage de #8 tenu pendant tout le test : ni groupe ni bascule automatique (#9) ne change les réglages pendant une mesure.
- Une entrée du SessionJournal de #4 par test. Ton gestionnaire StartupRecovery traite une entrée restée EnCours : « bench interrompu » ou « arrêt brutal pendant le bench » selon la qualification de #4, et fichier de test disque supprimé.

**CPU**
- Noyaux C# déterministes à graine fixe :
  - entier : tri, hachage ou compression d'un tampon généré, sans instructions SHA/AES ;
  - flottant : produit matriciel Vector256 + Fma, repli Vector<T> ; ARM64 AdvSimd marqué non comparable ;
  - branches.
- Mono-thread épinglé sur un cœur de la plus haute EfficiencyClass d'après CpuTopology (#5), par SetThreadSelectedCpuSets ; sinon un hybride tombe sur un cœur E. Multi : un thread par processeur logique.
- Rafale (20 premières s) et soutenu (après 3 min, au-delà du Tau Intel de 28 ou 56 s) : l'écart est déjà un diagnostic.

**RAM**
- Tampons `NativeMemory.AlignedAlloc` (64 o), au moins 8 × L3 (96 Mo sur X3D) et au plus 25 % de la RAM libre, pages touchées avant le chrono.
- Lecture, écriture (Avx.StoreAlignedNonTemporal) et copie multi-thread.
- Latence par pointer chasing sur une permutation à cycle unique (Sattolo) de 256 Mo à 1 Go, en pages de 4 Ko (grandes pages écartées : SeLockMemoryPrivilege).

**Disque**
- Fichier dédié, hors Documents et Bureau (accès contrôlé aux dossiers) :
  - dans le sous-dossier Bench du dossier %ProgramData%\PCPerfSuite sécurisé de #7 ;
  - ou à la racine d'un volume secondaire, avec les mêmes protections (ACL restreinte, jonctions refusées, CreateNew).
  Espace libre vérifié ; supprimé en finally et par StartupRecovery.
- `File.OpenHandle(..., FileOptions.Asynchronous | (FileOptions)0x20000000)` (FILE_FLAG_NO_BUFFERING), `RandomAccess.ReadAsync/WriteAsync`. Offsets et tailles multiples de BytesPerPhysicalSector (IOCTL_STORAGE_QUERY_PROPERTY), tampons alignés sur la page.
- Fichier entier prérempli de données aléatoires avant les lectures, sinon NTFS rend des zéros au-delà de la valid data length ; pas de SetFileValidData.
- Profils façon CrystalDiskMark : séquentiel 1 Mo en file de 8 et 1, aléatoire 4 Ko en file de 32 et 1, 5-10 s chacun.
- Volume écrit plafonné (1-4 Go, à trancher), cache SLC signalé, HDD raccourci, volumes réseau exclus, BitLocker noté.
- BusType et MediaType ajoutés à DiskHealthService (MSFT_PhysicalDisk), réutilisables par #19 ; eux et le lien PCIe (#4) figurent dans le résultat.

**Résultats et page**
- Un JSON par session, dans le sous-dossier bench du dossier de données (AppDataPaths de #1), pas dans settings.json. Contenu : contexte machine, BenchVersion, unités physiques (GFLOPS, Go/s, ns, Mo/s, IOPS), CV, séries capteurs sous-échantillonnées, raisons de bridage. Schéma stable et lu de façon tolérante : #12 s'en servira.
- Page Bench (emplacement selon #1), chargée à la première ouverture : tests cochables indépendamment, durée estimée, progression, courbes en direct, historique. Un test indisponible affiche sa raison (règles 1 à 3).
- Aucun outil tiers propriétaire embarqué ; DiskSpd (MIT) seulement pour valider nos chiffres en développement.

## 5. Pièges
- Worker High sur tous les cœurs : il peut affamer le relevé (thread AboveNormal de SamplingLoop, processus Normal), aveugler les arrêts de sécurité et déclencher à tort le chien de garde des ventilateurs (15 s sans relevé → BIOS). Mesure-le. Parades : priorité de l'app relevée pendant le test, ou un thread de moins.
- Snapshot resservi jusqu'à 3 fois après une exception : dédoublonner par CapturedAtUtc ; tester GroupsRead.
- Ne pas passer par SetManualInterval (réglage de l'utilisateur). Mode éco : un IBackgroundSensorConsumer qui déclare les groupes utiles pendant le test.
- Des Go aléatoires ressemblent à un rançongiciel pour un EDR : un seul fichier, hors dossiers protégés.
- Portable : règle 5, ne jamais « aider » le refroidissement en écrivant dans le contrôleur embarqué. Sans PawnIO : bench mesuré, mais « sans fréquences ni températures CPU ».
- HardwareMonitorService (1280 lignes) et MonitoringViewModel (1024) : rien dedans, tout dans src/PCPerfSuite.Core/Benchmark/.

## 6. Questions à me poser
1. Périmètre V1 : CPU + RAM + disque ici, GPU et test combiné en #11 ?
2. Exe worker séparé (second binaire à signer, Smart App Control) ou mode du même exe (l'étude de F4 prévoit `PCPerfSuite.exe --stress-gpu`) ? Un seul choix pour #11, #15 et #16. Recommandé : ce que dicte la décision de signature de docs/decisions.md ; sans signature proche, le même exe.
3. Unités physiques, points, ou les deux ?
4. Écriture sur le disque système autorisée ? Volume max (1, 4 ou 8 Go) ? HDD et clés USB testés ?
5. Sur batterie : refuser, ou autoriser avec « non représentatif » ?
6. Quelles machines de calibration (AMD, portable) ?

## 7. Périmètre
Dans : moteur de charge, modes secondaires, protocole, benchs CPU/RAM/disque, résultats, page, diagnostic. Hors :
- bench GPU et test combiné (#11 Bench GPU et test combiné « alimentation ») ;
- verdicts normal / pas normal et rapport (#12 Moteur de diagnostic déterministe et rapport) ;
- compte rendu (#13 Mode technicien et rédaction IA du compte rendu) ;
- OC automatique (#15 OC automatique GPU, #16 Curve Optimizer AMD automatique) : le moteur doit seulement les rendre possibles.

## 8. Livrables et méthode
Branche feature/bench-cpu-ram-disque depuis develop, commits en français au présent comme l'historique. Tests xUnit sur la logique pure : médiane et CV, préconditions, permutation à cycle unique, alignements et plafonds disque, messages du tube, battement de cœur, somme de contrôle des noyaux, modes secondaires (argument inconnu refusé), gestionnaire de reprise, lecture tolérante. `dotnet build -c Release` et `dotnet test` verts. Ligne « Bench » par un fournisseur de lignes (#1) : tests disponibles ici, et pourquoi pas les autres. Section README. Seuils et durées « expérimental » (règle 6). `/review-max` avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Sur mon i5-14600K / RTX 5070 Ti / ASUS TUF B760-PLUS WIFI (tests des agents faits sur un i5-13500T), puis un AMD et un portable :
- Mono épinglé sur un cœur P ; multi sur 20 threads ; écart rafale/soutenu cohérent avec PL1/PL2.
- 3 passes à CV < 3 % au repos.
- RAM : débits et latence du même ordre qu'un outil connu.
- NVMe proche de CrystalDiskMark, fichier supprimé, lien PCIe relevé.
- Arrêter et fermeture brutale de l'app : le worker s'arrête.
- App tuée pendant le test disque : au relancement, fichier supprimé et test noté interrompu.
````

### 11. Bench GPU et test combiné « alimentation »

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #10, #4
- Branche : `feature/bench-gpu-alimentation`
- Matériel pour vérifier : Machine de Denis (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI), plus une Radeon et une Arc si possible

````text
# Bench GPU et test combiné « alimentation » (F1, partie 2)

## 1. Objectif
Suite de F1 : « ajouter une possibilité de bench cpu/gpu/ram/disque/power etc sélectionnables indépendamment ». Après les benchs CPU, RAM et disque de #10, tu ajoutes deux tests, sélectionnables indépendamment dans la page Bench :
- le bench GPU : calcul, bande passante VRAM, stabilité ;
- le test combiné CPU + GPU, qui donne des indices sur l'alimentation.
Le test combiné n'est pas une mesure du bloc (seuls de rares blocs connectés donnent leur puissance) : ne le présente jamais comme tel.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis, si elles sont présentes (Grep, pas de lecture intégrale) : « ### F1 » (GPU et « ALIMENTATION ») et « ### F4 » (charge de test) de docs/etudes/etude-faisabilite-2026-09.md, puis « ### F1 » de docs/etudes/cartographie-code-2026-09.md.
- Lis ce que les autres conversations ont réellement livré : tu t'y branches, tu ne dupliques rien.
  - #10 : worker, modes secondaires, protocole ;
  - #4 : raisons de bridage, SessionJournal, StartupRecovery ;
  - #2 : GpuIdentity, sécurité thermique en logique pure ;
  - #8 : bail de réglage.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille L : plan en mode plan avant tout code.

## 3. Ce qui existe déjà et à réutiliser
- **#10** : le worker (PCPerfSuite.BenchWorker ou mode du même exe), les modes secondaires, le protocole commun (préchauffe, passes, arrêts, « Arrêter »), l'enregistreur, la cadence temporaire, BenchVersion, la charge CPU. Vérifie les noms réels.
- **#4** : raisons de bridage GPU (NVAPI, IGCL), journaux Windows, qualification d'incident, SessionJournal et StartupRecovery, liens PCIe.
- **#2** : GpuIdentity (nom, marque, identifiants PCI si lus), sécurité thermique en logique pure. **#8** : TuningLease.
- **Capteurs** (src/PCPerfSuite.Core/Hardware/HardwareModels.cs) : GpuSnapshot (CoreTempC, HotSpotTempC, CoreClockMhz, PowerWatts, FanRpm, FanPercent), PsuPowerWatts (bloc Corsair connecté seulement), MotherboardSnapshot.Voltages (+12 V brut). HardwareSnapshot ne suit qu'un GPU, celui que pilote GpuControlService, retrouvé par son nom (HardwareMonitorService.PreferredGpuName, ≈78).
- **GpuControlService** : GetActiveLimit (≈118, NVIDIA seulement), GetSnapshot (limite de puissance), GetOverclock (limite de température, NVIDIA et Intel).
- **Autres** : BatterySnapshot (PowerOnline, Discharging), MachineInfo.Current (IsLaptop), FanInventory.Of.

## 4. Approche retenue
- **ComputeSharp 3.x** (MIT, cible net8.0, shaders de calcul DX12 en C#), dans le worker seulement : un TDR ou un DEVICE_REMOVED ne tue que lui. Vérifie la version et les DLL natives à embarquer ; ajoute-le au THIRD-PARTY-NOTICES.
- **Choix de la carte** : la dédiée, choisie par LUID via GraphicsDevice.QueryDevices. Elle est rapprochée du GPU que suit le relevé par GpuIdentity (#2), que tu enrichis du LUID (mets sa ligne de docs/decisions.md à jour). Dans le doute, N/D plutôt qu'une mesure attribuée à la mauvaise carte.
  - Refuse tout device dont IsHardwareAccelerated vaut false. Sinon ComputeSharp bascule seul sur WARP, donc sur le CPU : N/D « seul un rendu logiciel WARP est disponible ».
  - Sans DX12/SM 6.0 (iGPU anciens ou pilotes figés, non vérifié) : N/D avec la raison. Vérifie ces noms d'API avant de t'y fier.
- **Noyaux** :
  - FMA FP32, en GFLOPS ;
  - copie de grands buffers, en Go/s, avec motifs écriture/relecture vérifiés, bien au-delà du L2 et de l'Infinity Cache (proposition à calibrer : au moins 1 Go, au plus 25 % de la VRAM libre) ;
  - entier déterministe, vérifié exactement contre une référence ; en FP32, somme de contrôle identique d'une boucle à l'autre.

  Dispatches de moins de 100 ms (TDR de Windows : 2 s), chronométrés par lots. C'est l'API que l'OC auto (#15) réutilisera. Elle est paramétrable (durée, intensité, cycles charge/repos de 1 s) et distingue « erreur de calcul » et « device perdu ».
- **Charge soutenue** : 3 à 5 min en boucles de 30 s. Stabilité = pire boucle / meilleure boucle, seuil 97 % (principe 3DMark). Garde rafale et soutenu, avec fréquence, puissance, température et lien PCIe lu sous charge (#4 ; il descend au repos).
- **Arrêts**, à calibrer, sur la logique pure de sécurité thermique de #2 :
  - hot spot à sa limite (proposition : 105 °C tenus 5 s), sinon cœur au-delà de la limite du pilote ;
  - ventilateur GPU à 0 tr/min sous charge établie (zero RPM au repos) ;
  - batterie sous 30 %.
  En combiné, aussi ceux de #10. Bail de réglage de #8 tenu pendant chaque test : ni groupe ni bascule automatique.
- **Test combiné**, 3 à 5 min : charge CPU de #10 et noyau GPU ensemble.
  - Consentement à chaque lancement (« un bloc faible peut éteindre le PC ») ; jamais coché par défaut ni lancé automatiquement.
  - Avant le test : une entrée EnCours du SessionJournal de #4 (composant « test combiné »), passée à Terminé en fin normale. Aucun fichier marqueur à part.
  - Au démarrage suivant, ton gestionnaire StartupRecovery (#4) reçoit la qualification depuis l'heure de l'entrée. Un Kernel-Power 41 avec BugcheckCode 0 et PowerButtonTimestamp 0 donne l'événement « arrêt brutal pendant le test combiné », pour #12 ; sans lui, « test interrompu ».
  - Pendant le test, relever : InsufficientPower (NVAPI, connecteur manquant), PSU_ALERT (IGCL, non vérifié sur Arc), HwPowerBrake (NVML, best-effort, si #4 l'a branché), perf cap, batterie qui se décharge sur secteur.
  - Le +12 V ne compte qu'en relatif (repos puis charge, même capteur), avec un repos plausible entre 11,2 et 12,6 V (ATX 3.0). « Voltage #n » d'une carte inconnue : non fiable.
- **Règles** : sur portable, aucune écriture dans le contrôleur embarqué pour « aider » le refroidissement (règle 5) ; sur bureau, courbes de ventilation de l'app actives et notées ; chaque absence dit sa raison (règles 1 à 4) ; un échec est un résultat, jamais une exception (règle 2).

## 5. Pièges connus
- RTX 50 : le hot spot ne serait plus exposé (à vérifier sur la 5070 Ti) ; prévois un repli sur la température cœur.
- Portable hybride : la carte chargée doit être celle du relevé. Sur batterie, le bridage est voulu (AC_BATT) : résultat « non représentatif ».
- GpuControlService n'a pas de verrou : aucun appel NVAPI depuis un thread de bench.
- Une FMA pure consomme plus qu'un jeu : plafonne sa durée.
- 5 TDR en une minute donnent un écran bleu 0x117 : arrête-toi au premier DEVICE_REMOVED.

## 6. Questions à me poser
1. ComputeSharp accepté comme dépendance (recommandé : oui, dans le worker seul) ?
2. Test combiné en opt-in avec avertissement (recommandé), ou retiré du parcours ?
3. Test combiné sur portable (recommandé : sur secteur seulement, arrêts plus bas) ?
4. Durées (recommandé : 5 min pour le GPU, 3 min pour le combiné) ?

## 7. Périmètre et hors périmètre
Dans le périmètre : bench GPU, test combiné, gestionnaire de reprise, lignes de compatibilité, README.
Hors périmètre :
- benchs CPU, RAM et disque (#10 « Moteur de charge et bench CPU / RAM / disque ») ;
- lecture des signaux et journal de session (#4 « Socle de signaux : bridage, journaux Windows, liens PCIe ») ;
- verdicts (#12 « Moteur de diagnostic déterministe et rapport ») : tu produis des mesures ;
- #13 « Mode technicien et rédaction IA du compte rendu », #15 « OC automatique GPU : trois profils sûr / classique / agressif », rendu 3D (V2).

## 8. Livrables et méthode
- Branche feature/bench-gpu-alimentation, créée depuis develop. Commits en français au présent.
- Tests xUnit sur la logique pure : stabilité, arrêts, gestionnaire de reprise sur des entrées synthétiques, +12 V relatif, choix de la carte (LUID, WARP refusé).
- dotnet build -c Release et dotnet test verts.
- Lignes « Bench GPU » (carte retenue, WARP refusé, raison) et « Test combiné » dans Paramètres › Compatibilité de ce PC, par des fournisseurs de lignes (#1) ; section du README ; « expérimental » sur les seuils et les marques non vérifiées (règle 6).
- /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Tes essais tournent sur un autre PC (i5-13500T). Sur ma machine (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI), je vérifierai :
- la 5070 Ti choisie (ni WARP ni l'iGPU), avec des scores et une stabilité ≥ 97 % plausibles ;
- le hot spot lu, ou le repli ;
- « Arrêter » effectif en moins de 2 s ;
- le test combiné sans coupure, et aucun faux « arrêt brutal » au relancement si je tue le worker ;
- un +12 V plausible, ou marqué non fiable ;
- une Radeon et une Arc, si possible.
````

### 12. Moteur de diagnostic déterministe et rapport

- Modèle / effort : **Fable 5.1 / xhigh** · taille L · relecture /review
- Dépend de : #4, #10 ; #11 pour le GPU et l’alimentation
- Branche : `feature/diagnostic`
- Matériel pour vérifier : Plusieurs machines réelles pour calibrer les règles (au minimum la machine de Denis et le PC i5-13500T, idéalement Intel et AMD, bureau et portable) ; sinon tout reste expérimental

````text
# Moteur de diagnostic déterministe et rapport (F2)

## 1. Objectif
F2 : « IA qui après le bench dit si un ou plusieurs composants sous-performent par rapport aux scores normaux, et l'IA doit identifier si c'est dû à un mauvais refroidissement, un manque de puissance fourni par l'alimentation et si c'est possible en regardant les moyens de refroidissement et dire s'ils sont suffisants et aider au rapport/compte rendu ». Tu en construis le cœur : un moteur de règles déterministe (mesures et signaux → constats) et le modèle de rapport. Pas de LLM : la rédaction IA vient en #13 et ne décide jamais du verdict.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis, si elles sont présentes (Grep) : « ### F2 », et « ### F3 » pour le rapport, dans docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md.
- Lis ce que #4, #10 et #11 ont livré.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille L : plan en mode plan avant tout code.

## 3. Ce qui existe déjà et à réutiliser
- **CPU** : CpuControlService.ReadPowerLimits (CpuPowerLimitSnapshot), CpuFirmwareDefaultsStore (limites du BIOS), CpuPlatform (Vendor, Family, Model, IsHybrid), AmdCodeName (DetectCodeName est privé dans AmdSmuBackend : expose-le sans dupliquer la table).
- **GPU** : GpuControlService.GetSnapshot et GetOverclock (limites de puissance et de température).
- **Refroidissement** (src/PCPerfSuite.Core/Hardware/Fans/) : FanInventory.Of, FanCategory (Cpu, Gpu, Pump, Case), EmptyHeaderDetector.
- **Machine** : MachineInfo.Current (non testable : passe le châssis en entrée), BatterySnapshot (PowerOnline, Discharging, FullChargeMWh et DesignMWh lus par BatteryReader, HealthPercent), DiskHealthService.CheckAsync, MemoryModuleReader ; les motifs d'absence (MetricDefinition.UnavailableHint, côté App) arrivent au moteur comme données.
- **Événements et incidents (#4)** : SessionJournal et IncidentClassifier (Core/Safety).
- **CompatibilityViewModel** (693 lignes, 7 dépendances) : CompatibilityRow avec IsPersonal (≈28), BuildRows (≈106), MemoryRow, DiskRow et BatteryRow statiques (≈344-420), CopyReport (≈621-692), qui masque les données personnelles. ICompatibilityRowProvider (Core/Compatibility, #1).

## 4. Approche retenue
- **src/PCPerfSuite.Core/Diagnostics/** : fonctions pures sur des séries horodatées, sans accès matériel. Les lecteurs fournissent, le moteur tranche.
- **Finding** : Id stable en chaîne, composant, verdict (Très bien, Normal, À surveiller, Anormal, Non mesurable), cause probable, confiance (élevée, moyenne, faible), preuves chiffrées (valeur, unité, source, mesurée ou déduite), action conseillée. Un « Non mesurable » porte une raison typée selon les trois cas de la règle 3. IsExperimental marque toute règle non calibrée (règle 6).
- **Scores normaux, sans scraping** :
  - D'abord les attendus internes, cœur de la V1 :
    - fréquence tenue face aux ratios turbo ou au boost du pilote ; puissance face à PL1, PPT ou la limite GPU ; température face à TjMax ;
    - disque face à l'interface négociée : SATA ≈ 0,55 Go/s, PCIe 3.0 x4 ≈ 3,5, 4.0 x4 ≈ 7, 5.0 x4 ≈ 14 ;
    - RAM face au théorique (MT/s × 8 octets × canaux) : sous 60 % du double canal, « compatible avec un seul canal » ;
    - écart entre rafale et soutenu, stabilité à 97 %.
  - Ensuite un étalon « même configuration », enregistré par le technicien sur une machine saine (clé : modèles et BenchVersion).
  - Puis une table de référence JSON, versionnée et expérimentale, dont tu livres le format.

  L'estimation par les specs (± 20 %, incertaine) reste un garde-fou, jamais un verdict. Aucun score PassMark, 3DMark ou Geekbench.
- **Contrôles de configuration** (signaux de #4) : lien PCIe sous son maximum, lu sous charge ; écran d'un PC de bureau branché sur l'iGPU ; XMP/EXPO (heuristique, jamais affirmé) ; microcode Intel 13e/14e bureau sous 0x12F ; WHEA-Logger, TDR 4101, BugCheck 1001, Kernel-Processor-Power 37, erreurs disque ; SMART. Les événements et incidents (dont Kernel-Power 41) se lisent depuis SessionJournal et IncidentClassifier de #4, pas depuis les journaux Windows.
- **Règles d'attribution** :
  - Refroidissement limitant : bit thermique ou TjMax atteint sous PL1/PPT, ou perf cap Temperature (GpuPerformanceLimit). Ventilateurs à 100 % : saturé ou encrassé. Ventilateurs bas : courbe ou BIOS. CPU à 0 tr/min : critique. Chiffres : temps avant bridage, pente, °C au-dessus du repos par W.
  - Limite de puissance : PL1, PPT ou perf cap Power atteint sous TjMax − 10 °C. Le refroidissement suffit ; normal si ce sont les limites du BIOS.
  - Suspicion d'alimentation, jamais une certitude :
    - indices : marqueur de #11 et Kernel-Power 41 code 0, InsufficientPower, PSU_ALERT, HwPowerBrake, chute relative du +12 V, batterie qui se décharge sur secteur ;
    - formulé « indices compatibles avec… », avec un niveau de confiance ;
    - avec ce qu'on ne peut pas dire : puissance délivrée, pics à la milliseconde, bloc, VRM ou câble.
  - « Refroidissement suffisant pour tenir les limites d'usine ? » : conclu par le constat « Moyens de refroidissement » ci-dessous. Ventirad, pâte et flux d'air ne sont pas identifiables par la mesure.
  - Normal par conception : Zen 4/5 de bureau (Raphael, GraniteRidge) à 95 °C (89 °C sur X3D, repéré par le nom), portable fin qui se bride, perf cap Power NVIDIA (limite de la carte, pas le bloc).
- **Constat « Moyens de refroidissement »** (réponse à « en regardant les moyens de refroidissement et dire s'ils sont suffisants ») : il reprend l'inventaire relevé (ventilateurs par catégorie avec tr/min et % au repos et sous charge, pompe détectée : watercooling probable, prises vides par EmptyHeaderDetector, zero RPM du GPU au repos) ; il accepte une déclaration facultative du technicien (ventirad, AIO 240/280/360, ventilateurs de boîtier), marquée « déclarée » dans les preuves ; il conclut « suffisants », « limites » ou « insuffisants » pour tenir les limites d'usine.
- **Constat « Usure de la batterie »** : FullChargeMWh / DesignMWh, déjà lus par BatteryReader (BatterySnapshot.HealthPercent) ; absent sur un PC sans batterie, « Non mesurable » avec sa raison si la capacité nominale n'est pas fournie ; seuils non calibrés marqués expérimentaux.
- **Source unique** : l'interface ICompatibilityRowProvider existe (#1) : déplace MemoryRow, DiskRow, BatteryRow et CopyReport dans Core, avec le modèle de ligne s'il est encore côté App : construit depuis les ViewModels, CopyReport est intestable. CompatibilityViewModel ne fait qu'afficher ; toute nouvelle ligne s'ajoute par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une 8e dépendance.
- **DiagnosticReport JSON v1** (System.Text.Json, enums en chaînes, SchemaVersion ; brique partagée, inscris sa ligne dans docs/decisions.md) est la source de vérité : identité sans numéro de série, conditions (secteur, BenchVersion, PawnIO, administrateur), capacités, constats, séries résumées, événements filtrés (depuis SessionJournal et IncidentClassifier de #4). Rendus : HTML autonome (CSS et SVG en ligne) et texte, qui devient « Copier le rapport ». Rapports et étalons stockés dans un sous-dossier des données via AppDataPaths (#1), jamais un chemin %LOCALAPPDATA%\PCPerfSuite codé en dur (#13 y ajoutera la racine portable).

## 5. Pièges connus
- N'accuse jamais à tort une alimentation, un revendeur ou un fabricant.
- TjMax : HardwareMonitorService exclut exprès les sondes « Distance to TjMax » (≈434-444) ; prends-le dans #4.
- Radeon : ADLX ne donne aucune raison de bridage, donc inférence (fréquence, hot spot, TBP) à confiance faible.
- +12 V d'une carte inconnue (« Voltage #n ») : non fiable. PM table AMD : versions cartographiées seulement.
- Température ambiante inconnue : demande-la au technicien ou estime-la au repos.
- Séries : dédoublonne par CapturedAtUtc, filtre par GroupsRead.
- HTML : encode toute chaîne lue sur le PC ; retire des journaux les noms d'applications.

## 6. Questions à me poser
1. « L'IA rédige, elle ne décide jamais du verdict » : accepté (recommandé) ? (seulement si docs/decisions.md ne la tranche pas)
2. Verdicts prudents avec niveau de confiance (recommandé) ?
3. Un ton de rapport, ou deux, client et technicien (recommandé : deux, même modèle) ?
4. Qui construit la base de référence (recommandé : attendus et étalon en V1, ta table ensuite, télémétrie plus tard) ?

## 7. Périmètre et hors périmètre
Dans le périmètre : moteur, étalon, format de la table, DiagnosticReport v1, rendus HTML et texte, extraction des lignes, constats affichés après un bench (dont « Moyens de refroidissement » et « Usure de la batterie »).
Hors périmètre : signaux (#4 « Socle de signaux : bridage, journaux Windows, liens PCIe ») ; benchs (#10 « Moteur de charge et bench CPU / RAM / disque », #11 « Bench GPU et test combiné “alimentation” ») ; parcours, PDF, clé USB et LLM (#13 « Mode technicien et rédaction IA du compte rendu ») ; télémétrie.

## 8. Livrables et méthode
- Branche feature/diagnostic, créée depuis develop. Commits en français au présent.
- Tests xUnit (modèle : CpuMaxWattsResolver ; TestData.cs d'App.Tests) : un par règle, plus les cas « ne pas accuser » (Zen 4 à 95 °C, portable, perf cap Power, GPU à 0 tr/min au repos).
- dotnet build -c Release et dotnet test verts.
- Ligne « Diagnostic » dans Paramètres › Compatibilité de ce PC, ajoutée par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : règles actives, expérimentales, non mesurables et pourquoi ; section du README ; « expérimental » sur chaque seuil non calibré.
- /review avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Tes essais tournent sur un autre PC (i5-13500T). Sur ma machine (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI), je vérifierai :
- le constat sur les limites CPU (la TUF peut imposer les siennes au lieu de 125/181 W) ;
- des constats GPU cohérents avec le perf cap ;
- un constat « Moyens de refroidissement » cohérent avec ce qui est monté, avec et sans déclaration ;
- un HTML lisible hors ligne, sans numéro de série ni compte ;
- que « Copier le rapport » garde ses lignes d'avant.

Sur le i5-13500T (PL1 de 35 W par conception), la limite de puissance ne doit pas sortir « Anormal ».
````

### 13. Mode technicien et rédaction IA du compte rendu

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #12, #10, #11
- Branche : `feature/mode-technicien`
- Matériel pour vérifier : Un PC « client » propre (sans .NET ni PawnIO) et une clé USB ; une clé API Anthropic pour tester la couche IA ; la machine de Denis pour le rapport rédigé

````text
# Mode technicien et rédaction IA du compte rendu (F3 et rédaction IA de F2)

## 1. Objectif
F3 : « mon but est d'en faire au final une vraie app où un tech LDLC par exemple peut juste la lancer sur un PC et elle fait un rapport de ce qui est normal, pas normal ou très bien ». Plus la rédaction de F2 : « aider au rapport/compte rendu ». Trois livrables :
- un parcours guidé lancé depuis une clé USB ;
- un rapport HTML/PDF ;
- une IA facultative qui rédige d'après les constats de #12 sans changer un verdict.
Le PC du client doit être rendu tel qu'il était.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1), notamment : sens de « IA », signature de code, licence, public et fonctions du mode technicien. Ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis, si elles sont présentes (Grep) : « ### F3 » et « ### F2 » (partie IA) dans docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md.
- Lis ce que #4 et #10 à #12 ont livré, et les briques de #1 (AppDataPaths, registre des modifications de Windows, fournisseurs de lignes) et de #7 (OfficialInstaller par source, outils portables).
- Charge la compétence /claude-api avant toute ligne qui utilise le SDK Anthropic : ne devine ni une API, ni un identifiant de modèle, ni un nom de type.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille L : plan en mode plan avant tout code.

## 3. Ce qui existe déjà et à réutiliser
- **#12** : DiagnosticReport v1, constats, rendus HTML et texte. **#10, #11** : benchs et test combiné.
- **PawnIO** :
  - PawnIoDriver.SetupSource (« PawnIO_setup.exe », « -install -silent », éditeur « namazso »), IsInstalled ;
  - OfficialInstaller : dossier temporaire réservé aux processus élevés, fichier verrouillé, signature vérifiée, puis lancement (LaunchAsync, internal, ≈223). Verify et RestrictToElevatedProcesses sont privés : reprends l'entrée « installeur local » de #7 si elle existe, sinon ajoute-la, plutôt que de dupliquer la vérification ;
  - AuthenticodeVerifier.Check(path, openHandle).
- **Droits** : ElevationHelper.IsAdministrator() ; app.manifest en requireAdministrator.
- **Données** : AppDataPaths (#1) résout le dossier de données ; vérifie par Grep qu'aucun chemin ajouté de #2 à #12 n'y échappe. OfficialInstaller passe par %TEMP%\PCPerfSuite. Hors du profil : %ProgramData%\PCPerfSuite (outils portables de #7, fichier de bench de #10).
- **Traces système possibles** : registre des modifications de Windows (#1 : parking #5, animations #6, plan d'alimentation #8, et ce que #17 à #22 ajouteront) ; sessions ETW (ProcessIoTracer, surveillance de processus de #21) ; tâches planifiées (StartupTask, reprise d'OC de #15) ; pilote PawnIO ; SessionJournal et StartupRecovery (#4).

## 4. Approche retenue
- **Parcours guidé**, étapes visibles, « Arrêter » toujours présent, rien d'automatique :
  - Express, environ 5 min : inventaire, configuration, journaux, SMART, courts tests CPU, RAM et disque système ;
  - Standard, environ 15 min, la cible : Express, plus CPU soutenu 3 min, GPU 5 min, test combiné opt-in ;
  - Complet, 30 à 60 min.
- **PDF** : Microsoft.Web.WebView2, CoreWebView2.PrintToPdfAsync sur le HTML de #12. Sans runtime WebView2 : « ouvrir le HTML et imprimer en PDF ». Dossier de données WebView2 explicite, supprimé après. QuestPDF écarté (licence) ; PDFsharp/MigraDoc (MIT) en repli seulement.
- **Clé USB** :
  - publication self-contained win-x64 (le csproj est en SelfContained=false, ≈14), worker de #10 compris. En dossier plutôt qu'en single-file : IncludeNativeLibrariesForSelfExtract extrait les natives dans %TEMP% (à vérifier). ARM64 hors V1 ;
  - portable.flag à côté de l'exe : AppDataPaths (#1) prend alors sa racine sur la clé. Réglages, journaux, journal de session et rapports y vont ;
  - en mode portable : pas de StartupTask, et la croix quitte l'app (MinimizeToTrayOnClose vaut true par défaut) ;
  - aucune session ETW (ProcessIoTracer, surveillance de #21) : une session orpheline survit à un plantage ;
  - rien dans %ProgramData% : fichier de bench sur la clé, ou supprimé même après plantage par StartupRecovery de #4 ; aucun outil de #7 décompressé hors de la clé ;
  - en fin de parcours : « Tout rétablir » par le registre des modifications de #1. Le rapport liste ce qui a été rendu, et dit en clair ce qui ne peut pas l'être (réglage permanent sans origine connue).
- **PawnIO sur la clé** :
  - installeur copié sur la clé, ouvert verrouillé et vérifié comme dans OfficialInstaller, puis proposé au lancement ;
  - désinstallation proposée en fin de diagnostic, seulement si la session l'a installé. La commande est inconnue (« -uninstall -silent » ? vérifie dans PawnIO.Setup avant de t'y fier) ; sans certitude, ne la propose pas ;
  - sans PawnIO, le rapport indique « thermique CPU et ventilateurs carte mère non mesurés ».
- **Identité** : ajoute la lecture du BIOS, de la carte mère (Win32_BaseBoard) et des versions de pilotes GPU, absente du code, dans un lecteur que #14 et #18 réutiliseront. Numéros de série exclus par défaut (option technicien) ; nom du PC et compte en IsPersonal.
- **Règles** : sur portable, aucune écriture dans le contrôleur embarqué pendant le parcours (règle 5). Le rapport reprend les motifs d'absence du diagnostic sans copie divergente (règles 3 et 4). Tout verdict non calibré affiche « expérimental » (règle 6).
- **Administrateur exigé** en V1. La signature de code suit docs/decisions.md ; si elle n'y est pas tranchée, c'est une question (6.2), pas du code.
- **Couche IA facultative** (dans le sens de « IA » de docs/decisions.md : elle rédige, elle ne décide jamais) :
  - SDK NuGet officiel « Anthropic » (licence à vérifier, compatible avec celle de PCPerfSuite). Modèle choisi à la question 8 ; identifiant relevé dans /claude-api, mis en configuration, jamais codé en dur ;
  - sortie structurée (JSON conforme à un schéma, par le mécanisme que décrit /claude-api) : résumé client, explication technicien, actions, et pour chaque phrase les ids des constats cités ;
  - entrée : le JSON des constats, rien d'autre ;
  - validation : chaque id cité existe, chaque nombre cité figure dans l'entrée ; sinon rejet et repli sur le gabarit déterministe. Même repli sur un refus du modèle, sans réseau, sur 429 ou délai dépassé ;
  - trois modes :
    - hors ligne par défaut, avec des gabarits français ;
    - clé de l'utilisateur chiffrée par DPAPI (ProtectedData, CurrentUser ; paquet System.Security.Cryptography.ProtectedData), mais saisie à chaque session et gardée en mémoire sur clé USB ;
    - proxy serveur, côté client seulement (permis par les Commercial Terms).
    Jamais de clé dans l'exe ; jamais de connexion par un abonnement Claude.ai (interdit) ;
  - confidentialité : ni nom du PC, ni compte, ni séries, ni MAC ou IP, ni chemins, ni processus. Aperçu et consentement à chaque envoi (rétention de 30 jours au plus, pas d'entraînement) ;
  - injection de prompt : les chaînes lues sur le PC sont des données, encadrées et tronquées, jamais des instructions ;
  - journalise les tokens (estimé : 0,05 à 0,20 $ par rapport, à mesurer).

## 5. Pièges connus
- Responsabilité : formulations prudentes, version du bench, conditions du test, mention « diagnostic indicatif ».
- EDR et antivirus : exe sur clé, élévation, pilote, ETW et écritures massives.
- Azure Artifact Signing : organisations de l'UE oui, particuliers seulement aux États-Unis et au Canada (à revérifier).
- DPAPI CurrentUser : l'app élevée peut tourner sous un autre compte que la session (SessionUser).
- Un propriétaire du registre des modifications peut échouer à rétablir : le parcours continue, le rapport le dit (règle 2).

## 6. Questions à me poser
1. Partenariat réel (LDLC ou autre) ou persona (recommandé : persona tant qu'il n'y a pas d'accord) ?
2. Entité de signature, seulement si docs/decisions.md ne la tranche pas (recommandé : une société, pas un particulier) ?
3. PawnIO installé sur le PC d'un client, puis désinstallé (recommandé : oui, avec accord explicite) ?
4. Administrateur exigé (recommandé : oui en V1) ?
5. PDF obligatoire (recommandé : PDF, repli HTML) ?
6. Numéros de série (recommandé : exclus par défaut, option technicien) ?
7. Durée cible (recommandé : Standard, 15 min) ?
8. Mode IA (recommandé : hors ligne plus clé utilisateur, proxy plus tard), budget par rapport, et modèle (Opus ou Sonnet, dans la version en vigueur selon /claude-api) ?

## 7. Périmètre et hors périmètre
Dans le périmètre : parcours, PDF, mode portable, « Tout rétablir », PawnIO sur la clé, publication, identité, couche IA, gabarits, consentements.
Hors périmètre :
- règles et verdicts (#12 « Moteur de diagnostic déterministe et rapport ») ;
- benchs (#10 « Moteur de charge et bench CPU / RAM / disque », #11 « Bench GPU et test combiné “alimentation” ») ;
- OC en mode technicien : tranché dans docs/decisions.md (recommandé : non) ;
- serveur proxy, lanceur sans administrateur, télémétrie ; signature effective.

## 8. Livrables et méthode
- Branche feature/mode-technicien, créée depuis develop. Commits en français au présent.
- Tests xUnit : validation IA (id inconnu, nombre inventé, refus), gabarits, masquage, racine portable d'AppDataPaths, « Tout rétablir » (propriétaire qui échoue, liste rendue au rapport).
- dotnet build -c Release et dotnet test verts.
- Lignes « Mode portable » et « Rédaction IA » dans Paramètres › Compatibilité de ce PC, par des fournisseurs de lignes (#1) : mode, clé présente ou non, jamais la clé. Section du README ; « expérimental » où c'est non vérifié.
- /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Tes essais tournent sur un autre PC (i5-13500T). Je vérifierai :
- sur un PC « client » propre (sans .NET ni PawnIO), depuis une clé : PawnIO, parcours Standard, PDF, « Tout rétablir », désinstallation. Ensuite, aucune trace dans %LOCALAPPDATA%, %TEMP%, %ProgramData%\PCPerfSuite, le Planificateur de tâches, les sessions ETW (logman query -ets) ni les stratégies QoS ;
- sur ma machine (i5-14600K, RTX 5070 Ti) : un rapport rédigé avec ma clé, le repli sur le gabarit pour une réponse invalide, puis le mode hors ligne ;
- la réaction de SmartScreen et de Smart App Control à l'exe non signé.
````


## Lot E · Overclocking

À faire après le lot D, qui apporte les noyaux de calcul vérifiés. #14 et #17 peuvent commencer plus tôt. #15 et #16 provoquent des plantages volontaires et demandent du matériel de test.

### 14. Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #2 ; #8 conseillé
- Branche : `feature/verification-oc-gpu`
- Matériel pour vérifier : Cartes Radeon RDNA2/3/4 et Arc A/B (achat, prêt ou testeurs), avec le dernier pilote WHQL et un pilote d'environ 12 mois ; la RTX 5070 Ti de Denis pour le chemin NVIDIA de l'outil

````text
# Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc (F6)

## 1. Objectif
F6 : « OC sur toutes les marques de cartes : AMD, Intel ». Lecture retenue : les cartes graphiques Radeon et Arc (question 1). Le code existe déjà (AdlxGpuBackend, IgclGpuBackend), marqué expérimental. Il reste à :
- le vérifier sur de vraies cartes ;
- corriger ce que ces essais révéleront ;
- le passer en « vérifié » génération par génération.
Sans les cartes, F6 ne peut pas se terminer par du code seul. Tu poses aussi les sessions NVAPI, ADLX et IGCL partagées dont #17 et #20 auront besoin.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis, si elles sont présentes (Grep) : « ### F6 » et les remarques transverses qui suivent, dans docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md.
- Regarde ce qu'ont livré :
  - #2 : GpuIdentity, sécurité thermique GPU, relecture demandé / retenu, SettingsMatchCurrentGpu ;
  - #8 : bail de réglage ;
  - #4 : SessionJournal, StartupRecovery, verrou NVAPI, bindings IGCL ;
  - #13 : lecture des versions de pilotes.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille L : plan en mode plan avant tout code.

## 3. Ce qui existe déjà et à réutiliser
- IGpuTuningBackend (src/PCPerfSuite.Core/Hardware/Gpu/) : le contrat commun, qui ne lève jamais.
- AdlxGpuBackend et AdlxNative :
  - vtable TuningServices (ResetToFactory = 5, IsSupportedManual* = 8 à 11, GetManual* = 14 à 17) ;
  - tension absolue ou en décalage, déduite de range.Min <= 0 (≈313) ;
  - ventilateur en vitesse fixe par courbe aplatie, restaurée depuis une copie en mémoire (≈371-405) ;
  - IADLXGPU ne lit que Type (5) et Name (7).
- IgclGpuBackend :
  - V1/V2 et unités (≈244-330 ; « 3 » = CTL_UNITS_VOLTAGE_VOLTS dans igcl_api.h, converti en mV ≈307-326) ;
  - ctlOverclockWaiverSet (≈99), ctlOverclockResetToDefault avec repli (≈462) ;
  - IgclNative.DeviceAdapterProperties expose déjà PciDeviceId et DriverVersion.
- AdlxProbeGuard.RunGuarded : témoin disque, limité à l'initialisation ; ligne de diagnostic associée (CompatibilityViewModel ≈174-182).
- GpuControlViewModel : IsExperimentalBackend => Vendor is Amd or Intel (≈147), DescribeUnsupported (≈356), SettingsMatchCurrentGpu (≈268).
- GpuControlService : GetOverclock, les Try*, RestoreOverclockDefaults, TryRestoreFanAuto.
- ILaptopFanProvider.IsVerified (src/PCPerfSuite.Core/Hardware/LaptopFans/ILaptopFanProvider.cs) : le modèle du drapeau.
- Aucune session partagée aujourd'hui :
  - AdlxNative a un état statique et AdlxGpuBackend.Dispose appelle Terminate ;
  - NvApiGpuBackend appelle NVIDIA.Unload dans Dispose (≈437) ;
  - sur un PC hybride, ADLX n'est jamais initialisé si GpuControlService a retenu NVAPI.

## 4. Approche retenue
- **Sessions partagées** (tu en es propriétaire, voir docs/decisions.md) : `NvApiSession`, `AdlxSession`, `IgclSession`, à compteur de références, dans Core/Hardware/Gpu.
  - Initialisation au premier client, Terminate ou Unload au dernier.
  - Le verrou NVAPI de #4 y vit. ADLX s'ouvre même quand GpuControlService a retenu NVAPI.
  - Les backends passent par elles.
  - Premier commit fusionnable seul, sans attendre les cartes : #17 et #20 en dépendent.
- **Outil « Vérification matériel »**, sur bouton explicite, avec consentement, GPU au repos, sous le bail de réglage de #8 : ni groupe ni bascule automatique pendant l'outil. La renonciation Intel (IntelOverclockWaiverAccepted) est respectée ; RiskAccepted n'existe que côté CPU.
  - Pour chaque réglage pris en charge :
    1. lire la valeur, la plage, le pas, l'unité et le défaut ;
    2. écrire une petite valeur dans le sens sûr (un pas vers le bas, puissance −5 %, ventilateur à 50 % puis auto) ; jamais de tension vers le haut ;
    3. relire avec GetOverclock et comparer ;
    4. remettre l'origine dans un finally, puis relire ;
    5. contrôler IsAtFactory (ADLX) ou l'égalité aux défauts (IGCL).
  - Chaque écriture est une entrée du SessionJournal de #4. Si le PC tombe pendant l'outil, ton gestionnaire StartupRecovery remet d'usine avant tout : ResetToFactory, ctlOverclockResetToDefault, deltas NVAPI à 0.
  - IsAtFactory (IADLXGPUTuningServices, emplacement 4 selon IGPUTuning.h) est absent du code : vérifie avant de t'y fier.
  - Rapport exportable en JSON et en texte, que des testeurs renvoient. Il contient :
    - la carte, ses identifiants PCI et sa génération ;
    - les versions du pilote (lecteur de #13 s'il existe, sinon Win32_VideoController.DriverVersion ou IGCL) et de l'API ;
    - le résultat par capacité, et la version de pilote minimale qui marche (inconnue à ce jour) ;
    - aucune donnée personnelle.
  - L'outil marche aussi sur NVIDIA.
- **Drapeau vérifié par génération** : IsVerified sur IGpuTuningBackend, à la place du test sur la marque.
  - Il est tiré d'une table unique (RDNA1 à 4, Alchemist, Battlemage), indexée par identifiant PCI.
  - L'identifiant est lu dans GpuIdentity (#2), que tu complètes : IADLXGPU::DeviceId (emplacement à vérifier dans l'en-tête), IGCL PciDeviceId.
  - NVIDIA reste vérifiée. Une génération ne passe « vérifiée » que sur rapport réel (règle 6).
- **Checklist ADLX**, chaque point inscrit au rapport :
  - vtables ;
  - RDNA1 à 3 en absolu, RDNA4 en décalage (conversion à contrôler) ;
  - plages et pas (IntRange.Step) ;
  - VRAM : fréquence max seulement, pas de timings ;
  - puissance en % ;
  - courbe de ventilateur, zero RPM, retour à la courbe d'origine ;
  - persistance au redémarrage et après plantage (« Default Radeon WattMan settings have been restored ») ;
  - ADLX_RESET_NEEDED sous auto-tuning Adrenalin ;
  - Adrenalin ouvert en même temps ;
  - bon GPU sur APU + carte dédiée ;
  - Tuning2 sur une RX 5000 ?
- **Checklist IGCL** :
  - persistance de ctlOverclockWaiverSet ;
  - V1 ou V2, et unités de ctlOverclockGetProperties ;
  - tension : que le pilote annonce vraiment des volts. Une erreur de décimale peut permettre plus de 2 V : refuse toute valeur hors de la plage annoncée et de bornes absolues codées ;
  - vitesse mémoire V2 (Battlemage) et CTL_RESULT_ERROR_CORE_OVERCLOCK_RESET_REQUIRED ;
  - ctlOverclockResetToDefault ;
  - ventilateurs fixes puis par défaut, sur cartes partenaires ;
  - persistance au redémarrage ;
  - Intel Graphics Software ouvert.
- **Écritures ADLX sous témoin** : une AccessViolation ne se rattrape pas.
  - Étends AdlxProbeGuard aux écritures, avec un témoin par capacité posé juste avant l'appel natif et retiré juste après.
  - Au lancement suivant, la capacité fautive est désactivée et signalée, mais ResetToFactory reste tenté : une carte ne doit jamais rester sur un réglage persistant faute de pouvoir la remettre d'usine.
  - Un plantage système pendant une charge (TDR, écran bleu de #15) n'est pas une faute d'écriture : il relève du SessionJournal.
- **SettingsMatchCurrentGpu** : compare la GpuIdentity entière (nom et identifiant PCI), en partant de ce que #2 a fait.
- **Diagnostic** par un fournisseur de lignes (#1) : la ligne se limite aujourd'hui à « Via … ». Pour chaque capacité, afficher : prise en charge ou N/D avec la raison, vérifiée ou expérimentale, version du pilote.
- **README** : corrige « ne fonctionne qu'avec NVIDIA pour l'instant » (≈324), contredit vers ≈55 et ≈115-146, sauf si #1 l'a déjà fait.
- **Option (question 4)** : auto-tuning et préréglages ADLX.
  - IADLXGPUAutoTuning : IsSupportedAutoTuning = 6, GetAutoTuning = 12 ; UndervoltGPU, OverclockGPU et OverclockVRAM sont asynchrones, via IADLXGPUAutoTuningCompleteListener.
  - IADLXGPUPresetTuning.
  - Opaque et exclusif du manuel : bouton séparé, expérimental, sous bail.

## 5. Pièges connus
- ADLX et IGCL peuvent garder les réglages dans le pilote : une carte peut rester bloquée sur un OC instable (boucle de TDR). Remets toujours d'origine, et vérifie.
- Les Try* renvoient true sans vérifier : seul GetOverclock relu fait foi. Un échec est une ligne du rapport, jamais une exception (règle 2).
- Pendant la vérification, ni Persist() ni ApplyOverclockAtStartup.
- Une seule carte pilotée, la première trouvée. Sans administrateur, les pilotes refusent tout.
- iGPU Intel : IGCL répond « overclock non supporté », donc un N/D propre.
- Le retour « auto » du ventilateur ADLX réécrit une courbe gardée en mémoire : perdue après un plantage, seul ResetToFactory la rend.
- Sessions : un client qui oublie de rendre sa référence garde ADLX ouvert ; un Terminate en double casse les autres clients. Tests obligatoires.

## 6. Questions à me poser
1. F6 vise-t-il les cartes graphiques Radeon et Arc (recommandé), ou les cartes mères, donc l'OC CPU (#16) ?
2. Accès aux cartes : achat, prêt, testeurs (recommandé : testeurs, plus une carte achetée par marque) ?
3. Radeon pré-Navi, ManualGraphicsTuning1 (recommandé : non) ?
4. Auto-tuning et préréglages ADLX (recommandé : bouton séparé, expérimental) ?
5. Texte de la renonciation Intel (recommandé : le sens du texte d'Intel, en français, avec un lien) ?

## 7. Périmètre et hors périmètre
Dans le périmètre : sessions partagées, outil et rapport, drapeau par génération, témoin d'écriture, gestionnaire de reprise, corrections, diagnostic, README.
Hors périmètre : OC automatique (#15 « OC automatique GPU : trois profils sûr / classique / agressif ») ; OC CPU AMD (#16 « Curve Optimizer AMD automatique (OC CPU) ») ; raisons de bridage (#4 « Socle de signaux : bridage, journaux Windows, liens PCIe »). La limite de température AMD reste N/D, faute d'API.

## 8. Livrables et méthode
- Branche feature/verification-oc-gpu, créée depuis develop. Commits en français au présent ; sessions partagées dans un premier commit fusionnable seul.
- Tests xUnit : sessions (compteur, dernier client, PC hybride), table des générations (nom, ID PCI), conversion absolu/décalage, bornes de tension IGCL, gestionnaire de reprise, rapport.
- dotnet build -c Release et dotnet test verts.
- Diagnostic dans Paramètres › Compatibilité de ce PC (fournisseur de lignes) ; README ; « expérimental » par génération non vérifiée.
- /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Tes essais tournent sur un autre PC (i5-13500T). Je vérifierai :
- sur ma machine (i5-14600K, RTX 5070 Ti) : l'outil se déroule, tout revient d'origine, le rapport est lisible, NVIDIA reste vérifiée ; une bascule automatique ne s'applique pas pendant l'outil ;
- avec une Radeon RDNA2, 3 ou 4 et une Arc A ou B, sous le dernier pilote WHQL puis un pilote d'environ 12 mois : l'outil, la persistance après redémarrage, Adrenalin ou Intel Graphics Software ouverts.
````

### 15. OC automatique GPU : trois profils sûr / classique / agressif

- Modèle / effort : **Fable 5.1 / xhigh** · taille XL · relecture /review-max
- Dépend de : #2, #8, #4, #11 ; #14 pour AMD et Intel
- Branche : `feature/oc-auto-gpu`
- Matériel pour vérifier : Machine de Denis (i5-14600K, RTX 5070 Ti) pour NVIDIA ; Radeon et Arc seulement après #14

````text
# OC automatique GPU : trois profils sûr / classique / agressif (F4, partie GPU)

## 1. Objectif
F4 : « Ajouter une fonctionnalité d'OC par IA : fait des scans et des benchs pour tester les limites et proposer trois profils : safe OC (assure aucun plantage), classic OC (OC classique), agressive OC (toujours dans le raisonnable, il ne faut pas que ça plante toutes les heures et sur 1 jeu sur deux, mais ne garantit pas une stabilité) ». Ici, la partie GPU. Deux points, franchement :
- aucune méthode ne peut assurer l'absence de plantage : le profil sûr affichera « aucun plantage constaté, marge large » ;
- l'« IA » est un moteur de recherche déterministe et borné : aucun modèle de langage ne choisit de valeur.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1), notamment : sens de « IA », fonctions du mode technicien, règle 5. Ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis, si elles sont présentes (Grep) : « ### F4 », « ### F6 » et leurs remarques transverses dans docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md.
- Lis ce qu'ont livré :
  - #2 ;
  - #8 : API, bail de réglage, prudence au démarrage, EditedByUser ;
  - #9 : générateur et son emplacement OC ;
  - #4 : SessionJournal, StartupRecovery, qualification d'incident ;
  - #10 : modes secondaires ;
  - #11 ;
  - #14 : sessions partagées, témoin d'écriture, drapeau par génération.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Taille XL : plan en mode plan avant tout code. Premier jalon, livrable seul : l'infrastructure de sécurité, que #16 et #17 reprennent.

## 3. Ce qui existe déjà et à réutiliser
- **GpuControlService** : GetOverclock (plages et valeurs), TrySetClockOffsets, TrySetPowerLimitPercent, TrySetTemperatureLimit, TrySetVoltage, RestoreOverclockDefaults, TryRestorePowerLimitDefault, TryRestoreFanAuto, KeepOverclockOnExit, GetActiveLimit. TryInitialize est idempotent.
- **Backends** : NvApiGpuBackend (deltas P0 par SetPstates20, plages de repli ≈29-32), AdlxGpuBackend (ResetToFactory), IgclGpuBackend (ctlOverclockResetToDefault, renonciation). Leur RestoreOverclockDefaults fait déjà le retour d'origine propre à chaque marque.
- **Sortie** : GpuOverclockProfile (GpuControlModels.cs ≈107), importé par l'API de groupes de #8.
- **Autres conversations** :
  - worker et noyaux vérifiés (#10, #11), modes secondaires (#10) ;
  - SessionJournal, StartupRecovery et lecteur d'événements (#4) ;
  - bail de réglage et prudence de démarrage des groupes (#8), générateur de #9 ;
  - garde-fous GPU, sécurité thermique et veille (#2) ;
  - sessions partagées, témoin d'écriture ADLX et drapeau par génération (#14).
- **Démarrage** : AdlxProbeGuard (modèle de témoin), StartupTask (une tâche par compte, argument fixe --demarrage-windows), le mutex d'instance unique d'App.OnStartup. MainViewModel construit GpuControlViewModel (≈85), dont le constructeur appelle TryInitialize (≈232) puis réapplique les réglages si « Appliquer au démarrage » est coché.

## 4. Approche retenue

**A. Infrastructure de sécurité**, générique GPU, CPU et écran (#16 et #17 la réutiliseront).
- Journal : les entrées du SessionJournal de #4, pas un nouveau format. Avant chaque palier, une entrée (composant, valeurs, heure UTC, EnCours), passée ensuite à Terminé ou Échoué.
- Au lancement, ton gestionnaire passe en premier dans StartupRecovery de #4, avant la construction de GpuControlViewModel. Tu initialises donc toi-même GpuControlService, par les sessions de #14. Si un palier est resté EnCours :
  1. tout remettre d'origine d'abord : RestoreOverclockDefaults (ADLX ResetToFactory, ctlOverclockResetToDefault, deltas NVAPI à 0), limites et ventilateurs d'origine ;
  2. lire la qualification d'incident depuis l'heure notée ;
  3. marquer le palier échoué, avec sa cause ;
  4. proposer de reprendre sans la valeur fautive, jamais d'office. Une reprise après redémarrage (--reprendre-oc, ajouté aux modes secondaires de #10) passe par une tâche distincte, créée seulement si j'accepte et supprimée après, sans toucher à StartupTask.
- Chien de garde : `Watchdog` (Core/Safety), lancé par le mode secondaire --watchdog que tu ajoutes aux modes de #10, hors du mutex et sans fenêtre. Il reçoit un battement de l'app qui pilote la recherche ; 10 s sans battement = retour d'origine.
- Avec lui, l'essai à retour automatique `TrialGuard` : appliquer, attendre une confirmation ou un délai, revenir. Les deux sont génériques, pour que #17 (OC de l'écran) les reprenne tels quels ; si #17 les a déjà créés à ces noms, reprends-les.
- La charge tourne dans le processus enfant de #11 : un TDR ne tue que lui. Garde thermique : la logique pure de #2, à seuils abaissés pendant la recherche.
- Bail de réglage de #8 tenu pendant toute la recherche et la validation : ni groupe ni bascule automatique.
- « Appliquer au démarrage » d'un profil OC : attendre 90 s. Si la session précédente s'est arrêtée brutalement moins de 30 min après l'application, ne pas réappliquer, descendre d'un profil et prévenir. Cela s'ajoute à la prudence des groupes de #8.

**B. Critères d'échec d'un palier**
- résultat de calcul faux, DXGI_ERROR_DEVICE_REMOVED ;
- Display 4101, WHEA-Logger 17/18/19, nvlddmkm ou amdkmdag dans le journal Système ;
- au démarrage suivant : Kernel-Power 41, EventLog 6008, BugCheck 1001 (qualification de #4) ;
- fréquence qui s'effondre, à distinguer d'un bridage Power ou Temperature normal (perf cap) ;
- VRAM : débit en baisse. En GDDR6/6X, l'EDC retransmet au lieu de corrompre : on cherche le pic de bande passante, pas l'absence d'artefacts (GDDR7 de la 5070 Ti : à vérifier).

**C. Recherche**
- Cœur : paliers de 15 MHz chez NVIDIA, sinon le pas du pilote (IntRange.Step chez ADLX, OcControlInfo.Step chez IGCL). Ce pas est absent de GpuOverclockSnapshot : ajoute-le si #14 ne l'a pas fait. 90 s par palier, dichotomie au premier échec.
- VRAM : +50 MHz par pas jusqu'au pic de débit.
- Au moins 60 s entre deux échecs : 5 TDR en une minute donnent un écran bleu 0x117.
- Charge : calcul vérifié, charge chaude, cycles charge/repos de 1 s.
- GetOverclock relu à chaque palier : un palier que le pilote ne retient pas arrête la recherche.
- NVIDIA d'abord ; AMD et Intel seulement pour les générations vérifiées par #14, sinon en expérimental.
- L'OC Scanner NVIDIA n'est pas public. L'auto-tuning ADLX, opaque, reste un bouton à part (#14). IGCL n'a pas d'auto-OC.

**D. Les trois profils** (L = dernier palier réussi ; constantes nommées, expérimentales jusqu'au calibrage)

| Profil | Cœur | VRAM | Puissance, température | Validation |
|---|---|---|---|---|
| Sûr | min(L − 60 MHz ; 50 % du gain) | pic − 2 pas | d'origine, tension d'origine | 60 min |
| Classique | L − 30 MHz | pic − 1 pas | puissance max autorisée, bureau seulement | 30 min, puis période probatoire |
| Agressif | L − 15 MHz | pic | puissance et température max, bureau seulement | 30 min, puis période probatoire |

- Validation du profil sûr : 20 min de calcul vérifié, 20 de charge chaude, 10 de cycles charge/repos, 10 de passages repos → charge. Classique et agressif : 30 min, dont 10 de cycles et de passages repos → charge, car les plantages en jeu viennent surtout des transitions.
- Ta définition de l'agressif (« il ne faut pas que ça plante toutes les heures et sur 1 jeu sur deux ») ne se vérifie pas en 10 min, d'où la période probatoire :
  - pendant les 10 premières heures de jeu sous un profil classique ou agressif, tout TDR, 4101, WHEA ou arrêt brutal qualifié par #4 le rétrograde d'un cran (agressif → classique → sûr) et me prévient ;
  - ensuite seulement, « aucun incident en 10 h » s'affiche.
- Exigences : zéro erreur, zéro TDR, zéro WHEA même corrigée, température sous la limite − 5 °C.
- Libellés : « aucun plantage constaté, marge large » pour le sûr, « stabilité non garantie » pour l'agressif, jamais « garanti ».
- Sortie : trois GpuOverclockProfile liés à la GpuIdentity de la carte (#2, #14), importés par l'API de #8. Si #9 est fusionné, remplis l'emplacement OC de son générateur (question 8), sans toucher un groupe EditedByUser.

**E. Durées et consentement**
- 35 à 60 min de recherche et environ 1 h 30 à 2 h de validation (l'auto-tuning de la NVIDIA App annonce 10 à 20 min). Mode nuit : tout s'enchaîne, résultat au réveil.
- Écran de consentement à chaque recherche : écrans noirs, écrans bleus et redémarrages possibles ; enregistrer son travail ; pas pendant une mise à jour Windows. La renonciation Intel n'est jamais contournée (CpuControlSettings.RiskAccepted concernera #16).

## 5. Pièges connus
- Jamais de test avec « Appliquer au démarrage » : ApplyOverclockAtStartup réappliquerait le palier fautif. Force KeepOverclockOnExit = false pendant tout le test.
- Ne passe pas par GpuControlViewModel : ApplyClockOffsetsNow et ApplyProfile appellent Persist() et écriraient chaque palier dans settings.json.
- Les Try* renvoient true sans vérifier : seul GetOverclock relu fait foi.
- NVAPI : ReadDelta (≈355-370) retombe sur −500/+1000 et −1000/+2000 MHz quand le pilote renvoie min = max. Ce ne sont pas les vraies limites.
- État des deltas NVAPI après un TDR : incertain. Relis-les et remets-les à 0.
- ADLX : si AdlxProbeGuard (ou le témoin de #14) désactive ADLX après un plantage, la remise d'usine au lancement doit rester possible, sinon la carte garde l'OC persistant. IGCL : sans renonciation acceptée à ce lancement, seul ctlOverclockResetToDefault passe.
- GpuControlService n'a pas de verrou : un seul thread pilote.
- Portable (règle 5) : aucun relèvement de puissance ni de température (NVAPI refuse souvent la limite de puissance) ; ventilateurs laissés à l'EC ; arrêt thermique plus bas.
- Afterburner, Adrenalin, NVIDIA App ou Armoury Crate écrasent les réglages : détecte-les et préviens.
- Une validation sûre ne couvre pas toutes les charges, comme la compilation de shaders DX12.
- Règle 2 : la machine plante exprès, aucune méthode Core ne lève pour autant.

## 6. Questions à me poser
1. Périmètre : GPU des trois marques, NVIDIA d'abord (recommandé) ?
2. Un LLM pour expliquer le résultat : tranché par le sens de « IA » de docs/decisions.md ; sinon, recommandé : non en V1, ou la couche de #13 si elle existe.
3. Consentement aux plantages provoqués (recommandé : écran dédié à chaque recherche) ?
4. Durées et mode nuit (recommandé : oui au mode nuit) ?
5. Portables : mode restreint (recommandé) ou interdit ?
6. Limites de puissance relevées en classique et agressif (recommandé : bureau seulement) ?
7. OC en mode technicien : tranché dans docs/decisions.md (recommandé : non) ; pose-la seulement si ce n'est pas le cas.
8. Profils placés dans les groupes générés de #9 (recommandé : sûr en gaming léger, classique en gaming intensif, l'agressif jamais d'office) ?
9. Classique et agressif validés 30 min avec période probatoire de 10 h (recommandé), ou les 20-30 min et 10 min de l'étude ?

## 7. Périmètre et hors périmètre
Dans le périmètre : infrastructure de sécurité (journal de paliers, reprise, Watchdog, TrialGuard), recherche, validations, période probatoire, trois profils, branchement dans le générateur de #9, écrans de consentement et de résultat, mode nuit, diagnostic.
Hors périmètre :
- CPU : #16 « Curve Optimizer AMD automatique (OC CPU) » reprendra l'infrastructure ; pas d'OC CPU Intel en V1 ;
- auto-tuning ADLX (#14 « Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc ») ;
- groupes et bascule (#8 « Groupes de profils CPU + GPU + ventilation », #9 « Profils automatiques selon l'usage »), sauf le remplissage de l'emplacement OC ;
- bench GPU (#11 « Bench GPU et test combiné “alimentation” ») ;
- OC de l'écran (#17 « OC de l'écran (fréquence de rafraîchissement) »).

## 8. Livrables et méthode
- Branche feature/oc-auto-gpu, créée depuis develop. Commits en français au présent.
- Tests xUnit :
  - machine à états de la recherche (dichotomie, 60 s entre échecs, pic VRAM) et calcul des profils ;
  - journal et reprise (gestionnaire StartupRecovery sur entrées synthétiques) ;
  - Watchdog (perte du battement) et TrialGuard ;
  - période probatoire (rétrogradation) ;
  - qualification d'un incident à partir d'événements synthétiques.
- dotnet build -c Release et dotnet test verts.
- Ligne « OC automatique GPU » par un fournisseur de lignes (#1) dans Paramètres › Compatibilité de ce PC : prise en charge, dernier résultat, dernier incident, palier instable, période probatoire en cours (règle 4). Section du README. « Expérimental » (règle 6).
- /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
Tes essais tournent sur un autre PC (i5-13500T), sans OC réel. Sur ma machine (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI), je vérifierai :
- une recherche complète en mode nuit : trois profils ordonnés et importés, groupes générés de #9 complétés ;
- un échec provoqué (worker tué, palier forcé trop haut) : au relancement, tout revient d'origine avant le reste et la cause est lue ;
- le retour d'origine environ 10 s après avoir tué l'app en plein palier (chien de garde) ;
- le profil sûr pendant 60 min de jeu et une compilation de shaders ;
- un TDR provoqué pendant la période probatoire d'un profil agressif : rétrogradé en classique, prévenu ;
- Radeon et Arc, seulement après #14.
````

### 16. Curve Optimizer AMD automatique (OC CPU)

- Modèle / effort : **Fable 5.1 / max** · taille L · relecture /review-max
- Dépend de : #15, #10, #4, #5
- Branche : `feature/oc-auto-cpu-amd`
- Matériel pour vérifier : Ryzen de bureau chez des testeurs : Zen 3 (Ryzen 5000, dont un 6 cœurs type 5600X pour le mappage des fusibles), Zen 4 (Ryzen 7000), Zen 5 (Ryzen 9000), idéalement un avec VBS actif et un avec PBO désactivé dans le BIOS. Denis est sur Intel (i5-14600K) : sa machine ne sert qu'à vérifier le message d'exclusion Intel et le diagnostic. Trouver des testeurs est indispensable.

````text
# Curve Optimizer AMD automatique (OC CPU) (F4, partie CPU AMD)

## 1. Objectif
Ma demande (F4) : « ajouter une fonctionnalité d'OC par IA : fait des scans et des benchs pour tester les limites et proposer trois profils : safe OC (assure aucun plantage), classic OC (OC classique), agressive OC (toujours dans le raisonnable, il ne faut pas que ça plante toutes les heures et sur 1 jeu sur deux, mais ne garantit pas une stabilité) ». Ici, la partie CPU : Curve Optimizer (CO) négatif par cœur sur les Ryzen de bureau. L'« IA » = recherche déterministe et bornée, pas un LLM. L'OC CPU Intel est exclu de la V1, et l'interface explique pourquoi.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1), notamment sens de « IA » et fonctions du mode technicien. Ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F4 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md si présentes (Grep).
- Vérifie dans develop ce qu'ont livré :
  - #15 : gestionnaire de reprise, Watchdog, période probatoire, vue OC auto ;
  - #10 : noyaux CPU, modes secondaires ;
  - #4 : SessionJournal, StartupRecovery, WHEA-Logger, Kernel-Power 41, 6008 ;
  - #5 : CpuTopology ;
  - #8 : bail de réglage, CpuProfile dans les groupes ;
  - #9 : emplacement OC de son générateur.
  Ne duplique rien ; s'il manque #15, #10 ou #4, arrête-toi et dis-le-moi.
- Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Ce qui existe déjà et à réutiliser
- src/PCPerfSuite.Core/Hardware/Cpu/AmdSmuBackend.cs :
  - AmdCodeName (DragonRange volontairement en lecture seule ; ni Fire Range ni Strix) ;
  - DetectCodeName/ReadCpuId (≈365-398, privés : à extraire) ;
  - envoi RSMU par PawnIoModule.TryExecute(`ioctl_send_smu_command`, 7 qwords en entrée, 6 en sortie ; SendDesktopLimit ≈216) ;
  - PciGuard (mutex Global\Access_PCI, classe privée ≈404 : à partager).
- PawnIoDriver.TryLoadModule (≈275) : RyzenSMU et AMDFamily17 (ioctl_read_smn) sont embarqués dans LibreHardwareMonitorLib 0.9.6 (vérifié).
- CpuTopology (#5) : cœur, fils SMT, CCD, d'après GetSystemCpuSetInformation. Étends-la plutôt que de refaire une carte.
- CpuFirmwareDefaultsStore.Resolve (origine mémorisée par session Windows), CpuControlViewModel.OnPowerModeChanged (≈352, réveil, relecture ajoutée par #2), CpuProfile (null = ne pas toucher), CpuCapability et UnsupportedCpuBackend (raison), CpuControlSettings.RiskAccepted, ILaptopFanProvider.IsVerified.

## 4. Approche retenue
- Nouveau module (par ex. Core/Tuning/Cpu/), onglet CPU dans la vue OC auto de #15.
- Numéros relevés dans ZenStates-Core (GPL-3.0 : les numéros, jamais le code) et RyzenAdj, à vérifier avant de t'y fier :
  - Zen 3 (Vermeer, 5800X3D compris) : RSMU Set 0x0A, SetAll 0x0B, Get 0x7C ;
  - Zen 4 (Raphael) et Zen 5 (Granite Ridge) : RSMU 0x06/0x07, Get 0xD5 (MP1 0x35/0x36 aussi) ;
  - Zen 2 : pas de CO ;
  - APU (Renoir à Cezanne MP1 0x55/0x54, Rembrandt à Strix 0x4C/0x4B) et Dragon/Fire Range (PSMU 0x07/0x06) : non vérifiés, parfois ignorés par le firmware OEM, sans relecture relevée (à vérifier). N/D en V1.
- Argument : masque (ccd<<28)|((core%8)<<20) plus la marge sur 16 bits signée (hors étude, à vérifier). Les cœurs désactivés se lisent dans les fusibles (0x5D218+…, 0x30081800+… selon la génération) par AMDFamily17 ioctl_read_smn ; sinon la CO part sur le mauvais cœur (5600X, 7600, 9600X).
- Carte processeur logique ↔ cœur ↔ index SMU ↔ APIC ID : extension de CpuTopology (#5), construite une fois, testée.
- Get après chaque Set, écart = échec. Négatif seulement, borné dans le backend : −30 sur Zen 3, jusqu'à −50 sur Zen 4/5 selon l'AGESA (hors étude, à vérifier).
- Origine = valeurs lues avant toute écriture, car le BIOS peut avoir posé sa CO. Le réglage est volatil et perdu au redémarrage : c'est le filet. Au réveil, réappliquer un profil validé, jamais une valeur en test. « Appliquer au démarrage » : garde-fous de #15 (90 s d'attente, un profil plus bas après un arrêt inattendu).
- Test par cœur, sous le bail de réglage de #8 (ni groupe ni bascule automatique), un seul cœur modifié à la fois :
  - thread épinglé (SetThreadSelectedCpuSets) ;
  - noyau AVX2/FMA à résultat vérifié, alterné avec des phases légères, car la CO casse au boost max, à faible charge ;
  - chaque palier est une entrée du SessionJournal de #4, reprise par l'infrastructure de #15.
  Échec = résultat faux, WHEA-Logger 19 ou 18 (attribution au cœur par l'APIC ID : à vérifier), processus de test tombé, redémarrage inattendu, ou seuil thermique abaissé franchi (logique pure de #2).
- Paliers de −5 puis affinage ; L = dernier palier réussi du cœur. Profils : sûr L+5 (PPT d'origine), classique L+3, agressif L+1. Validation :
  - sûr : 10 min par cœur + 30 min tous cœurs, zéro WHEA même corrigée, T < limite − 5 °C ;
  - classique : 20-30 min ;
  - agressif : 5 min par cœur + 30 min tous cœurs avec phases légères, puis la période probatoire de #15 : tout WHEA, arrêt brutal ou processus de test tombé dans les 10 premières heures d'usage le rétrograde d'un cran ; « stabilité non garantie ».
  Jamais « garanti » : « aucun plantage constaté ».
- Durées : 2-3 h pour 8 cœurs, 4-6 h pour 16 : mode nuit, reprise après plantage par l'infrastructure de #15.
- Consentement explicite (plantages voulus), pas de test pendant une mise à jour Windows, alerte si Ryzen Master ou PBO2 Tuner tournent. Portable, si un jour pris en charge : CO seule, jamais de PPT relevé (esprit de la règle 5).
- Profil : CO nullable dans CpuProfile, clé par cœur en chaîne, identité du CPU (celle des groupes de #8) ; refus sur un autre processeur. Si #9 est fusionné, remplis l'emplacement OC CPU de son générateur comme #15 l'a fait pour le GPU, sans toucher un groupe EditedByUser.
- Texte d'exclusion Intel :
  - ratios (MSR 0x1AD) en lecture seule ;
  - IntelMSR embarqué sans écriture (PawnIO.Modules ≥ 0.2.4 : vois ce qu'a tranché #2) ;
  - undervolt 0x150 non documenté, verrouillé dès la 12e gen et sous VBS ;
  - Vmin shift des 13e/14e gen, qui proscrit l'OC positif.

## 5. Pièges connus
- Inconnues : CO ignorée quand PBO est désactivé dans le BIOS ; SMU sous VBS (actif chez moi). Détecte par relecture et explique (règles 1 et 3).
- Le SMU répond OK même quand il ignore : seule la relecture fait foi.
- PciGuard continue sans le mutex au bout de 500 ms : pour la CO, refuse d'écrire s'il n'est pas tenu. Une écriture SMU à la fois.
- « Appliquer au démarrage » forcé à faux pendant la recherche.
- Un processus de test PCPerfSuite.exe passe par les modes secondaires de #10, hors du mutex d'instance unique.
- « Sûr » ne couvre pas tout (AVX-512 sur Zen 4/5, compilation de shaders) : le dire.
- Règle 2 : la machine plante exprès, aucune méthode Core ne lève.

## 6. Questions à me poser
- As-tu des Ryzen de test (Zen 3, 4, 5), et à qui envoyer le protocole ?
- Durées de 2 à 6 h acceptables, mode nuit avec reprise automatique ?
- Relever le PPT dans le profil agressif (tant que T < 90 °C), ou CO seule ?
- APU et portables : N/D en V1 (recommandé), ou CO sans relecture marquée expérimentale ?
- Exclusion de l'OC CPU Intel confirmée pour la V1 ?
- Validation de l'agressif : 5 min par cœur + 30 min et période probatoire (recommandé), ou 10 min comme l'étude ?
- Mode technicien : tranché dans docs/decisions.md ; pose-la seulement si ce n'est pas le cas.

## 7. Périmètre et hors périmètre
Dans : CO négative Zen 3/4/5 de bureau, recherche, 3 profils, validation, période probatoire, reprise, branchement dans le générateur de #9, message Intel, diagnostic.
Hors :
- #15 « OC automatique GPU », #14 « Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc » ;
- #10 « Moteur de charge et bench CPU / RAM / disque », #4 « Socle de signaux » ;
- #2 « Fiabiliser le tuning » (modules PawnIO Intel) ;
- #8 « Groupes de profils » (tu n'y ajoutes que le champ CO) ;
- #9 « Profils automatiques selon l'usage », sauf l'emplacement OC ;
- PBO scalar et boost override (V2).

## 8. Livrables et méthode
Branche feature/oc-auto-cpu-amd depuis develop ; commits en français au présent. Tests xUnit sur la logique pure : masque, fusibles, extension de CpuTopology, recherche et marges, bornes, période probatoire, reprise. dotnet build -c Release et dotnet test verts. Lignes par des fournisseurs de lignes (#1) dans « Paramètres › Compatibilité de ce PC » : génération, CO relue par cœur, cœurs désactivés, ioctl des modules, VBS (Win32_DeviceGuard), microcode (« Update Revision »), outils concurrents actifs, dernier incident. Section README avec un protocole pour les testeurs. « Expérimental » par génération (règle 6). Relecture /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
- Mon i5-14600K : exclusion Intel expliquée, aucune commande SMU ou MSR tentée, VBS et microcode justes.
- Un testeur Ryzen :
  - CO lue identique à Ryzen Master ; −5 sur un cœur, relu ; origine au redémarrage ; réveil de veille ;
  - un 6 cœurs ; PBO désactivé ; VBS actif puis inactif ;
  - une nuit complète ;
  - profil agressif : un WHEA corrigé pendant la période probatoire le rétrograde.
````

### 17. OC de l'écran (fréquence de rafraîchissement)

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #3, #1 ; #14 et #15 (phase 1) conseillés
- Branche : `feature/oc-ecran`
- Matériel pour vérifier : L'écran de Denis branché sur sa RTX 5070 Ti (voie NVIDIA, idéalement sans module G-Sync) ; si possible un écran branché sur la sortie de la carte mère (UHD 770) pour vérifier le refus Intel ; un écran sur une carte Radeon chez un testeur (voie ADLX).

````text
# OC de l'écran (fréquence de rafraîchissement) (F16)

## 1. Objectif
Ma demande (F16) : « ajouter un OC de l'écran ». Concrètement : faire tourner un écran au-dessus de sa fréquence native par une résolution personnalisée du pilote graphique. Toujours à l'essai, avec retour automatique, pour qu'un écran noir ne dure jamais plus de quelques secondes.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F16 » (et « ### F17 » pour le service des écrans) de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md si présentes (Grep).
- Vérifie ce qu'ont livré :
  - #3 : service des écrans (chemins QueryDisplayConfig, « \\.\DISPLAYn », LUID de l'adaptateur, type de sortie, identifiant stable) ;
  - #1 : navigation, registre des modifications de Windows ;
  - #14 : sessions NVAPI et ADLX partagées ;
  - #15, phase 1 : Watchdog, TrialGuard, sur le SessionJournal et StartupRecovery de #4.
  S'il manque #3, arrête-toi et dis-le-moi. S'il manque les sessions de #14 ou le chien de garde de #15, crée-les aux noms et emplacements de docs/decisions.md, pour qu'ils les reprennent.
- Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Ce qui existe déjà et à réutiliser
- Le service des écrans de #3 (DisplayTopology, Core/Hardware/Displays) : ne refais aucune énumération.
- GpuControlService.TryInitialize (≈53) : modèle de choix de backend par marque. UnsupportedCpuBackend : modèle Null Object qui porte sa raison.
- NvAPIWrapper.Net 0.8.1.101, déjà référencé. Vérifié par réflexion :
  - DisplayApi.GetDisplayIdByDisplayName, DisplayApi.GetTiming(displayId, TimingInput), DisplayDevice(string) ;
  - TrialCustomResolution(res, hardwareModeSetOnly), SaveCustomResolution(isThisOutputIdOnly, isThisMonitorOnly), RevertCustomResolution, DeleteCustomResolution, GetCustomResolutions, CalculateTiming ;
  - MonitorCapabilitiesType n'expose que VSDB (0x1000) et VCDB (0x1001).
- Sessions NVAPI et ADLX partagées (#14) ; AdlxNative (QueryInterface, GetObject, appels par emplacement) et AdlxProbeGuard (témoin disque).
- Watchdog et TrialGuard (Core/Safety, #15) ; SessionJournal et StartupRecovery (#4) ; mode secondaire --watchdog (#10, #15) ; registre des modifications de Windows (#1).
- MachineInfo.IsLaptop, ExternalLink.TryOpen, AppSettingsStore.Update, abonnement/désabonnement SystemEvents comme CpuControlViewModel (≈349/759).

## 4. Approche retenue
- Nouveau dossier Core, à côté du service de #3 : un backend par voie (NVAPI, ADLX, indisponible avec raison), choisi par écran d'après la marque de l'adaptateur qui le pilote (règle 1), pas d'après le GPU retenu par GpuControlService. Page « Écrans » à l'emplacement que prévoit la navigation de #1.
- NVIDIA :
  - displayId depuis « \\.\DISPLAYn », résolu à chaque fois, jamais enregistré ;
  - timing CVT-RB : DisplayApi.GetTiming, ou CalculateTiming si tu vérifies qu'il produit du CVT-RB ;
  - TrialCustomResolution (rien d'enregistré) ; SaveCustomResolution(…, isThisMonitorOnly: true) seulement après confirmation ; RevertCustomResolution sinon ; « Restaurer » = DeleteCustomResolution ;
  - refus des écrans à module G-Sync. isTrueGsync (NV_MONITOR_CAPS_TYPE_GENERIC = 0x1002 dans nvapi.h) n'est pas lu par le wrapper → P/Invoke de NvAPI_DISP_GetMonitorCapabilities (identifiant 0x3B05C7E1 par nvapi_QueryInterface).
- AMD :
  - IADLXSystem::GetDisplaysServices → GetCustomResolution(display) → IsSupported → GetCurrentAppliedResolution ;
  - SetValue(ADLX_CustomResolution : refreshRate en Hz entiers, timingStandard CVT_RB) → CreateNewResolution ;
  - application par ChangeDisplaySettingsExW : CDS_TEST, puis dwflags = 0 (dynamique, non enregistré). Confirmation → CDS_UPDATEREGISTRY ; sinon ChangeDisplaySettingsExW(null, null, IntPtr.Zero, 0, IntPtr.Zero) puis DeleteResolution ;
  - refus en mode dupliqué ou Eyefinity. Emplacements à relever dans les en-têtes du SDK.
- Intel (IGCL n'a que des modes source, sans fréquence), et dalles pilotées par un iGPU (Optimus : NVAPI_INVALID_DISPLAY_ID) : N/D en V1, chaque cas avec sa raison (règle 3). Surcharge EDID : phase 2, seulement si je l'accepte.
- Garde « essai avec retour automatique » : le TrialGuard de #15, pas une seconde implémentation.
  - Entrée EnCours du SessionJournal de #4 posée avant l'essai.
  - Confirmation sur TOUS les écrans : compte à rebours 15 s, retour sans clic. « Revenir » par défaut : Entrée et Échap reviennent, garder exige une touche dédiée, pour ne jamais valider un écran noir à l'aveugle.
  - Chien de garde --watchdog (le même processus de surveillance que #15), qui annule si l'interface cesse de battre.
  - Au lancement, ton gestionnaire StartupRecovery : entrée EnCours = retour d'origine, jamais de réapplication.
- Fréquence réellement appliquée relue par QueryDisplayConfig (targetInfo.refreshRate) et DwmGetCompositionTimingInfo ; écart = « le pilote a retenu X Hz ».
- Sauts d'images : aucune détection logicielle possible, dis-le. Test guidé en ouvrant https://www.testufo.com/frameskipping (ExternalLink), photo en pose ≥ 1/10 s.
- Paliers de +5 Hz choisis par l'utilisateur, aucune boucle automatique.
- Mode validé mémorisé par écran (identifiant stable de #3, en chaînes), dans une nouvelle section DisplaySettings. Inventaire des modes créés par PCPerfSuite, « Restaurer » par écran, et inscription au registre des modifications de Windows de #1 : un mode enregistré par le pilote survit à la désinstallation.

## 5. Pièges connus
- Sessions : NvApiGpuBackend appelle NVIDIA.Unload dans Dispose (≈437), AdlxGpuBackend.Dispose appelle Terminate : passe par les sessions partagées de #14. Sur un PC hybride, ADLX n'est jamais initialisé si GpuControlService a retenu NVAPI ; la session de #14 le permet.
- AdlxProbeGuard ne couvre que l'initialisation : un mauvais emplacement ADLX = violation d'accès non rattrapable. Mets les nouveaux services sous témoin.
- Le processus de surveillance est le mode secondaire --watchdog, lu avant le mutex d'instance unique (Local\PCPerfSuite.SingleInstance).
- Vérifie qu'un essai NVAPI s'annule depuis un autre processus ; sinon repli ChangeDisplaySettingsExW vers le mode du registre.
- Ne supprime que les modes créés par PCPerfSuite, jamais ceux de CRU ou du panneau NVIDIA.
- À expliquer ou signaler (règle 3) :
  - DSC : modes personnalisés refusés d'après des utilisateurs et CRU ; non documenté, indétectable par le nvapi.h public ;
  - bande passante du câble ou du port (HDMI 2.0, DP 1.2) ;
  - plage VRR qui ne suit pas le nouveau mode, HDR.
  ADLX sur APU et dalles de portable : non vérifié.
- Numéro de série EDID hors du rapport copié (IsPersonal).

## 6. Questions à me poser
- Surcharge EDID en phase 2 (seule voie pour Intel et la plupart des dalles de portable), ou jamais ?
- Écrans externes seulement, ou aussi les dalles de portable ?
- Paliers choisis par l'utilisateur, ou assistant qui propose les paliers (toujours sans boucle) ?
- Texte d'avertissement avant le premier essai (aucune casse ni règle de garantie trouvée : rester prudent) : qui le valide ?
- Écrans à module G-Sync et VRR actif : refuser ou avertir ?

## 7. Périmètre et hors périmètre
Dans : NVIDIA et AMD, branchement sur TrialGuard et le chien de garde, test guidé, mémorisation, diagnostic.
Hors :
- #3 « Choix de l'écran de l'overlay et service des écrans » ;
- #14 « Vérifier et finaliser l'OC GPU AMD Radeon et Intel Arc » (sessions) ;
- #15 « OC automatique GPU » (infrastructure de sécurité) ;
- #22 « GPU dédié des portables » ;
- Intel et surcharge EDID (phase 2).

## 8. Livrables et méthode
Branche feature/oc-ecran depuis develop ; commits en français au présent. Tests xUnit sur la logique pure :
- tes cas de la garde (TrialGuard) : confirmation, délai, touche par défaut, perte du battement, entrée EnCours au lancement ;
- choix de la voie et raisons de refus ;
- paliers, comparaison de fréquence.
dotnet build -c Release et dotnet test verts. Une ligne par écran, par un fournisseur de lignes (#1), dans « Paramètres › Compatibilité de ce PC » : voie, raison, fréquence native et appliquée, mode PCPerfSuite actif. Section README. « Expérimental » (règle 6) pour AMD, et pour NVIDIA tant que je ne l'ai pas vérifié. Relecture /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
- Mon écran sur la RTX 5070 Ti :
  - essai +5 Hz ; retour sans clic après 15 s ; Entrée qui revient, touche dédiée qui garde ;
  - interface suspendue (Moniteur de ressources) → annulation par la surveillance ;
  - fréquence relue ; test TestUFO à l'appareil photo ;
  - « Restaurer » ; redémarrage pendant un essai non confirmé.
- Si un écran est branché sur la carte mère (UHD 770) : N/D Intel expliqué.
- Un écran sur Radeon (testeur).
````


## Lot F · Gestionnaires Windows

Indépendants après le lot A. #18 et #19 font des opérations risquées (périphériques désactivés, partitions) : /review-max obligatoire.

### 18. Gestionnaire de périphériques et de pilotes

- Modèle / effort : **Opus 5.5 / xhigh** · taille XL (deux étapes) · relecture /review-max
- Dépend de : #1 ; #3, #7, #4 conseillés
- Branche : `feature/peripheriques-pilotes`
- Matériel pour vérifier : Machine de Denis (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI) avec un périphérique USB sans risque (webcam) et une ancienne clé USB pour les fantômes ; un portable de marque (Dell, Lenovo, HP ou ASUS) pour la détection des outils constructeur et les refus batterie / contrôleur embarqué.

````text
# Gestionnaire de périphériques et de pilotes (F12 puis F7)

## 1. Objectif
Mes demandes : « (F12) gestionnaire de périphériques » et « (F7) Gestionnaire d'installation de pilotes ». Deux étapes dans cette conversation : d'abord l'inventaire des périphériques avec des actions sûres (F12), puis un onglet Pilotes construit sur cet inventaire (F7). Le service de périphériques servira aussi à #22 (état d'alimentation du GPU dédié des portables).

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F12 » et « ### F7 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md si présentes (Grep).
- Dépendances : #1 ; #3, #7, #4 conseillés. Vérifie ce qu'ils ont livré : #1 (sections de navigation et cycle de vie des pages : PageKeys, IPageLifecycle ; AppDataPaths ; ICompatibilityRowProvider ; registre des modifications ISystemChangeOwner), #3 (DisplayTopology, pour savoir quelle carte pilote un écran actif), #7 (boîte à outils : DDU, liens des outils constructeur, OfficialInstaller paramétré par source, ProgramDataFolder).
- #4 a posé le lecteur CfgMgr32 dans Core/Devices : étends-le en service des périphériques (s'il lit déjà d'autres propriétés, par ex. le lien PCIe, elles restent dans ce service unique). Si #4 n'est pas encore passé, crée ce lecteur à cet emplacement et mets à jour sa ligne dans docs/decisions.md.
- Réutilise le lecteur de versions (pilotes/BIOS) de #13 s'il existe : ne duplique pas la lecture des versions.
- Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Soumets-moi un plan en mode plan, pour les deux étapes, avant d'écrire du code.

## 3. Ce qui existe déjà et à réutiliser
- BatteryReader (≈153-238, P/Invoke ≈326-357) : SetupDiGetClassDevs, SetupDiEnumDeviceInterfaces, SetupDiGetDeviceInterfaceDetail, SetupDiDestroyDeviceInfoList, en best-effort. Dans le code actuel, c'est le seul code SetupAPI, et il n'y a ni Win32_PnPEntity ni Win32_PnPSignedDriver ; CfgMgr32 n'arrive que par le lecteur de #4 (Core/Devices).
- ProcessTerminationGuard.GetRefusalReason (≈26) et DeletionGuard.GetRefusalReason (≈46) : modèle de garde statique, phrase prête à afficher, refus en cas d'exception.
- StorageViewModel.LoadDrivesAsync (≈120) : chargement hors constructeur (MainViewModel construit tout avant la fenêtre) ; IPageLifecycle (#1) pour ne charger qu'à l'ouverture de la page.
- MachineInfo.Current (IsLaptop, Manufacturer, VideoControllers), ElevationHelper.IsAdministrator, SessionUser.IsOtherProfile (dossier de sauvegarde), ExternalLink.TryOpen, RtssInstallation.Detect (clés Uninstall : modèle pour repérer les outils constructeur).
- DisplayTopology (#3, Core/Hardware/Displays) : quelle carte pilote un écran actif.
- GpuControlService.RestoreOverclockDefaults, TryRestoreFanAuto et Dispose (≈139-194).
- Textes qui excluent les pilotes, à mettre à jour : InstallationsViewModel.cs (≈345) et AppSettingsView.xaml (≈153).

## 4. Approche retenue
### Étape 1 — F12
- Un seul service Core/Devices/, étendu depuis le lecteur CfgMgr32 de #4 : CfgMgr32 pour la lecture, SetupAPI pour les actions. Jamais d'analyse du texte de pnputil, traduit dans la langue de Windows.
- Énumération : CM_Get_Device_ID_List_SizeW / CM_Get_Device_ID_ListW sur tous les nœuds ; absents (« fantômes ») par CM_Locate_DevNodeW(CM_LOCATE_DEVNODE_PHANTOM) ; état par CM_Get_DevNode_Status (bits DN_*, codes CM_PROB_*) ; propriétés par CM_Get_DevNode_PropertyW (DEVPKEY FriendlyName, DeviceDesc, Class/ClassGuid, Manufacturer, DriverVersion, DriverDate, DriverProvider, DriverInfPath, LocationInfo, IsPresent, LastArrivalDate/LastRemovalDate, parent et enfants) ; noms de classes traduits par SetupDiGetClassDescriptionW. Expose aussi DEVPKEY_Device_PowerData (CM_POWER_DATA.PD_MostRecentPowerState) pour #22.
- Page « Périphériques » (clé PageKeys, placée selon la navigation de #1, chargée à l'ouverture par IPageLifecycle) : vues par classe et par connexion, recherche, filtres « en erreur » et « cachés ».
- Codes problème en français avec leur numéro : 10, 22, 28, 31, 43, 45, 52 (réutilisés par #12, #13 et l'étape 2).
- Actions : activer/désactiver par SetupDiSetClassInstallParams(DIF_PROPERTYCHANGE, DICS_ENABLE/DICS_DISABLE) + SetupDiCallClassInstaller, avec lecture de DI_NEEDREBOOT ; repli CM_Enable_DevNode / CM_Disable_DevNode. « Jusqu'au redémarrage » par CM_Disable_DevNode sans CM_DISABLE_PERSIST, « définitif » par DICS_FLAG_GLOBAL ou CM_DISABLE_PERSIST (Windows 10+) : vérifie le comportement exact de chaque voie avant de t'y fier. Rechercher les modifications par CM_Reenumerate_DevNode sur la racine ; désinstaller par DiUninstallDevice (newdev.dll) ; nettoyage des fantômes en liste cochée, absents et non critiques seulement, après confirmation.
- Registre des modifications (ISystemChangeOwner, #1) : chaque désactivation persistante (« définitive ») et chaque fantôme supprimé s'y inscrit, avec Describe() toujours (périphérique, classe, identifiants matériels, date) et RestoreAll() quand c'est possible (réactiver un périphérique désactivé de façon définitive). Un fantôme supprimé ne se restaure pas : Describe() seulement, et l'interface le dit.
- Garde DeviceActionGuard, tests obligatoires : refus sans DN_DISABLEABLE ; classes System, Computer, Processor, Volume ; contrôleur qui porte le disque de démarrage (remonter la chaîne des parents) ; seule carte graphique, ou carte qui pilote un écran actif (DisplayTopology, #3 ; si #3 n'est pas encore passé, refuse toute carte de la classe Display) ; dernier clavier ou dernière souris ; carte réseau en session distante (prévenir) ; sur portable, jamais le contrôleur embarqué ACPI (PNP0C09), la batterie ni les zones thermiques (esprit de la règle 5). Identifier par classe et identifiants matériels, jamais par le nom (règle 1).
- « Rétablir le pilote précédent » : aucune API publique documentée à la connaissance de l'étude (non confirmé), bouton qui ouvre le Gestionnaire de périphériques.

### Étape 2 — F7
- Onglet Pilotes : version, date, fournisseur, INF (propriétés de l'étape 1, ou lecteur de versions de #13 s'il existe), signataire par Win32_PnPSignedDriver (lent : Task.Run et cache), pilotes anciens, périphériques sans pilote (code 28).
- Windows Update, seule source d'installation automatique : Type.GetTypeFromProgID("Microsoft.Update.Session") + dynamic (une COMReference ne compile pas avec dotnet build, MSB4803) ; CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver'") en tâche de fond (plusieurs minutes) ; titre, classe, version, date (IWindowsDriverUpdate) ; sélection pilote par pilote ; UpdateDownloader puis UpdateInstaller ; RebootRequired signalé. Classe Firmware (BIOS, contrôleur embarqué) toujours exclue (règle 5).
- « Aucune mise à jour » expliqué selon la cause (règle 3) : PC géré (WSUS, Intune, ExcludeWUDriversInQualityUpdate), service arrêté, pas d'Internet, fabricant absent de Windows Update.
- Avant installation : point de restauration par WMI root\default SystemRestore.CreateRestorePoint(description, 10 = DEVICE_DRIVER_INSTALL, 100 = BEGIN_SYSTEM_CHANGE). Un point de moins de 24 h empêche la création (SystemRestorePointCreationFrequency) : vérifie qu'il existe et explique. Protection désactivée : prévenir sans l'activer.
- Avant un pilote graphique : rendre la carte au pilote (overclock, ventilateurs), libérer NVAPI/ADLX/IGCL (si #14 a posé NvApiSession, AdlxSession et IgclSession à compteur de références, jusqu'à ce que chaque compteur retombe à zéro), puis demander de relancer PCPerfSuite (GpuControlService ne se réinitialise pas après Dispose).
- Sauvegarde : pnputil /export-driver * <dossier>, code de sortie seulement ; dossier proposé par défaut obtenu d'AppDataPaths (#1), jamais un chemin %LOCALAPPDATA%\PCPerfSuite codé en dur (SessionUser.IsOtherProfile : le signaler) ; tout autre fichier écrit par l'app (cache éventuel des signataires) passe aussi par AppDataPaths. Installation d'un .inf par DiInstallDriverW (newdev.dll). Suppression d'un paquet par DiUninstallDriverW, après sauvegarde, refusée s'il sert encore à un périphérique présent.
- Registre des modifications (ISystemChangeOwner, #1) : chaque paquet retiré s'y inscrit, avec Describe() toujours (INF, version, fournisseur, emplacement de la sauvegarde) et RestoreAll() quand c'est possible (réinstaller depuis la sauvegarde par DiInstallDriverW, à vérifier) ; chaque pilote installé s'y inscrit aussi, Describe() seulement (retour par le point de restauration ou le Gestionnaire de périphériques).
- GPU et portables : version installée, puis orienter vers l'outil du fabricant (NVIDIA App, AMD Software, Intel Graphics Software ou DSA ; Dell Command Update, Lenovo Vantage ou System Update, HP Support Assistant ou HPIA, MyASUS), détecté et lancé s'il est installé, sinon lien de la boîte à outils de #7. DDU et liens des outils constructeur : toujours via la boîte à outils de #7, jamais une seconde liste dans cette page. Sur portable, pilote GPU souvent propre au constructeur (Optimus, MUX), comme l'audio, le pavé tactile et les touches de fonction : avertir. Dernière version NVIDIA par l'adresse non documentée AjaxDriverService.php?func=DriverManualLookup : expérimental, seulement si je l'accepte.
- Impossible ou écarté, à dire franchement : téléchargement direct des pilotes AMD, Intel ou Realtek (aucune API publique), Microsoft Update Catalog (pas d'API officielle), base de pilotes façon Driver Booster, mise en cache ou réhébergement de pilotes (licences), DDU réimplémenté (il est dans la boîte à outils de #7), redémarrage automatique en mode sans échec.

## 5. Pièges connus
- Contrôleur de démarrage désactivé = INACCESSIBLE_BOOT_DEVICE ; seule carte graphique désactivée de façon persistante = écran noir même après redémarrage.
- Nettoyer des fantômes efface les lettres d'anciens disques USB et les IP fixes d'anciennes cartes réseau : le dire.
- L'app élevée installe sans invite UAC : confirmation explicite avant chaque installation. Elle ne lance jamais un exe utilisateur directement (explorer.exe) ; pnputil par son chemin complet dans System32.
- IUpdate.EulaAccepted : n'accepte jamais en silence.
- Pilotes à mettre à jour : IsRequired=false, sinon le bouton Paramètres clignote en permanence.
- Si tu retiens un téléchargement direct : passe par OfficialInstaller de #7, paramétré par source, qui borne à 100 Mo, 5 min, et refuse ce qui n'est pas « MZ », avec téléchargement dans ProgramDataFolder (#7) ; sans relâcher PawnIO. Si #7 n'est pas encore passé, paramètre-le par source toi-même, sans relâcher ces bornes.
- WMI et WUA hors du thread d'interface, avec délai ; rien dans un constructeur.

## 6. Questions à me poser
- Inclure le nettoyage des périphériques fantômes ?
- Désactivation « jusqu'au redémarrage » par défaut avec l'option « définitive », ou l'inverse ?
- « Rétablir le pilote précédent » et « mettre à jour depuis un dossier » : renvoi au Gestionnaire de périphériques ?
- Téléchargement direct des pilotes GPU, ou orientation vers l'outil du fabricant ?
- Adresse NVIDIA non documentée, marquée expérimentale : oui ou non ?
- Protection système désactivée : prévenir seulement, ou proposer de l'activer ?
- Mode technicien (#13) : tout installer d'un clic, ou toujours pilote par pilote ? (seulement si docs/decisions.md ne la tranche pas)

## 7. Périmètre et hors périmètre
Dans : les deux étapes. Hors : #22 « GPU dédié des portables » (réutilise le service), #13 « Mode technicien et rédaction IA du compte rendu » (son lecteur de versions est réutilisé s'il existe), #12 « Moteur de diagnostic déterministe et rapport », #7 « Boîte à outils : liens de téléchargement direct » (DDU, liens des outils constructeur : via sa boîte à outils), #3 « Choix de l'écran de l'overlay et service des écrans » (DisplayTopology réutilisé, pas modifié), #4 « Socle de signaux » (son lecteur CfgMgr32 est étendu ici ; le lien PCIe reste à #4).

## 8. Livrables et méthode
Branche feature/peripheriques-pilotes depuis develop ; commits en français au présent. Étape 1 terminée, testée et relue avant l'étape 2 : dis-moi quand elle peut être fusionnée seule, #22 en dépend. Tests xUnit sur la logique pure : chaque refus de la garde, traduction des codes, filtre Firmware, causes de « aucune mise à jour », règle des 24 h. dotnet build -c Release et dotnet test verts. Lignes dans « Paramètres › Compatibilité de ce PC », ajoutées par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : périphériques en erreur, fantômes, Windows Update (géré ou non, service), protection système. Section README. « Expérimental » pour ce qui n'est pas vérifié sur une vraie machine (règle 6). Relecture /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
- Mon PC (i5-14600K, RTX 5070 Ti, TUF B760-PLUS WIFI) : liste comparée à devmgmt.msc, cachés compris ; code 22 après désactivation d'un périphérique sans risque (webcam USB), puis réactivation ; refus sur la carte graphique, le dernier clavier et le contrôleur NVMe de démarrage ; fantôme d'une ancienne clé USB nettoyé ; recherche Windows Update ; point de restauration et règle des 24 h ; sauvegarde des pilotes.
- Un portable de marque : outil constructeur détecté ; refus sur la batterie et le contrôleur embarqué.
````

### 19. Gestionnaire de disques

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #1 ; #10 conseillé
- Branche : `feature/gestionnaire-disques`
- Matériel pour vérifier : Une clé USB ou un disque de test sans aucune donnée, branché sur la machine de Denis (le disque système sert seulement à vérifier les refus).

````text
# Gestionnaire de disques (F11)

## 1. Objectif
Ma demande : « (F11) gestionnaire de disques ». Une vue complète des disques, partitions et volumes dans la page Stockage, plus un petit jeu d'opérations sûres. Le risque est la perte de données : les garde-fous comptent plus que les fonctions.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F11 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md si présentes (Grep).
- Vérifie ce qu'ont livré #1 (navigation et cycle de vie des pages : PageKeys, IPageLifecycle ; AppDataPaths ; ICompatibilityRowProvider ; registre des modifications ISystemChangeOwner) et #10 (BusType et MediaType ajoutés à DiskHealthService).
- Pose-moi les questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Ce qui existe déjà et à réutiliser
- DiskHealthService (CheckAsync ; FindBestMatch et TryParsePhysicalDriveIndex ≈142-211 ; EscapeWmiString ≈131, privé) : WMI root\Microsoft\Windows\Storage, santé par MSFT_PhysicalDisk et compteurs de fiabilité.
- BusType et MediaType de DiskHealthService (ajoutés par #10) : réutilise-les, ne relis pas BusType par MSFT_Disk. Si #10 n'est pas encore passé, ajoute-les à DiskHealthService sous ces noms et mets à jour leur ligne dans docs/decisions.md.
- DiskVolumeReader.ForDisk (internal ; premier appel synchrone, jamais sur le thread d'interface), DiskModelReader, DiskVolume et DiskSnapshot (HardwareModels.cs).
- StorageViewModel (DriveOption.TryFromDrive, LoadDrivesAsync ≈120) et StorageView.xaml ; styles PillSelector et PillItem (Theme.xaml) pour un sous-onglet « Disques ».
- DeletionGuard.GetRefusalReason (≈46) : modèle de garde qui refuse en cas d'exception. Aucun test ne le couvre : les tiens sont obligatoires.
- ShellFileOperations.ShowProperties, DiskHealthStatusToBrushConverter, confirmation MessageBox avec « Non » par défaut (StorageViewModel ≈362), CompatibilityViewModel DiskRow et DiskNamesRow.
- Rien aujourd'hui sur MSFT_Disk, MSFT_Partition, MSFT_Volume ni BitLocker (hors ce que #10 a pu ajouter : vérifie-le).

## 4. Approche retenue
- Lecture (nouveau lecteur dans Core/Hardware/Storage/) : MSFT_Disk (sans relire BusType, déjà fourni par DiskHealthService), MSFT_Partition, MSFT_Volume et les associations MSFT_DiskToPartition, MSFT_PartitionToVolume ; BitLocker par Win32_EncryptableVolume (root\CIMV2\Security\MicrosoftVolumeEncryption : ProtectionStatus, GetLockStatus, ConversionStatus) ; santé, BusType et MediaType par DiskHealthService ; barre graphique par disque, comme la Gestion des disques. Modèle pur, sans ManagementObject, pour tester la garde.
- Opérations V1 : initialiser un disque RAW (MSFT_Disk.Initialize, GPT) ; créer une partition sur l'espace libre (CreatePartition avec UseMaximumSize et AssignDriveLetter) puis MSFT_Volume.Format (NTFS ou exFAT, rapide) ; ajouter, changer ou retirer une lettre (AddAccessPath / RemoveAccessPath) ; renommer (SetFileSystemLabel) ; étendre dans l'espace contigu (GetSupportedSize puis Resize) ; mettre en ligne ou hors ligne un disque non système.
- Mode expert ou V2, à trancher : réduire (borné par le minimum de GetSupportedSize), supprimer (DeleteObject), effacer (Clear), convertir MBR/GPT (ConvertStyle, disque vide seulement).
- Opérations destructives (initialiser, formater, réduire, supprimer, effacer, convertir) : exclues du mode technicien (#13), selon docs/decisions.md.
- Exécution : ManagementObject.InvokeMethod hors du thread d'interface ; lire ReturnValue et ExtendedStatus (MSFT_StorageExtendedStatus) ; opérations longues en RunAsJob suivies par MSFT_StorageJob ; délai maximal et annulation.
- Garde DiskOperationGuard.GetRefusalReason, en liste blanche (partitions de données de base seulement) :
  - rien de destructeur sur un disque IsBoot, IsSystem ou BootFromDisk ;
  - jamais les partitions EFI, MSR ou de récupération (GptType c12a7328-f81f-11d2-ba4b-00a0c93ec93b, e3c9e316-0b5c-4db8-817d-f92df00215ae, de94bba4-06d1-4d40-a16a-bfd50179d6ac, à vérifier ; MBR 0x27) ;
  - disques dynamiques (MBR 0x42 ; GPT LDM 5808c8aa-7e8f-42e0-85d2-e1e90434cfb3 et af9b60a0-1431-4f62-bc68-3311714a69ad, à vérifier), Storage Spaces (BusType 16) et virtuels (14, 15) en lecture seule, BusType lu par DiskHealthService ;
  - jamais le disque du fichier d'échange, de l'hibernation ou de PCPerfSuite (AppContext.BaseDirectory) ;
  - BitLocker verrouillé ou chiffré : explication, aucune action ;
  - disque identifié par UniqueId ou ObjectId, jamais par Number (instable d'un démarrage à l'autre), et état relu juste avant d'agir.
- Aperçu avant/après ; confirmation saisie (numéro du disque + nom du volume), « Non » par défaut ; journal de chaque opération dans un fichier à part du dossier de données (AppDataPaths, #1 ; jamais un chemin %LOCALAPPDATA%\PCPerfSuite codé en dur), repris dans le diagnostic.
- Registre des modifications (ISystemChangeOwner, #1) : chaque changement durable s'y inscrit avec Describe(). RestoreAll() seulement pour les lettres, les noms de volume et l'état en ligne/hors ligne, garde relue avant d'agir. Initialiser, créer, formater et étendre : Describe() seulement, car aucune restauration ne supprime, ne réduit ni ne reformate une partition.
- Nouveau DiskManagerViewModel, chargé à l'ouverture par IPageLifecycle (#1) : ne grossis ni StorageViewModel ni MonitoringViewModel (1024 lignes).
- Écartés : VDS (obsolète), diskpart piloté par script (sortie traduite, erreurs mal remontées), cmdlets PowerShell (même fournisseur, un processus en plus).

## 5. Pièges connus
- RAW ne veut pas dire vide (disque Linux ou macOS) : avertir avant d'initialiser.
- Windows 11 : la partition WinRE suit souvent C:, qui ne peut donc pas s'étendre. L'expliquer, ne jamais la supprimer (plus de réinitialisation possible).
- RAID Intel RST ou AMD RAIDXpert (BusType 8) : un seul disque logique, jamais les membres de la grappe. Sous Storage Spaces, l'appariement par numéro de DiskHealthService ne marche pas.
- Fournisseur Storage lent ou bloqué, espace de noms absent sur un Windows abîmé : délai et message adapté (règle 3). Toute modification exige l'administrateur : le dire. Compteurs de fiabilité refusés derrière certains ponts USB : N/D avec la raison.
- Formater ou redimensionner un volume chiffré demande de le déverrouiller ou de suspendre BitLocker.
- Clés USB à plusieurs partitions : gérées depuis Windows 10 1703 seulement.
- Le numéro de série est une donnée personnelle (IsPersonal).
- Déplacer une partition est impossible par l'API : ne le promets pas.
- Règle 2 : chaque opération renvoie un résultat typé avec sa raison, jamais d'exception.

## 6. Questions à me poser
- Périmètre V1 : lecture + opérations sûres (recommandé), lecture seule avec un bouton « Ouvrir la Gestion des disques », ou clone complet ?
- Autoriser « étendre C: » quand l'espace libre suit directement C: ?
- Réduire, supprimer, effacer, convertir : mode expert protégé par confirmation saisie, V2, ou jamais ?

## 7. Périmètre et hors périmètre
Dans : lecture, opérations V1, garde, journal, diagnostic. Hors : #10 « Moteur de charge et bench CPU / RAM / disque » (BusType et MediaType de DiskHealthService réutilisés), #21 « Limiter CPU / RAM / disque / réseau par processus », #20 « RAM : répartition, fichier d'échange, mémoire du GPU intégré » ; défragmentation et TRIM (plus tard) ; la treemap existante reste telle quelle.

## 8. Livrables et méthode
Branche feature/gestionnaire-disques depuis develop ; commits en français au présent. Tests xUnit obligatoires sur la garde (chaque refus, et refus en cas d'exception, modèle DeletionGuard), sur la construction de l'arborescence disque → partitions → volumes et le calcul d'extension. dotnet build -c Release et dotnet test verts. Lignes dans « Paramètres › Compatibilité de ce PC », ajoutées par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : disques et partitions avec leurs attributs, BitLocker, espace de noms Storage, dernière opération. Section README. « Expérimental » pour ce qui n'est pas vérifié sur une vraie machine (règle 6). Relecture /review-max avant de fusionner dans develop, jamais sur master.

## 9. À vérifier sur une vraie machine
- Avec une clé USB ou un disque de test SANS données, rendu RAW à la main (diskpart clean, après avoir vérifié deux fois le numéro du disque) : initialiser, créer et formater en exFAT puis NTFS, changer la lettre, renommer, étendre (espace libre laissé derrière la partition, à la main si besoin), hors ligne puis en ligne ; comparer à diskmgmt.msc.
- Sur mon disque système : toute opération destructive refusée avec sa raison ; EFI et récupération intouchables.
- Interface fluide pendant un formatage.
````

### 20. RAM : répartition, fichier d'échange, mémoire du GPU intégré

- Modèle / effort : **Opus 5.5 / xhigh** · taille M · relecture /review-max
- Dépend de : #1 ; #14 conseillé
- Branche : `feature/memoire`
- Matériel pour vérifier : Machine de Denis (répartition, fichier d'échange, purge, message VGM) ; un APU AMD compatible mémoire graphique variable (Ryzen AI 300 avec au moins 16 Go) chez un testeur.

````text
# RAM : répartition, fichier d'échange, mémoire du GPU intégré (F13)

## 1. Objectif
Ma demande : « (F13) allocation de RAM ». Le sens est à trancher d'abord. Recommandé : répartition de la RAM, fichier d'échange, mémoire réservée au GPU intégré AMD, plus une purge manuelle « expert ».

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1), notamment la portée de la règle 5 pour la taille de la mémoire du GPU intégré. Ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F13 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md si présentes (Grep).
- Première question, avant tout : que voulais-je dire ?
  1. répartition façon RAMMap ;
  2. purge de la liste d'attente ;
  3. fichier d'échange ;
  4. RAM donnée au GPU intégré ;
  5. limite par processus, qui relève de #21.
  Puis les autres questions du §6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Vérifie ce qu'ont livré #1 (emplacement de la page, cycle de vie, registre des modifications de Windows, fournisseurs de lignes) et #14 (session ADLX partagée).

## 3. Ce qui existe déjà et à réutiliser
- PdhCounterSampler (internal, PdhAddEnglishCounterW, un compteur par instance), SystemMemoryReader.Read (ullTotalPhys n'est pas la RAM installée), MemoryModuleReader, MemorySnapshot, MemoryInfoViewModel (≈226 de MonitoringViewModel.cs).
- Session ADLX partagée (#14) ; AdlxNative (QueryInterface, GetObject) et AdlxProbeGuard.
- AppSettings.OriginalPowerValues et WindowsPerformanceSettingsService (mémoriser l'origine avant la première écriture), registre des modifications de Windows (#1), PerformanceTweak.RequiresRestart, CompatibilityViewModel.MemoryRow (≈344).

## 4. Approche retenue
- (1) Répartition, relue seulement page affichée :
  - compteurs \Memory\Standby Cache Core Bytes, Standby Cache Normal Priority Bytes, Standby Cache Reserve Bytes, Modified Page List Bytes, Free & Zero Page List Bytes, Cache Bytes, Pool Paged Bytes, Pool Nonpaged Bytes, Committed Bytes, Commit Limit ;
  - mémoire compressée ≈ jeu de travail du processus « Memory Compression » (approximation, à dire) ;
  - réservée au matériel = installée − visible.
- (3) Fichier d'échange :
  - lecture : Win32_ComputerSystem.AutomaticManagedPagefile, Win32_PageFileSetting (InitialSize, MaximumSize, disque), usage par Win32_PageFileUsage ;
  - redémarrage requis ;
  - avertir avant de le désactiver ou de le réduire : plantages à la limite de mémoire engagée, plus de fichier de vidage ;
  - origine mémorisée et inscrite au registre des modifications de Windows de #1, bouton « Rétablir ».
- (4) GPU intégré AMD : IADLXSystem3::GetVariableGraphicsMemory puis IADLXVariableGraphicsMemory (IsSupported, GetAvailableOptions, GetOption, SetOption ; ADLX ≥ 1.5 ; emplacements à vérifier dans les en-têtes du SDK), par la session partagée de #14.
  - Pris en charge sur Ryzen AI 300 et plus récents, 16 Go minimum, pilote récent ; ailleurs IsSupported = faux.
  - SetOption redémarre lui-même le PC et change la taille réservée par le firmware : applique la décision « règle 5 » de docs/decisions.md. Si elle réserve ce réglage à la lecture, affiche les options et renvoie vers AMD Software.
  - Sinon : confirmation, conseil d'enregistrer son travail, origine inscrite au registre ; chaque Go donné au GPU est retiré à Windows.
  - Raisons distinctes (règle 3) : APU non compatible, pilote trop ancien, Intel sans API publique (ouvrir Intel Graphics Software), taille BIOS non modifiable depuis Windows, carte dédiée seule : sans objet.
- (2) Purge : bouton manuel « expert » expérimental, jamais de minuterie.
  - NtSetSystemInformation(SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4), après activation de SeProfileSingleProcessPrivilege.
  - API non documentée : exception commentée au principe « API documentées » (en-tête de ProcessService), try/catch.
  - Dire que le gain est rare (surtout un ancien bogue de saccades de Windows 10 1803-1809).
- Écartés : nettoyeur automatique, bidouilles de registre, taille UMA écrite dans le BIOS.
- Nouveau MemoryViewModel ; ne grossis pas MonitoringViewModel (1024 lignes).

## 5. Pièges connus
- ADLX : passe par la session partagée de #14. Pourquoi : AdlxNative a un état statique et AdlxGpuBackend.Dispose appelle Terminate ; sur un APU seul, AdlxGpuBackend peut échouer et fermer ADLX ; avec une GeForce, GpuControlService retient NVAPI et n'initialise jamais ADLX. Si la session n'existe pas encore, crée-la au nom et à l'emplacement de docs/decisions.md. Nouveaux emplacements sous le témoin AdlxProbeGuard.
- Modifier le fichier d'échange par WMI peut exiger EnablePrivileges : vérifie.
- Fichier d'échange désactivé ou trop petit : jamais sans confirmation, jamais depuis un préréglage.
- En mode éco, le groupe Memory n'est pas relu.

## 6. Questions à me poser
- Sens voulu (1 à 5, plusieurs possibles) ?
- Purge : bouton manuel (recommandé), automatique façon ISLC (déconseillé), ou rien ?
- Accepter que le pilote AMD redémarre le PC après confirmation (si la règle 5 l'autorise) ?

## 7. Périmètre et hors périmètre
Dans : les sens que je retiens, avec leurs lignes de diagnostic et leur inscription au registre des modifications. Hors : #21 « Limiter CPU / RAM / disque / réseau par processus », #12 « Moteur de diagnostic déterministe et rapport » (XMP, double canal), #10 « Moteur de charge et bench CPU / RAM / disque », #14 (session ADLX).

## 8. Livrables et méthode
Branche feature/memoire depuis develop ; commits en français au présent. Tests xUnit sur la logique pure : parts de la répartition, valeurs manquantes, tailles du fichier d'échange, origine et rétablissement, raisons VGM. dotnet build -c Release et dotnet test verts. Lignes « Répartition de la RAM », « Fichier d'échange », « Mémoire du GPU intégré » par des fournisseurs de lignes (#1) dans « Paramètres › Compatibilité de ce PC ». Section README. Purge et VGM « expérimental » (règle 6). Relecture /review-max avant de fusionner dans develop (fichier d'échange, redémarrage imposé par le pilote, réglage du firmware, API non documentée), jamais sur master.

## 9. À vérifier sur une vraie machine
- Mon PC :
  - répartition comparée au Gestionnaire des tâches et à RAMMap ;
  - fichier d'échange modifié, redémarrage, « Rétablir » ;
  - purge visible dans RAMMap ;
  - message VGM juste (Intel UHD 770 ou « sans objet »).
- Un APU AMD (Ryzen AI 300) : options VGM lues, puis appliquées avec redémarrage si la règle 5 le permet.
````

### 21. Limiter CPU / RAM / disque / réseau par processus

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #1, #4 ; #5, #9 conseillés
- Branche : `feature/limites-processus`
- Matériel pour vérifier : machine de Denis (i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI)

````text
# Limiter CPU / RAM / disque / réseau par processus (F14)

## 1. Objectif
Ma demande : « Pouvoir limiter l'usage de ram/cpu/disque/réseau par processus. » Depuis Processus (clic droit « Limiter… ») et par règles persistantes par exécutable ; ce que Windows ne permet pas, l'interface le dit.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F14 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md, si présentes (Grep, pas en entier).
- Vérifie ce qu'ont livré #1 (PageKeys, IPageLifecycle, AppDataPaths, ICompatibilityRowProvider, ISystemChangeOwner), #5 (CpuTopology), #9 (ApplicationMatch, hook de premier plan), #4 (SessionJournal, étape StartupRecovery) et #13 (mode portable, racine portable d'AppDataPaths).
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier.
- Puis soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Existant à réutiliser
- `ProcessService.Terminate` (src/PCPerfSuite.Core/Processes/ProcessService.cs, ≈ l.554) : handle dédié aux droits minimaux, revérification PID + heure de démarrage (`ProcessIdentity`), résultat typé (`TerminateResult`). Le handle de relevé (`EnsureHandle`) reste en lecture seule et suffit pour GetPriorityClass, GetProcessAffinityMask, IsProcessInJob.
- `ProcessTerminationGuard.GetRefusalReason`, à décliner en `ProcessLimitGuard` (ajouter dwm, audiodg, explorer, et les processus de PCPerfSuite eux-mêmes : l'app, le worker de bench et le `--watchdog`, reconnus par le chemin de l'exe, pas par le nom) ; une exception refuse.
- ProcessesViewModel : `ExpandAggregate` (un agrégat est un arbre, cible naturelle d'un job), `TerminateManyAsync` (modèle de lot : garde, confirmation « Non » par défaut, StatusText), `ProcessRowViewModel.ToInfo`.
- `ProcessesSettings`, `AppSettingsStore.Update` (chaînes plutôt qu'enums), `CompatibilityViewModel.ProcessIoTraceRow`.
- `CpuTopology` (#5, Core/Hardware/Cpu : cœur, fils SMT, L3/CCD, classe) pour le choix des cœurs ; `ApplicationMatch` (#9, Core/Processes) pour les règles ; `SessionJournal` et l'étape `StartupRecovery` d'App.OnStartup (#4) pour les limites restées actives.

## 4. Approche retenue
Étape 0, si ce n'est pas déjà fait, en commit séparé sans changement de comportement : découper ProcessesViewModel.cs (≈ 2218 lignes, 5 classes), une classe par fichier.

Core (src/PCPerfSuite.Core/Processes/) : `ProcessLimitService`, `ProcessLimitGuard`, `ProcessLimitModels` ; chaque écriture mémorise la valeur d'origine et renvoie un résultat typé (règle 2). Chaque limite posée et chaque stratégie QoS s'inscrit au registre des modifications (ISystemChangeOwner, #1), avec Describe() (processus ou exe, limite, valeur d'origine) et RestoreAll() (ControlFlags 0, valeurs d'origine si PID + heure concordent, stratégies PCPerfSuite-* supprimées).

CPU
- Priorité : SetPriorityClass. Jamais REALTIME ; HIGH après avertissement.
- Mode efficacité : SetProcessInformation(ProcessPowerThrottling), ControlMask = StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED (EcoQoS), plus IDLE ou BELOW_NORMAL.
- Cœurs : choix sur CpuTopology (#5) ; SetProcessAffinityMask ; au-delà de 64 processeurs logiques, groupes de processeurs ou CPU sets (SetProcessDefaultCpuSets).
- Plafond dur optionnel : Job Object nommé, AssignProcessToJobObject (PROCESS_SET_QUOTA | PROCESS_TERMINATE), JobObjectCpuRateControlInformation ENABLE | HARD_CAP, CpuRate = % × 100, en % du PC entier et pour tout le job. Jamais KILL_ON_JOB_CLOSE. Un processus ne quitte pas un job : lever = ControlFlags 0. Seuls les enfants créés après l'affectation y entrent : ajoute les autres par l'arbre de processus.

RAM
- Jamais de limite de mémoire engagée (PROCESS_MEMORY, JOB_MEMORY) : l'allocation échoue, l'app plante.
- Priorité mémoire (ProcessMemoryPriority), « Vider le jeu de travail » (EmptyWorkingSet), alerte au-delà de X Go par JobObjectNotificationLimitInformation (JobMemoryLimit) : Windows notifie sans refuser.
- Expérimental : plafond de RAM physique (SetProcessWorkingSetSizeEx + QUOTA_LIMITS_HARDWS_MAX_ENABLE ; défauts de page, pas de plantage).

Disque
- Priorité E/S seulement : NtSetInformationProcess(ProcessIoPriority = 33 ; 0/1/2 = très basse/basse/normale), non documentée, donc expérimentale.
- Aucun plafond en Mo/s : SetIoRateControlInformationJobObject n'est plus pris en charge depuis Windows 10 1607. L'interface le dit.

Réseau
- Plafond montant : stratégie QoS WMI (ROOT\StandardCimv2, MSFT_NetQosPolicySettingData, équivalent de New-NetQosPolicy -AppPathNameMatchCondition -ThrottleRateActionBitsPerSecond) en ActiveStore, nommée PCPerfSuite-*, recréée au lancement ; jamais dans le magasin persistant (elle survivrait à la désinstallation). Elle vise un exe, pas un PID. Vérifie la création par WMI avant de t'y fier.
- Variante par PID, à tester : JobObjectNetRateControlInformation (MaxBandwidth, sortant seulement).
- Descendant impossible sans pilote noyau WFP : le dire.

Règles
- Identifiées par ApplicationMatch (#9) : chemin complet normalisé (+ éditeur), jamais par le seul nom, même si ApplicationMatch le permet sur choix explicite.
- #9 n'a qu'un hook de premier plan : crée ici la surveillance ETW ProcessStart, en service Core réutilisable, indépendante de l'onglet et du mode éco, sans sondage WMI. Elle applique les règles au lancement des processus. Règles sur ApplicationMatch (#9), cœurs sur CpuTopology (#5).
- Aucune session ETW en mode portable (#13) : les règles au lancement y sont indisponibles, et l'interface comme la ligne de compatibilité le disent (règle 3).
- Règles persistées par AppSettingsStore ; tout autre fichier écrit par l'app passe par AppDataPaths (#1), jamais un chemin %LOCALAPPDATA%\PCPerfSuite codé en dur.
- En quittant (MainViewModel.Dispose) : tout lever (ControlFlags 0, valeurs d'origine si PID + heure concordent, stratégies QoS supprimées).

Interface : « Limiter… » dans le menu contextuel (agrégat et sélection multiple compris), limites actives dans le panneau de détail, liste des règles à l'emplacement prévu par #1 (PageKeys, IPageLifecycle), dans des vues à part.

## 5. Pièges connus
- ProcessIoTracer n'active que DiskIO | NetworkTCPIP et s'arrête avec l'onglet : il faut le mot-clé Process dans une session à part (ou une session légère Microsoft-Windows-Kernel-Process, à vérifier). ProcessService appartient à ProcessesViewModel : le service de limites vit dans MainViewModel.
- Si PCPerfSuite plante, la limite reste jusqu'à la fin du processus, et le nom du job disparaît sans doute avec le dernier handle (non vérifié) : consigne les limites actives dans le SessionJournal de #4 ; l'étape StartupRecovery d'App.OnStartup (#4) avertit au lancement suivant.
- SeDebugPrivilege n'est volontairement pas activé (commentaire en tête de ProcessService.cs) : garde ce choix ; les services système refuseront souvent, à expliquer.
- Refus à expliquer (règle 3) : processus protégé (PPL, antivirus), job existant à restrictions d'interface (bacs à sable des navigateurs, à vérifier), DFSS en Bureau à distance (erreur 50), EcoQoS réel seulement sous Windows 11, QoS non vérifiée hors domaine (clé « Do not use NLA » peut-être requise).
- Plafond CPU dur = saccades : tâches de fond seulement, avec avertissement.
- Anti-triche (EAC, BattlEye, Vanguard) : un handle d'écriture sur un jeu peut être refusé ou repéré. Liste d'exclusion, message, jamais un jeu sans demande explicite.
- Le tweak « power-throttling » d'Optimisation Windows (PowerThrottlingOff = 1) peut neutraliser EcoQoS : vérifier et avertir.

## 6. Questions à me poser
1. Cible : applis de fond pendant que je joue, ou aussi les jeux (anti-triche) ?
2. Plafond CPU dur en V1, ou seulement priorité, mode efficacité et cœurs ?
3. RAM : alerte + priorité + « vider », ou aussi le plafond de RAM physique expérimental ?
4. Réseau : l'upload seul me suffit-il ?
5. En quittant : lever toutes les limites (recommandé) ou les laisser actives ? (seulement si docs/decisions.md ne la tranche pas)

## 7. Périmètre et hors périmètre
Hors périmètre : débit disque et réseau descendant (impossibles sans pilote noyau) ; limites portées par des profils (#8 Groupes de profils CPU + GPU + ventilation, #9 Profils automatiques selon l'usage) ; core parking (#5 Core parking et visuel d'usage par cœur) ; structure de navigation (#1 Navigation par sections et cycle de vie des pages).

## 8. Livrables et méthode
- Branche feature/limites-processus depuis develop ; commits en français au présent, comme l'historique.
- Tests xUnit : ProcessLimitGuard (dont le refus des processus de PCPerfSuite), correspondance des règles, CpuRate, masques et CPU sets, sérialisation tolérante. dotnet build -c Release et dotnet test verts.
- Ligne « Limites par processus » dans Paramètres › Compatibilité de ce PC, ajoutée par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : Job Objects, EcoQoS, QoS, DFSS, surveillance ETW (absente en mode portable), administrateur.
- Section du README ; « expérimental » (règle 6) pour la priorité E/S, le plafond RAM physique, la bride réseau et tout ce qui n'est pas vérifié sur une vraie machine.
- `/review-max` avant de fusionner dans develop ; jamais sur master.

## 9. À vérifier sur une vraie machine
Sur ma machine (i5-14600K, RTX 5070 Ti, TUF B760-PLUS WIFI ; tes tests tournent sur un i5-13500T) :
- Mode efficacité sur un navigateur : feuille verte dans le Gestionnaire des tâches, threads sur les cœurs E, avec et sans le tweak power throttling.
- Plafond CPU 10 % sur un encodage : respecté, puis levé.
- Règle par exe : appli relancée, limite réappliquée, enfants compris.
- Upload bridé à 1 Mo/s : effectif ou non.
- Fermeture : tout revient à l'origine, `Get-NetQosPolicy -PolicyStore ActiveStore` vide.
- Refus propre sur un jeu avec anti-triche et sur un processus système.
````


## Lot G · Portables et RGB

#22 attend le service des écrans (#3) et celui des périphériques (#18). #24 attend #23. La règle 5 doit être tranchée avant.

### 22. GPU dédié des portables : l'endormir pour la batterie et la chauffe

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #3, #18 ; #9 conseillé
- Branche : `feature/gpu-portable`
- Matériel pour vérifier : un portable Optimus/hybride (NVIDIA + iGPU), idéalement ASUS ou Lenovo ; la machine de jeu de Denis (bureau, UHD 770 + RTX 5070 Ti) seulement pour vérifier le masquage sur bureau et l'éditeur de préférence GPU

````text
# GPU dédié des portables : l'endormir pour la batterie et la chauffe (F9)

## 1. Objectif
Ma demande : « Pour les PC portables, ajouter la possibilité de switcher entre GPU dédié et GPU intégré au CPU (donc éteindre le gros GPU pour économiser de la batterie / chauffe). »

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F9 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md, si présentes (Grep, pas en entier).
- Décision préalable : la vraie bascule (MUX, mode Eco) passe toujours par une écriture firmware (ASUS DEVS, Lenovo GameZone Set*, MSI registre EC 0xD1 + variable UEFI, HP GpuMode du BIOS) : conflit direct avec la règle 5. Elle est exclue, sauf exception que j'écrirai moi-même dans CLAUDE.md. Ne l'écris pas à ma place.
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier. Puis soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Existant à réutiliser
- DisplayTopology (#3, Core/Hardware/Displays : QueryDisplayConfig, LUID, technologie de sortie) pour les écrans branchés sur le dGPU, et service des périphériques (#18, Core/Devices : CM_* ; ajoute-y DEVPKEY_Device_PowerData s'il manque) pour son état d'alimentation, à retrouver par Grep.
- GpuIdentity (#2, Core/Hardware/Gpu : marque, nom, identifiants PCI si lus, LUID facultatif) pour désigner le dGPU ; renseigne son LUID s'il manque.
- ApplicationMatch (#9, Core/Processes : chemin complet normalisé) pour les chemins d'exe : préférences GPU, processus qui tiennent le dGPU éveillé, applis à fermer et relancer.
- Briques de #1 : registre des modifications (ISystemChangeOwner, Core/SystemChanges), AppDataPaths (Core/Environment), ICompatibilityRowProvider (Core/Compatibility).
- src/PCPerfSuite.Core/Hardware/LaptopFans/ comme modèle de module par marque : ILaptopFanProvider (IsVerified, TryDetect), LaptopFanService.CreateProvider (d'après MachineInfo.Current.Manufacturer), LaptopFanSupport ; WmiMethods (à déplacer dans un espace de noms neutre) ; AsusFanProvider (DSTS sur AsusAtkWmi_WMNB).
- MachineInfo (namespace PCPerfSuite.Core.SystemInfo ; rendu testable par #12 s'il l'a déjà fait), GpuControlViewModel.DedicatedGpu, HardwareMonitorService.SetBackgroundGroups, PdhCounterSampler (un seul compteur : ajouter les jokers, PdhGetFormattedCounterArrayW), MetricCatalog.GpuSensorHint (infobulle du GPU en veille).
- SessionUser, ProcessTerminationGuard, ouverture via explorer.exe depuis l'app élevée (StorageViewModel).

## 4. Approche retenue (V1, aucune écriture firmware)
Prérequis, en commits séparés :
- PCPerfSuite empêche probablement lui-même le dGPU de dormir (non vérifié) : le groupe GPU de LibreHardwareMonitor est relu à chaque tick, et SensorGroup.Gpu couvre tous les GPU. Suspendre la lecture du seul dGPU quand son nœud est en D3 et afficher « GPU dédié en veille » au lieu de N/D. Rapprochement LHM ↔ nœud PnP : GenericGpu.DeviceId (vérifie dans LHM 0.9.6).
- GPU qui disparaît ou réapparaît : GpuControlService ne redétecte jamais (_initialized), le Computer LHM est ouvert une fois, MachineInfo.VideoControllers est figé (Lazy), handles NVAPI peut-être périmés (NvApiSession, #14). Au minimum, ne jamais planter.
- MachineInfo non testable (constructeur privé) : constructeur internal ou fabrique, sauf si #12 l'a déjà fait.

1) Panneau d'état, lecture seule
- D0/D3, par le service des périphériques (#18) : CM_Get_DevNode_PropertyW(DEVPKEY_Device_PowerData), CM_POWER_DATA.PD_MostRecentPowerState.
- Écrans sur le dGPU, par DisplayTopology (#3) : chemin actif (QDC_ONLY_ACTIVE_PATHS) dont targetInfo.adapterId = LUID du dGPU (IDXGIAdapter1::GetDesc1). Écran interne (DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL ou DISPLAYPORT_EMBEDDED) sur le dGPU = MUX, Advanced Optimus ou ADS en mode dGPU : il ne peut pas dormir, le dire (Advanced Optimus et ADS : pas d'API publique, détection seulement).
- Processus qui le tiennent éveillé : PDH « \GPU Process Memory(pid_*_luid_<LUID>_*)\Dedicated Usage » et « \GPU Engine(…)\Utilization Percentage » (WDDM 2.0+).
- Mode constructeur, lu seulement : ASUS DSTS 0x00090020 (Eco) et 0x00090016 (MUX), identifiants repris de G-Helper, à vérifier ; Lenovo LENOVO_GAMEZONE_DATA GetIGPUModeStatus et GetGSyncStatus. Un module par marque dans src/PCPerfSuite.Core/Hardware/LaptopGpu/, expérimental tant que non vérifié (règle 6). HP (lisible par BIOS WMI, pas en V1), MSI, Dell, Razer, Acer : « marque pas encore prise en charge ».

2) « Libérer le GPU dédié », actif seulement en mode hybride sans écran sur le dGPU
- Préférence GPU (Windows 10 1803+) dans HKEY_USERS\<SID de la session>\Software\Microsoft\DirectX\UserGpuPreferences : nom = chemin complet de l'exe (obtenu par ApplicationMatch, #9), REG_SZ « GpuPreference=1; » (0 Windows décide, 1 économie, 2 performances). Seules ces valeurs sont documentées : ne réécrire que la paire GpuPreference, préserver les autres, mémoriser l'origine (sous AppDataPaths, #1). Chaque préférence écrite s'inscrit au registre des modifications (ISystemChangeOwner, #1) avec Describe() et RestoreAll() (valeur d'origine rétablie, ou valeur supprimée si elle n'existait pas). Liste visible des préférences posées, bouton « Rétablir ». RegistryHelper ne gère que les DWORD.
- Puis fermeture propre (WM_CLOSE, confirmation « Non » par défaut, jamais de kill ni de processus protégé) et relance via explorer.exe. Le pilote endort ensuite le dGPU (D3cold).

3) Éditeur de préférence GPU par application (intégré / dédié / Windows décide), service Core réutilisable plus tard par les profils (#8, #9) ; mêmes règles d'écriture et même inscription au registre des modifications (ISystemChangeOwner, #1).

Mode sur batterie, selon la question 3 : déclencheur « sur batterie » branché dans AutoProfileSwitcher (#9), désactivé par défaut, qui s'appuie sur le service de 2) ; jamais de fermeture d'appli sans confirmation.

Vraie bascule : message adapté à la marque (« Sur ce ROG, le mode Eco se règle dans Armoury Crate ou G-Helper »). PC de bureau : « réservé aux portables à double GPU ».

## 5. Pièges connus
- Lire PowerData, les compteurs PDH GPU ou énumérer DXGI peut réveiller le dGPU : non vérifié. Lectures espacées, rien par LHM ni NVAPI en D3, à mesurer.
- Les noms d'instance « GPU Engine » ne sont pas contractuels (commentaire en tête de ProcessService.cs) : analyse défensive.
- Mauvaise ruche si l'app élevée tourne sous un autre compte (SessionUser.IsOtherProfile) : SessionUser lit le SID d'explorer.exe sans l'exposer, expose-le.
- L'appli a le dernier mot sur son GPU ; OpenGL/Vulkan dépend du pilote (non vérifié). Écran externe câblé sur le dGPU (HDMI des portables gaming) : aucun effet, le dire.
- Relance par explorer.exe : les arguments d'origine sont perdus ; quand ils comptent, conseiller de rouvrir soi-même. Certains antivirus surveillent les fermetures en série.
- Services (conteneurs NVIDIA…) : lister, ne pas fermer.
- HardwareMonitorService.cs (≈ 1280 lignes) et GpuControlViewModel.cs (≈ 705) : code nouveau à part.

## 6. Questions à me poser
1. Exception à la règle 5 pour les bascules constructeur (Eco, MUX), ou non (recommandé : non) ? (seulement si docs/decisions.md ne la tranche pas) Même si oui, rien de firmware ici : conversation à part, après mon ajout dans CLAUDE.md.
2. Fermer et relancer les applis après confirmation, ou seulement les lister avec un conseil ?
3. Mode automatique sur batterie (#9 est fusionné et a exclu ce point) : service exposé ici et déclencheur « sur batterie » branché dans AutoProfileSwitcher (#9), désactivé par défaut (recommandé), ou report en V2, noté dans docs/decisions.md ?
4. Désactivation temporaire du périphérique (CM_Disable_DevNode non persistant) en option expert, malgré l'écran noir possible (recommandé : non) ? Si oui, inscrite au registre des modifications (ISystemChangeOwner, #1).
5. Éditeur de préférence GPU aussi sur les PC de bureau à iGPU + carte dédiée ?

## 7. Périmètre et hors périmètre
Hors périmètre : toute écriture EC, ACPI, BIOS ou UEFI (règle 5) ; mode automatique sur batterie au-delà du déclencheur « sur batterie » de la question 3 (#9 Profils automatiques selon l'usage) ; désactivation générique de périphériques (#18 Gestionnaire de périphériques et de pilotes) ; branchement dans les groupes (#8 Groupes de profils CPU + GPU + ventilation).

## 8. Livrables et méthode
- Branche feature/gpu-portable depuis develop ; commits en français au présent, comme l'historique.
- Tests xUnit : fusion de la chaîne UserGpuPreferences, analyse des noms PDH et des LUID, décodage DSTS/GameZone, activation du bouton, choix du module par marque. dotnet build -c Release et dotnet test verts.
- Ligne « GPU dédié du portable » dans Paramètres › Compatibilité de ce PC, ajoutée par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : D0/D3, écrans branchés dessus, mode constructeur, raison d'absence.
- Section du README ; « expérimental » pour ce qui n'est pas vérifié sur une vraie machine.
- `/review-max` avant de fusionner dans develop ; jamais sur master.

## 9. À vérifier sur une vraie machine
Sur un portable Optimus, idéalement ASUS ou Lenovo (tes tests tournent sur un i5-13500T de bureau) :
- PCPerfSuite ouvert seul, panneau affiché : le dGPU passe-t-il en D3 et y reste-t-il ?
- Jeu lancé puis fermé : D0, puis « GPU dédié en veille ».
- « Libérer » sur un navigateur : bonne ruche, autres paires intactes, appli relancée, dGPU en D3.
- Écran HDMI branché : bouton désactivé avec la raison.
- Mode lu identique à Armoury Crate ou Vantage.
Sur ma machine de jeu (bureau : i5-14600K avec UHD 770, RTX 5070 Ti, TUF B760-PLUS WIFI) : fonction masquée avec le bon message ; éditeur si retenu sur bureau.
````

### 23. Éclairage RGB : socle et OpenRGB

- Modèle / effort : **Opus 5.5 / xhigh** · taille L · relecture /review-max
- Dépend de : #1 ; #7 conseillé
- Branche : `feature/rgb-openrgb`
- Matériel pour vérifier : machine de Denis (ASUS TUF B760-PLUS WIFI Aura, i5-14600K, RTX 5070 Ti) + périphériques RGB ; RAM RGB et ventilateurs ARGB si disponibles

````text
# Éclairage RGB : socle et OpenRGB (F8, partie 1)

## 1. Objectif
Ma demande : « Ajouter la gestion des appareils RGB (ventilateurs et périphériques). » Partie 1 : ventilateurs ARGB, carte mère, RAM, GPU et périphériques via OpenRGB, sur un socle qui accueillera Dynamic Lighting et les effets (#24).

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F8 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md, si présentes (Grep, pas en entier).
- AVANT tout développement : test de 1 à 2 h d'OpenRGB 1.0 avec PCPerfSuite sur ma machine. Donne-moi le protocole et attends mes résultats : OpenRGB prend-il par défaut le mutex Global\Access_SMBUS.HTP.Method (non vérifié) ? Températures SPD toujours justes ? Aucun gel de Computer.Open ni du système ?
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier. Puis soumets-moi un plan en mode plan avant d'écrire du code.

## 3. Existant à réutiliser
- Modèle de module détecté à l'exécution : ILaptopFanProvider / LaptopFanService (src/PCPerfSuite.Core/Hardware/LaptopFans/ : IsVerified, LaptopFanSupport, DetectionError).
- OfficialInstaller, paramétré par source par #7, et son dossier sécurisé ProgramDataFolder (#7) : HTTPS, hôtes autorisés à chaque redirection, Authenticode + éditeur attendu, en-tête MZ, donc pas de MSI en l'état. InstallationsViewModel : PawnIoItemViewModel (installation vérifiée), RtssItemViewModel (page officielle, IsRequired = false : ne fait pas clignoter Paramètres). Si #7 ou #18 ont ajouté les MSI, le SHA-256 ou une entrée OpenRGB au catalogue, réutilise-les.
- Briques de #1 : PageKeys et IPageLifecycle (page Éclairage), ICompatibilityRowProvider (rubrique du diagnostic), AppDataPaths (réglages, dont l'opt-in SMBus).
- PawnIO déjà proposé (OpenRGB 1.0 en a besoin pour la RAM et la carte mère) ; MachineInfo.SoftwareFanControlRefused (Chassis != Desktop) comme test « bureau avéré ».
- Commentaire de HardwareMonitorService (≈ l.87) : un détenteur du mutex SMBus (Afterburner, Armoury Crate) bloque Computer.Open.

## 4. Approche retenue
Principe : PCPerfSuite ne parle jamais directement au matériel RGB : ni SMBus, ni USB rétro-conçu, ni EC.

Socle, src/PCPerfSuite.Core/Hardware/Lighting/ (brique partagée « socle d'éclairage » : mets à jour sa ligne dans docs/decisions.md)
- ILightingProvider { Id, DisplayName, IsVerified, DetectAsync(ct) → LightingProviderStatus, Devices, ApplyAsync(device, effect, ct) → LightingResult, Release() }.
- LightingProviderStatus : NotInstalled, NotRunning, VendorSoftwareConflict(nom), NeedsAdmin, UnsupportedOs, NoDevice, Ready.
- LightingDevice { StableKey (fournisseur + VID:PID + série ou emplacement), Name, Kind (Motherboard, Dram, Gpu, Cooler, FanController, LedStrip, Keyboard, Mouse, Headset, Other), Bus (Hid, SmBus, GpuI2c, AcpiWmi, Unknown), LedCount, Zones, HardwareModes, CanWrite, ReadOnlyReason }.
- LightingEffect minimal ici (Éteint, Couleur fixe), sérialisable en chaînes ; #24 l'étend.
- Best-effort avec délai (règle 2) : OpenRGB injoignable = un statut, pas une exception.

Fournisseur OpenRGB
- OpenRGB.NET 3.1.1 (NuGet, MIT) : protocole 4 au plus, le serveur 1.0 (protocole 6) négocie. Appareils adressés par index : tout relire sur DeviceListUpdated.
- TCP 127.0.0.1:6742, délai et réessais : la détection d'OpenRGB prend plusieurs secondes.
- OpenRGB (GPL-2) installé à part, jamais embarqué. Bouton dans Installations, par l'OfficialInstaller paramétré par source de #7 (une source OpenRGB de plus, pas un installeur à part) : https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases/latest, asset *_Windows_64_*.msi ; codeberg.org (et l'hôte réel des fichiers) aux hôtes autorisés de cette source. L'étude F10 cite plutôt GitHub Releases : vérifie la source officielle actuelle. Signature du MSI non vérifiée : si absente, page officielle (comme RTSS) ou SHA-256 épinglé par version.
- Démarrage : se connecter à une instance ou au service existant ; sinon lancer le binaire installé sous Program Files avec « --server --server-host 127.0.0.1 --noautoconnect » (vérifie ces options en 1.0). Toujours 127.0.0.1 : le serveur n'a pas d'authentification, et l'adresse d'écoute par défaut varie selon les sources.
- Écriture : couleurs et mode seulement. Jamais SaveMode (écrit en flash), jamais ResizeZone.
- Appareils SMBus (RAM, cartes mères qui y passent) : opt-in expérimental avec avertissement, désactivé par défaut (réglage enregistré sous AppDataPaths, #1). Sa détection touche déjà le SMBus : l'opt-in porte sur nos écritures ; couper ses détecteurs SMBus par sa configuration est à étudier.

Logiciels constructeur : détecteur par processus et services (Armoury Crate/LightingService, iCUE, Synapse, G HUB, MSI Center, GCC, NZXT CAM, L-Connect, SignalRGB ; noms exacts à relever). S'il tourne : appareils de sa marque « gérés par X » en lecture seule, OpenRGB ni lancé ni piloté automatiquement.

Portable (Chassis != Desktop) : écriture seulement sur les appareils HID/USB, tri par le champ location d'OpenRGB (format à vérifier). Rétroéclairage clavier par l'EC : « non pris en charge sur portable : contrôleur embarqué » (règle 5).

Interface minimale : page Éclairage, à l'emplacement prévu par #1 (PageKeys, IPageLifecycle), avec l'inventaire (appareil, source, bus, statut, raison) et un test « couleur fixe » ; #24 fera la page complète.

## 5. Pièges connus
- Les écritures SMBus d'OpenRGB sont le seul vrai risque de brique (Aorus Z390, gels T-Force Delta, ≈ 10 % CPU avec 4 barrettes animées) : jamais d'effet logiciel sur le SMBus.
- Collision avec la lecture SPD de LibreHardwareMonitor (IsMemoryEnabled) : températures fausses ou appareil dans un état invalide si le mutex n'est pas partagé.
- Lancer OpenRGB depuis l'app élevée = exécutable tiers en administrateur (il en a besoin pour PawnIO) : exception assumée à la règle « passer par explorer.exe », limitée au binaire installé par le MSI officiel sous Program Files, justifiée en commentaire.
- Deux logiciels sur le même contrôleur : clignotements, états invalides. ARM64 : pas de build OpenRGB. Anti-triche face au pilote PawnIO : non vérifié.
- Le dépôt n'a pas de LICENSE. RGB.NET (LGPL-2.1, DLL à garder remplaçable, gênant pour une publication single-file) : ne l'ajoute pas.
- Theme.xaml ≈ 1513 lignes : nouveau ResourceDictionary. Rien de lent dans le constructeur du ViewModel.

## 6. Questions à me poser
1. Licence de PCPerfSuite (fermée, MIT, GPL…) ? (seulement si docs/decisions.md ne la tranche pas)
2. Dépendance à OpenRGB installé à part acceptée ? Sinon, Dynamic Lighting seul (#24).
3. Appareils SMBus (RAM, carte mère) : jamais, opt-in expérimental (recommandé) ou par défaut ?
4. La règle 5 couvre-t-elle le rétroéclairage des claviers de portables (recommandé : oui) ? (seulement si docs/decisions.md ne la tranche pas)
5. Logiciel constructeur détecté : s'effacer en lecture seule (recommandé) ou proposer de l'arrêter ?
6. OpenRGB : le démarrer et l'arrêter soi-même, ou seulement s'y connecter ?
7. Quel matériel de test as-tu (RAM RGB, ventilateurs ARGB, périphériques) ?
8. RGB exclu du mode technicien (recommandé : oui, jamais de scan SMBus chez un client) ? (seulement si docs/decisions.md ne la tranche pas)

## 7. Périmètre et hors périmètre
Hors périmètre : Dynamic Lighting, effets et page complète (#24 Éclairage RGB : Dynamic Lighting, effets et page Éclairage) ; SDK constructeurs et RGB.NET ; éclairage en arrière-plan par identité de paquet ; catalogue d'outils (#7 Boîte à outils : liens de téléchargement direct) ; mode technicien (#13 Mode technicien et rédaction IA du compte rendu).

## 8. Livrables et méthode
- Branche feature/rgb-openrgb depuis develop ; commits en français au présent, comme l'historique.
- Tests xUnit : StableKey, tri HID/SMBus sur portable, détecteur sur une liste de processus fictive, choix de l'asset dans un JSON Codeberg d'exemple (sans réseau), statuts. dotnet build -c Release et dotnet test verts.
- Rubrique « Éclairage RGB » dans Paramètres › Compatibilité de ce PC, ajoutée par un fournisseur de lignes (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel : fournisseurs, version d'OpenRGB et protocole négocié, appareils par bus, logiciels en conflit, raison de chaque absence.
- Section du README ; « expérimental » (règle 6) pour chaque couple fournisseur + famille d'appareils non vérifié.
- `/review-max` avant de fusionner dans develop ; jamais sur master.

## 9. À vérifier sur une vraie machine
Sur ma machine (TUF B760-PLUS WIFI Aura, i5-14600K, RTX 5070 Ti ; tes tests tournent sur un i5-13500T) :
- Le test SMBus préalable (section 2).
- Installation d'OpenRGB : contrôle réussi ou refus expliqué.
- Statut Ready, carte mère et périphériques listés, couleur fixe appliquée.
- Armoury Crate lancé : appareils ASUS « gérés par Armoury Crate », aucune écriture.
- OpenRGB fermé en cours de route : NotRunning, pas de plantage, reconnexion.
````

### 24. Éclairage RGB : Dynamic Lighting, effets et page Éclairage

- Modèle / effort : **Opus 5.5 / high** · taille M · relecture /review
- Dépend de : #23 ; #8 conseillé
- Branche : `feature/rgb-effets`
- Matériel pour vérifier : un périphérique compatible Dynamic Lighting (Windows 11 22621.2361+, en pratique 23H2) ; la TUF B760-PLUS WIFI de Denis est annoncée compatible (FAQ ASUS)

````text
# Éclairage RGB : Dynamic Lighting, effets et page Éclairage (F8, partie 2)

## 1. Objectif
Ma demande : « Ajouter la gestion des appareils RGB (ventilateurs et périphériques). » Partie 2, sur le socle de #23 : Windows Dynamic Lighting comme second fournisseur, un moteur d'effets unique et la page Éclairage complète.

## 2. Avant de coder
- Lis docs/decisions.md (décisions transverses et briques partagées, créé par #1) : ne me repose pas une question qui y est tranchée, réutilise chaque brique listée sous son nom et à son emplacement, et mets à jour la ligne d'une brique que tu crées.
- Lis les sections « ### F8 » de docs/etudes/etude-faisabilite-2026-09.md et docs/etudes/cartographie-code-2026-09.md, si présentes (Grep, pas en entier).
- Pose-moi les questions de la section 6 avec AskUserQuestion, une décision par question, ta recommandation en premier ; la question 1 (TFM, valable pour toute l'app), seulement si docs/decisions.md ne la tranche pas, avant de toucher aux .csproj.

## 3. Existant à réutiliser
- Socle d'éclairage de #23 (src/PCPerfSuite.Core/Hardware/Lighting/) : ILightingProvider, LightingProviderStatus, LightingDevice, LightingEffect minimal, fournisseur OpenRGB, détecteur de logiciels constructeur, page Éclairage minimale.
- Briques de #1 : PageKeys et IPageLifecycle (page Éclairage), ICompatibilityRowProvider (fournisseur de lignes de la rubrique « Éclairage RGB », posé par #23), AppDataPaths (réglages d'effets).
- ColorPicker (src/PCPerfSuite.App/Controls/, ColorHex transmis au relâchement), pastilles d'OverlayPalette et OverlayAppearanceViewModel, AdaptiveGrid.
- FanTempSource (CpuPackage, GpuCore, MotherboardSystem, HottestOfCpuGpu), MonitoringViewModel.SnapshotUpdated, IBackgroundSensorConsumer et MainViewModel.UpdateEcoMode (liste fixe de consommateurs à compléter).
- ms-settings s'ouvre comme dans WindowsPerformanceSettingsService (ExternalLink.TryOpen refuse tout sauf HTTPS).
- ProfileGroup et le bail de réglage TuningLease de #8 (Grep ; s'ils manquent, dis-le-moi).

## 4. Approche retenue
TFM tel que tranché dans docs/decisions.md. S'il ne l'est pas, recommandé : assemblage PCPerfSuite.Lighting.WinRT en net8.0-windows10.0.22621.0 (CsWinRT via Microsoft.Windows.SDK.NET.Ref), SupportedOSPlatformVersion 10.0.19041.0, gardes ApiInformation, qui implémente ILightingProvider et se charge à la demande. Un projet net8.0-windows ne peut sans doute pas le référencer (vérifie) : chargement dynamique avec copie de la DLL en sortie et en publication, sinon App et tests au TFM WinRT. Mesure la taille de Microsoft.Windows.SDK.NET.dll.

Dynamic Lighting (LampArray)
- DeviceWatcher sur LampArray.GetDeviceSelector(), LampArray.FromIdAsync ; IsAvailable, AvailabilityChanged, LampArrayKind, LampCount, HardwareVendorId/ProductId ; SetColor, SetColorsForIndices.
- Sans identité de paquet, Windows n'applique les couleurs que tant que l'app a le focus. V1 : inventaire, « Identifier » (clignotement pendant le focus), lien ms-settings:personalization-lighting.
- Dédoublonnage VID/PID avec OpenRGB : préférer Dynamic Lighting.
- Windows 11 22621.2361+ (KB5030509, en pratique 23H2) ; avant, Windows 10 compris : UnsupportedOs avec message (règle 3).

Moteur d'effets unique (LightingEffect sérialisable, en chaînes ; réglages enregistrés sous AppDataPaths, #1)
- Éteint, Couleur fixe, Respiration : mode matériel natif de préférence (UpdateMode sur Static/Breathing : zéro CPU, survit à la fermeture).
- Couleur selon température : FanTempSource, 2-3 paliers avec hystérésis, au plus 1 écriture/s et seulement si la couleur change au-delà d'un seuil.
- Mode Direct logiciel 20-30 Hz : USB/HID seulement, jamais SMBus.
- Quitter : Release, aucune écriture.
- Jamais d'écriture pendant un bail TuningLease tenu par un autre (#8) ; le statut de l'appareil le dit.

Page Éclairage (PageKeys, IPageLifecycle de #1) : grille d'appareils, effet par appareil ou zone (ColorPicker), statut et raison ; styles dans un nouveau ResourceDictionary. Groupes : dimension facultative éclairage dans ProfileGroup (#8), null = ne pas toucher, lecture tolérante des anciens réglages.

## 5. Pièges connus
- LampArray depuis un processus élevé (requireAdministrator) : non vérifié.
- Fenêtre cachée : seul OpenRGB garde les couleurs ; le moteur doit être un IBackgroundSensorConsumer pour ses capteurs en mode éco.
- Dynamic Lighting ne rapporte pas les couleurs courantes ; ses priorités sont liées au port USB.
- Sur la TUF, Dynamic Lighting passe par Armoury Crate ou le BIOS, or #23 passe les appareils ASUS en lecture seule si Armoury Crate tourne : ne masque pas la voie Dynamic Lighting (à vérifier).

## 6. Questions à me poser
1. TFM : assemblage WinRT isolé chargé à la demande (recommandé) ou toute l'app en net8.0-windows10.0.22621.0 ? (seulement si docs/decisions.md ne la tranche pas)
2. Couleur selon température quand l'app est dans la zone de notification (OpenRGB seulement) ?
3. Effets V1 : éteint, fixe, respiration, température, ou d'autres ?
4. Intégration aux groupes de profils (#8) dès maintenant ?

## 7. Périmètre et hors périmètre
Hors périmètre : éclairage en arrière-plan « ambient » (identité de paquet + certificat de production) ; SDK constructeurs et RGB.NET ; OpenRGB lui-même (#23 Éclairage RGB : socle et OpenRGB) ; les groupes eux-mêmes (#8 Groupes de profils CPU + GPU + ventilation).

## 8. Livrables et méthode
- Branche feature/rgb-effets depuis develop ; commits en français au présent, comme l'historique.
- Tests xUnit, dans Core sans WinRT : paliers, hystérésis et limite d'une écriture par seconde, dédoublonnage, sérialisation, jamais de Direct sur SMBus, aucune écriture pendant un bail TuningLease tenu par un autre. dotnet build -c Release et dotnet test verts.
- Ligne Dynamic Lighting dans la rubrique « Éclairage RGB » de Paramètres › Compatibilité de ce PC, ajoutée par le fournisseur de lignes de la rubrique (ICompatibilityRowProvider, #1), jamais par une nouvelle méthode ni une nouvelle dépendance de CompatibilityViewModel ; section du README ; « expérimental » (règle 6) pour ce qui n'est pas vérifié sur une vraie machine.
- `/review` avant de fusionner dans develop ; jamais sur master.

## 9. À vérifier sur une vraie machine
Sur ma TUF B760-PLUS WIFI (compatible selon la FAQ ASUS) ou un autre périphérique Dynamic Lighting, Windows 11 22631 ; tes tests tournent sur un i5-13500T :
- Inventaire sans doublon avec OpenRGB.
- « Identifier » clignote avec le focus, s'arrête sans ; fonctionne malgré l'élévation.
- Couleur selon température GPU en jeu : au plus un changement par seconde, sans scintillement.
- Quitter : aucune écriture, mode matériel conservé.
````
