# PCPerfSuite

App Windows (WPF, .NET 8) de monitoring et de tuning PC. Construite pour ta config
(i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI) mais faite pour rester compatible
avec la plupart des configs Intel/AMD + NVIDIA/AMD/Intel.

## Ce que fait l'app

- **Monitoring** — dashboard temps réel plus complet et plus lisible que le Gestionnaire
  des tâches : CPU (charge, température, puissance, fréquence), GPU (charge, températures
  cœur + hot spot, horloges cœur/mémoire, VRAM, ventilo), RAM, carte mère, réseau, disques
  (débits, température, santé SMART) et la liste de tous les ventilateurs détectés avec
  RPM + %. La tuile « Mes métriques » se compose librement dans le catalogue de métriques,
  et la cadence de rafraîchissement se saisit librement en millisecondes en haut de l'onglet
  (de 100 à 60000 ms). **Un clic gauche sur une courbe** épingle un repère qui affiche la valeur
  exacte du relevé visé et son ancienneté (glisser pour le déplacer, clic droit ou Échap pour
  l'enlever) : le repère reste accroché à *son* relevé pendant que la courbe défile, il ne désigne
  pas un endroit de l'écran.
- **Processus** — un gestionnaire de tâches pensé pour rester *cliquable*. Le défaut de celui
  de Windows, c'est que les lignes sautent dès qu'on trie par CPU : ici le classement suit une
  moyenne lissée sur quelques secondes (la colonne, elle, montre bien la valeur instantanée), il
  n'est recalculé qu'à intervalle lent, et il **se fige complètement dès que la souris entre dans
  la liste** — la ligne visée ne se dérobe donc jamais sous le curseur. Recherche instantanée
  (nom, PID, éditeur, chemin, titre de fenêtre, insensible aux accents), filtres Applications /
  Arrière-plan / Windows, colonnes au choix, sélection multiple pour terminer plusieurs processus
  d'un coup, mise en évidence des gros consommateurs, et un panneau de détail avec l'historique
  CPU et mémoire du processus sélectionné. Les processus critiques pour Windows sont affichés mais
  leur arrêt est refusé, avec l'explication.
- **Nettoyage** — cache shaders NVIDIA/AMD/Intel, cache shaders DirectX (D3DSCache), cache
  Steam, fichiers temporaires (%TEMP% et système), Prefetch, cache Windows Update, rapports
  d'erreurs Windows, cache des miniatures, cache Delivery Optimization, + un bouton "Vider
  la corbeille". Chaque catégorie affiche la taille réelle occupée et propose "Ouvrir le
  dossier" et "Nettoyer".
- **Stockage** — carte proportionnelle (treemap) de ce qui occupe un disque, avec menu
  contextuel (ouvrir, explorer, supprimer) sur chaque bloc.
- **Optimisation Windows** — les réglages de performance de Windows, y compris ceux qui
  sont masqués : plan d'alimentation
  "Performances ultimes" (cette app le débloque et l'active), planification GPU accélérée
  par le matériel (HAGS), Mode Jeu, effets visuels, power throttling, limitation réseau
  multimédia, démarrage rapide, suspension sélective USB, core parking CPU, ASPM PCIe, et
  un indicateur pour l'isolation du noyau (HVCI).
- **Paramètres** (en bas de la barre latérale) — les réglages de PCPerfSuite lui-même, par
  onglets : Général (zone de notification), Compatibilité de ce PC (voir plus bas) et Thèmes
  (à venir).
- **Ventilateurs** — **tous** les ventilateurs pilotables au même endroit : ceux de la carte
  mère (via les capteurs de contrôle que LibreHardwareMonitor sait écrire sur ton Super I/O) **et
  celui du GPU** (NVAPI, ADLX ou IGCL selon la marque). Modes Auto / Manuel / Courbe, presets, et par ventilateur : source de
  température (CPU, GPU, la plus chaude des deux, carte mère), hystérésis, vitesses mini/maxi et
  arrêt complet à froid. Détail plus bas.
- **GPU** — overclocking NVIDIA, AMD Radeon et Intel Arc : décalage d'horloge cœur et
  mémoire, limite de puissance, limite de température, tension quand la carte l'accepte,
  profils enregistrés et affichage de ce qui bride la carte en direct. Détail plus bas.
- **Processeur** — deux étages : les réglages d'alimentation Windows (mode boost, fréquence max,
  EPP), qui marchent sur Intel, AMD *et* Snapdragon sans pilote ; et la limite de puissance en watts
  (PL1/PL2 sur Intel, PPT/STAPM sur AMD), avec relecture systématique de ce que le processeur a
  réellement retenu et sécurité thermique. Détail plus bas.
- **Overlay** — métriques affichées par-dessus les jeux, via RTSS et/ou une fenêtre
  transparente dessinée par l'app, avec police, taille, couleurs et position réglables.
  Détail plus bas.
- **Zone de notification** — la croix ne quitte pas l'app, elle la range près de l'horloge :
  le monitoring, les courbes de ventilation et l'overlay continuent de tourner pendant que tu
  joues. Clic sur l'icône pour rouvrir la fenêtre, clic droit → « Quitter » pour fermer
  vraiment. Décochable dans Paramètres › Général si tu préfères que la croix ferme l'app.

## Overlay en jeu

Deux canaux, cumulables, qui affichent exactement les mêmes lignes :

1. **RTSS** (RivaTuner Statistics Server, le moteur d'overlay de MSI Afterburner).
   PCPerfSuite écrit le texte dans la mémoire partagée `RTSSSharedMemoryV2` que RTSS relit
   et dessine lui-même à l'intérieur du jeu — aucune injection de notre côté. C'est le seul
   canal qui fonctionne en **plein écran exclusif**. RTSS doit être installé et lancé
   (gratuit, guru3d.com). Les couleurs et la taille du texte lui sont transmises via ses
   balises de mise en forme (`<C=AARRGGBB>`, `<S=nnn>`) ; la police, elle, reste celle
   configurée dans RTSS. Si une version trop ancienne de RTSS affichait les balises en
   clair, l'envoi des couleurs se désactive d'un interrupteur.
2. **Fenêtre PCPerfSuite** : une fenêtre transparente, sans bordure, toujours au-dessus et
   traversante pour la souris (`WS_EX_TRANSPARENT`/`WS_EX_NOACTIVATE`/`WS_EX_TOOLWINDOW`).
   Rien à installer, police/taille/couleurs/position/opacité entièrement libres, mais elle
   n'apparaît **pas** en plein écran exclusif (limite de Windows) — en fenêtré ou sans
   bordure, c'est-à-dire la grande majorité des jeux récents, elle fonctionne.

Réglages disponibles : métriques affichées (même catalogue que le Monitoring), une ligne
par métrique ou une ligne par catégorie façon Afterburner, **l'ordre des lignes** (flèches
monter / descendre dans l'onglet, enregistré séparément pour chacun des deux modes),
**cadence propre à l'overlay**
(indépendante de celle du Monitoring, sans pouvoir aller plus vite que lui puisque les
valeurs en viennent), police, taille, **une couleur par catégorie** (CPU, GPU, RAM, NET…)
pour repérer chaque ligne d'un coup d'œil, couleur des valeurs, position sur l'écran
(grille 3×3 + marges) et opacité du fond. L'aperçu de l'onglet rend exactement ce que
l'overlay affichera.

Côté jeu, en plus du FPS instantané et du temps de frame, le catalogue propose le **FPS moyen**,
le **1% low** et le **0.1% low**, calculés à partir de l'historique des 1024 derniers temps de frame
que RTSS tient à jour — la même matière première que les outils de benchmark. Un centile n'est
affiché qu'avec assez d'images derrière lui (100 pour le 1%, 1000 pour le 0.1%) : sinon la valeur
reste à `--` plutôt que d'annoncer un chiffre inventé.

Sur la ligne JEU, chaque valeur de FPS est précédée de son libellé — `FPS`, `MOY`, `1%`, `0.1%` —
qui tient lieu d'unité (« JEU  FPS 144  MOY 138  1% 95  0.1% 80  6.9 ms ») ; le temps de frame
garde son unité, sans libellé.

## Overclocking GPU

Chaque marque passe par l'API officielle livrée avec son pilote, sans pilote ni service
supplémentaire. L'app essaie NVIDIA, puis AMD, puis Intel et garde la première carte
pilotable : dans un PC avec un GPU intégré et une carte dédiée, c'est la carte dédiée qui
est réglée (et c'est aussi elle que suivent les relevés).

| Marque | API | Cartes couvertes |
|---|---|---|
| NVIDIA | NVAPI (NvAPIWrapper.Net) | Kepler → cartes actuelles |
| AMD Radeon | ADLX (`amdadlx64.dll`, pilote Adrenalin) | RDNA 1 → 4 (RX 5000 → RX 9000) |
| Intel Arc | IGCL (`ControlLib.dll`, pilote Arc) | Arc A (Alchemist), Arc B (Battlemage) |

Les GPU intégrés (Intel UHD/Iris, Radeon des processeurs AMD), les Radeon GCN/Vega et les
autres marques (Moore Threads, Qualcomm Adreno…) n'exposent pas de réglages d'overclocking :
l'onglet l'explique au lieu d'afficher des curseurs sans effet.

- **Horloges cœur et mémoire** : un décalage par rapport à la valeur d'usine, borné par ce
  que le pilote annonce. Chez NVIDIA, offset sur l'état P0 via P-States 2.0
  (`NvAPI_GPU_Get/SetPstates20`), exactement comme les curseurs de MSI Afterburner. Chez AMD,
  fréquence max d'Adrenalin (absolue jusqu'à RDNA 3, en décalage depuis RDNA 4) ramenée à un
  décalage. Chez Intel, décalage de fréquence et vitesse mémoire (en MT/s ou Mbps selon la
  génération d'Arc).
- **Limite de puissance** en % de la valeur d'usine, quelle que soit l'unité du pilote.
- **Limite de température** en °C (NVIDIA et Intel ; AMD ne la propose pas au réglage).
- **Tension** : surtension en % sur les GPU Pascal (GTX 10xx), tension ou décalage en mV
  chez AMD et Intel. Le curseur n'apparaît que si la carte répond réellement.
- **Intel** : le pilote exige une renonciation de garantie avant tout overclock. L'onglet la
  présente une fois et mémorise ton accord ; sans lui, les curseurs restent grisés.
- **Profils** : les réglages courants s'enregistrent sous un nom et se rappellent en un clic
  (un profil du même nom est remplacé). Une tension enregistrée n'est appliquée qu'à une carte
  qui raisonne dans la même unité, et l'overclock n'est pas réappliqué au démarrage après un
  changement de marque de carte.
- **Ce qui bride la carte** en direct (puissance, température, tension, pas de charge), comme la
  ligne « perf cap » de GPU-Z — fourni par NVIDIA seulement, « N/D » ailleurs.
- Ce que la carte n'expose pas est listé sous les curseurs avec la raison (« N/D sur cette
  carte, non exposé par le pilote… »), par exemple la limite de puissance des GPU portables,
  fixée par le constructeur du PC.
- Après chaque application, l'onglet affiche **ce que le pilote a réellement retenu** : il rabote
  une demande hors plage sans prévenir, ce qui explique un overclock « qui ne monte pas ».

Le ventilateur du GPU n'est pas dans cet onglet : il est dans **Ventilateurs** avec tous les autres
(avec passage forcé à 100 % au-delà de 88 °C tant que l'app le pilote).

## Processeur

L'onglet **Processeur** a deux étages.

### Réglages d'alimentation (Intel, AMD et Snapdragon)

Les réglages processeur du plan d'alimentation Windows actif : mode boost, état minimal et maximal,
fréquence maximale en MHz, et arbitrage performance/économie (EPP). Sans pilote, sans risque, et avec
une valeur « sur secteur » et une valeur « sur batterie » sur les portables. Sur les processeurs
hybrides (Intel 12e génération et plus, Snapdragon X), les réglages propres aux cœurs rapides
apparaissent en plus.

Tout passe par l'API `powrprof` et non par `powercfg.exe` : la moitié de ces réglages sont masqués
par défaut et n'apparaissent pas dans la sortie de `powercfg`, dont le texte est en plus traduit dans
la langue de Windows, donc impossible à analyser de façon fiable. Chaque modification est relue : si
Windows retient autre chose, l'onglet l'affiche.

C'est le seul étage disponible sur Snapdragon, où le reste est verrouillé par le firmware.

### Limite de puissance en watts (Intel et AMD)

Le second étage règle la puissance que le CPU a le droit de consommer, en watts. C'est le réglage qui
change le plus le comportement d'un PC : l'abaisser fait chuter température, bruit et consommation
pour une perte de performance souvent minime (utile sur un portable), le relever laisse le processeur
tenir ses fréquences plus longtemps quand le refroidissement suit.

- **Intel** : PL1 (limite soutenue) et PL2 (limite de pointe), via le registre `MSR_PKG_POWER_LIMIT`
  — le même que règle le BIOS. Si le BIOS a posé son verrou (bit 63), l'onglet le dit et n'écrit
  rien : seul un redémarrage peut le lever, et encore, si le BIOS ne le repose pas.
- **AMD** : PPT sur les processeurs de bureau (Ryzen 3000 à 9000), et STAPM + limites lente/rapide
  sur les portables (Ryzen 4000 à 8040), via la SMU — le même chemin que Ryzen Master.
- **Snapdragon** : impossible. La fréquence, la tension et les limites de puissance sont verrouillées
  par le firmware Qualcomm et aucune interface publique ne permet d'y toucher. L'onglet l'affiche en
  « N/D » avec cette explication plutôt que de laisser croire à une panne.

Garde-fous :

- Les valeurs sont bornées : jamais moins de 5 W, jamais plus de 1,5 fois la limite d'usine.
- **Chaque écriture est relue.** Un MSR comme un SMU peut accepter une consigne et n'en rien faire :
  si le firmware impose la sienne, l'onglet affiche la valeur réellement retenue au lieu d'annoncer
  un succès en l'air.
- Un avertissement est à accepter une fois avant le premier réglage.
- **Sécurité thermique** : si le processeur reste à 98 °C pendant 15 secondes avec une limite
  relevée, l'app rétablit d'elle-même les limites d'origine.
- Rien n'est appliqué au lancement et tout repart d'origine en quittant, sauf si tu coches
  « Appliquer au démarrage ». De toute façon, **les limites ne survivent pas à un redémarrage** :
  le firmware les repose à chaque démarrage, ce qui fait du bouton d'arrêt le filet de sécurité
  ultime.

Cet étage a besoin du pilote **PawnIO** (voir « Points d'attention ») : sans lui, l'onglet affiche
« N/D » et propose de l'installer.

## Courbes de ventilation

Chaque ventilateur pilotable a sa carte, carte mère comme GPU :

- **Modes** Auto (firmware) / Manuel / Courbe, et presets Silencieux / Équilibré / Perf.
- **Éditeur de courbe** : on glisse un point dans les deux axes, on en ajoute un au double-clic,
  on en retire un au clic droit (de 2 à 12 points).
- **Source de température** par ventilateur : CPU, GPU, la plus chaude des deux (le bon choix pour
  un ventilateur de boîtier) ou la carte mère.
- **Hystérésis** en °C : le ventilateur ne ralentit qu'une fois la température retombée d'autant,
  ce qui l'empêche de « pomper » autour d'un point de la courbe.
- **Vitesses mini et maxi**, pour un ventilateur qui cale trop bas ou qu'on ne veut jamais entendre
  à fond.
- **Arrêt complet à froid (0 RPM)** sous une température au choix — désactivé par défaut, tous les
  ventilateurs ne redémarrant pas proprement.
- **Appliquer à tous** recopie une courbe et ses réglages sur les autres ventilateurs.

Sécurité : par défaut **rien n'est réappliqué au démarrage et tout est rendu au pilote en
quittant**. La case « Appliquer au démarrage » rend l'overclock persistant dans les deux
sens (réappliqué au lancement, conservé à la fermeture). Un décalage trop ambitieux fige
l'écran ou fait planter le pilote sans rien casser : un redémarrage remet tout d'origine,
et l'overclock n'est pas réappliqué tant que cette case est décochée. Monte par paliers de
15 à 25 MHz et teste entre chaque. Chaque réglage refusé par le pilote est signalé dans
l'onglet plutôt que d'échouer en silence.

## Compatibilité et capteurs non disponibles

Tous les PC n'exposent pas les mêmes capteurs : cela dépend du CPU, du GPU, de la carte mère,
des pilotes et, sur un portable, de la marque. L'app le dit toujours clairement :

- `--` : valeur pas encore lue (juste après le lancement) ;
- `N/D` : **ce PC ne fournit pas cette valeur**. La tuile du Monitoring explique pourquoi, et le
  choix des métriques (Monitoring et overlay) signale « Non disponible sur ce PC ». Exemple : la
  température mémoire (junction) n'existe que sur les GPU NVIDIA en GDDR6X.
- **Paramètres › Compatibilité de ce PC** résume ce qui est lu et pilotable sur la machine, et
  pourquoi le reste manque. « Copier le rapport » en fait un texte à joindre à un signalement.

**Ventilateurs des portables** : ils sont gérés par le contrôleur embarqué du constructeur, que
LibreHardwareMonitor ne voit pas. L'app les lit (en lecture seule, jamais pilotés) via l'interface
WMI de la marque, comme son utilitaire officiel :

| Marque | Interface | État |
|---|---|---|
| ASUS | `AsusAtkWmi_WMNB` (Armoury Crate) | vérifié, RPM et % |
| Lenovo Legion / LOQ | `LENOVO_FAN_METHOD` (Legion Zone) | expérimental, RPM et % |
| HP Omen / Victus | `hpqBIntM` (Omen Gaming Hub) | expérimental, RPM |
| MSI | `MSI_ACPI` (MSI Center) | expérimental, RPM |
| Acer Predator / Nitro | `AcerGamingFunction` (PredatorSense) | expérimental, RPM |

« Expérimental » : protocole repris des pilotes Linux et des outils open source de référence, mais
pas encore confirmé sur une vraie machine. Les autres marques de portables sont signalées « non
prises en charge ».

Le **contrôle GPU** (onglet GPU) ne fonctionne qu'avec NVIDIA pour l'instant ; le monitoring GPU,
lui, couvre NVIDIA, AMD et Intel.

## Points d'attention importants

- **L'app doit tourner en administrateur.** Le manifeste (`app.manifest`) le demande déjà
  automatiquement au lancement — Windows affichera l'invite UAC. Sans ça, la plupart des
  capteurs et tous les réglages système resteront inaccessibles (l'app te le signale dans
  l'interface plutôt que de planter).
- **Pilote PawnIO** : les capteurs bas niveau et la limite de puissance du processeur passent
  par [PawnIO](https://pawnio.eu/), un pilote signé et à jour. Il remplace WinRing0, que Windows
  Defender signale depuis 2025 comme pilote vulnérable (CVE-2020-14979) et que la liste de blocage
  des pilotes refuse de charger. PawnIO n'ouvre pas un accès brut au matériel : il exécute des
  modules signés qui décident eux-mêmes de ce qu'ils autorisent, et un accès refusé est affiché
  comme tel par l'app. Prends simplement la dernière version proposée par son site (testé avec la
  2.2.0). L'onglet Processeur affiche la version installée, et entre parenthèses la version de
  l'interface de programmation, qui est celle que renvoie le pilote lui-même.
- **Isolation du noyau / Intégrité de la mémoire (HVCI ou "Memory Integrity")** : cette option
  n'empêche pas PawnIO de fonctionner (contrairement à WinRing0), mais l'app affiche quand même
  l'état du réglage dans l'onglet Optimisation Windows, avec un lien direct vers le réglage Windows.
- **Fermer la fenêtre n'arrête pas l'app** (voir « Zone de notification » plus haut). Tant
  qu'elle tourne, les ventilateurs pilotés par une courbe restent sous son contrôle : c'est
  « Quitter » depuis l'icône qui les repasse en automatique et rend la carte au pilote.
- Les réglages sont stockés dans
  `%LOCALAPPDATA%\PCPerfSuite\settings.json` (lisible et modifiable à la main).

## Compiler et lancer

Prérequis : SDK .NET 8.

```
cd PCPerfSuite
dotnet restore
dotnet build -c Release
```

Puis lance `src\PCPerfSuite.App\bin\Release\net8.0-windows\PCPerfSuite.exe` (clic droit →
Exécuter en tant qu'administrateur si l'invite UAC ne se déclenche pas automatiquement), ou
ouvre `PCPerfSuite.sln` dans Visual Studio et F5.

La solution compile sans erreur (C# et XAML), mais elle n'a pas pu être *exécutée* sur une
vraie machine Windows depuis cette session : le comportement des appels NVAPI, ADLX et IGCL
dépend de ta carte et de ton pilote. Si un réglage ne prend pas, l'onglet GPU affiche le refus — copie
le message, on ajuste.
