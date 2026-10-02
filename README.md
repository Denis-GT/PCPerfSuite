# PCPerfSuite

App Windows (WPF, .NET 8) de monitoring et de tuning PC, pour les PC de bureau comme pour les
portables, de toutes marques : processeur Intel ou AMD, carte graphique NVIDIA, AMD ou Intel.
Ce qui dépend du matériel est détecté au lancement ; une fonction absente sur un PC le dit, avec
la raison (voir « Compatibilité et capteurs non disponibles »).

## Organisation de l'app

La barre latérale range les pages en quatre sections. Les pages marquées *(bientôt disponible)*
sont déjà dans le menu, en grisé : elles ouvrent une page qui dit ce qu'elles feront.

| Section | Pages |
|---|---|
| **Surveiller** | Monitoring, Processus, Overlay |
| **Régler** | Processeur, GPU, Ventilateurs, Profils (Groupes, Automatique), OC automatique *(bientôt disponible)*, Écrans *(bientôt disponible)*, Éclairage *(bientôt disponible)*, GPU portable *(bientôt disponible, absente des PC de bureau)* |
| **Diagnostiquer** | Bench et diagnostic *(bientôt disponible)* |
| **Outils** | Optimisation Windows, Nettoyage, Stockage, Périphériques *(bientôt disponible)*, Boîte à outils, Mémoire *(bientôt disponible)* |

**Paramètres** est en bas de la barre. Une page qui grossit reçoit des sous-onglets en pastilles
(comme Paramètres : Général, Installations, Compatibilité de ce PC, Thèmes, ou Profils : Groupes, Automatique) plutôt qu'une entrée de
plus. Les pages de gestion (Optimisation Windows, Nettoyage, Stockage, Boîte à outils, Profils, et le
diagnostic de Paramètres) ne lisent leurs données qu'à leur première ouverture, et le relevé des processus ne
tourne que tant que sa page est affichée : l'app démarre plus vite, même lancée dans la zone de
notification. Le relevé des capteurs (Monitoring, overlay, ventilateurs, limites) tourne, lui, dès
le lancement.

Les fichiers de l'app sont réunis dans `%LOCALAPPDATA%\PCPerfSuite` : `settings.json`
(réglages), `erreurs.log` (erreurs inattendues), `diagnostic-ventilateurs.log` (état de la puce
des ventilateurs au démarrage et à la fermeture), le témoin du contrôle GPU AMD
(`adlx-plantage.temoin`), le journal de session (`journal-session.jsonl` : opérations risquées en
cours, reprises au lancement suivant, gardées 30 jours) et, une fois la bascule automatique activée,
son historique (`usage.json` : temps et températures par usage, applications vues, journal des
bascules, 30 jours, jamais transmis). Ce que la Boîte à outils télécharge passe par
`%ProgramData%\PCPerfSuite`, que seuls les administrateurs peuvent modifier : `Installations\` pour un
fichier en cours de vérification, `Tools\` pour les outils portables, et, pour tous les comptes du PC,
le dernier catalogue en ligne accepté (`catalogue-outils.json` et sa signature) avec le plus haut numéro
accepté (`catalogue-outils.plancher`). Lancée sans droits administrateur, l'app se replie sur
`%TEMP%\PCPerfSuite` pour ses installeurs.

Pour les développeurs : l'emplacement de chaque fonction à venir et la recette pour ajouter une
page sont dans `docs/navigation.md` ; les décisions transverses et les briques partagées dans
`docs/decisions.md`.

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
- **Processus** — un gestionnaire de tâches qui reprend les mesures du Gestionnaire des tâches
  de Windows : mémoire en jeu de travail privé, %CPU sur environ une seconde et pondéré par la
  fréquence réelle des cœurs, disque et réseau par processus (trace ETW du noyau, en
  administrateur ; sans elle, « Disque » retombe sur le compteur d'E/S de Windows et « Réseau »
  affiche N/D, avec la raison en infobulle). Les processus d'une même application sont regroupés
  **par arbre de processus**, groupes repliés au départ, comme dans le Gestionnaire. Le
  classement suit **chaque relevé**, sur les valeurs affichées, par déplacements de lignes : la
  sélection et le défilement ne sont pas perdus (« Figer » arrête tout pour lire tranquillement).
  Le fond des cellules CPU, Mémoire, Disque et Réseau est d'autant plus soutenu que la part du
  total du PC est grande (échelle absolue). Recherche instantanée (nom, PID, éditeur, chemin, titre
  de fenêtre, insensible aux accents), filtres Applications / Arrière-plan / Windows, colonnes au
  choix, sélection multiple pour terminer plusieurs processus d'un coup, et un panneau de détail
  avec l'historique CPU et mémoire du processus sélectionné. Les processus critiques pour Windows
  sont affichés mais leur arrêt est refusé, avec l'explication.
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
  par le matériel (HAGS), Mode Jeu, power throttling, limitation réseau
  multimédia, démarrage rapide, suspension sélective USB, core parking CPU, ASPM PCIe, et
  un indicateur pour l'isolation du noyau (HVCI). Le sous-onglet **Animations** porte la carte
  « Animations et effets » *(expérimental)* : un interrupteur par effet (interrupteur général des
  effets, « Effets d'animation » de Windows, réduction des fenêtres, barre des tâches, menus,
  info-bulles, listes, défilement fluide, contenu pendant le déplacement, ombres, transparence),
  le préréglage « Réactif » (tout coupé en un passage, sauf le lissage des polices et
  l'interrupteur général) et « Rétablir mes réglages d'origine ». Tout passe par
  SystemParametersInfo, comme le fait Windows, sauf la barre des tâches et la transparence
  (registre du profil, peut-être seulement à la prochaine ouverture de session). L'app retient la
  valeur d'origine de chaque effet avant d'y toucher ; ces réglages restent en place quand on
  quitte l'app. Le gain porte sur la réactivité du bureau, pas sur les FPS. PCPerfSuite suit
  lui-même « Effets d'animation » : coupé, ses volets s'ouvrent sans animation. Lancée en
  administrateur sous un autre compte que celui devant l'écran, l'app grise cette carte et le
  Mode Jeu, qui régleraient le profil de l'administrateur.
- **Paramètres** (en bas de la barre latérale) — les réglages de PCPerfSuite lui-même, par
  onglets : Général (zone de notification, lancement au démarrage de Windows), Installations
  (voir plus bas), Compatibilité de ce PC (voir plus bas) et Thèmes (à venir).
- **Ventilateurs** — **tous** les ventilateurs pilotables au même endroit, rangés par catégorie
  (processeur, pompe, carte graphique, boîtier…) sur deux colonnes : ceux de la carte
  mère (via les capteurs de contrôle que LibreHardwareMonitor sait écrire sur ton Super I/O) **et
  ceux du GPU** (NVAPI, ADLX ou IGCL selon la marque). Modes Auto / Manuel / Courbe, presets, et par ventilateur : source de
  température (CPU, GPU, la plus chaude des deux, carte mère), hystérésis, accélération et
  décélération, vitesses mini/maxi et arrêt complet à froid. Détail plus bas.
- **GPU** — overclocking NVIDIA, AMD Radeon et Intel Arc : décalage d'horloge cœur et
  mémoire, limite de puissance, limite de température, tension quand la carte l'accepte,
  profils enregistrés et affichage de ce qui bride la carte en direct. Détail plus bas.
- **Processeur** — deux étages : les réglages d'alimentation Windows (mode boost, fréquence max,
  EPP), qui marchent sur Intel, AMD *et* Snapdragon sans pilote ; et la limite de puissance en watts
  (PL1/PL2 sur Intel, PPT/STAPM sur AMD), avec relecture systématique de ce que le processeur a
  réellement retenu et sécurité thermique. Détail plus bas.
- **Overlay** — métriques affichées par-dessus les jeux, via RTSS et/ou une fenêtre
  transparente dessinée par l'app, avec police, taille, espacements, couleurs et position réglables.
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
   (gratuit, guru3d.com ; Outils › Boîte à outils l'installe depuis le miroir officiel de Guru3D,
   Paramètres › Installations ouvre sa page officielle et le lance une fois installé). Les couleurs et la taille du texte lui sont transmises via ses
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
valeurs en viennent), police, taille, **espacements** (entre les valeurs d'une ligne, et
séparations après le nom de la ligne et autour des débits disque et réseau, en nombre
d'espaces), **une couleur par catégorie** (CPU, GPU, RAM, NET…)
pour repérer chaque ligne d'un coup d'œil, et au besoin **une couleur pour les valeurs de
chaque catégorie** (sinon elles suivent une couleur commune), position sur l'écran
(grille 3×3 + marges) et opacité du fond. L'aperçu de l'onglet rend exactement ce que
l'overlay affichera.

**Écran de la fenêtre PCPerfSuite**, au choix :

- **Écran principal** (par défaut, comme avant) ;
- **Cet écran** : un écran de la liste, nommé comme dans Windows (« DELL U2720Q », ou
  « Écran 2 (1920×1080) » s'il ne donne pas de nom). Le bouton **Identifier** affiche
  quelques secondes un grand numéro sur chaque écran. L'écran est retrouvé même s'il change
  de connecteur ; deux écrans du même modèle sont départagés par leur numéro de série (seule
  une empreinte propre à ce PC en est gardée dans `settings.json` : réglages recopiés sur un
  autre PC ou Windows réinstallé, deux écrans identiques sont à choisir de nouveau) ;
- **Écran du jeu** : l'écran de la fenêtre au premier plan. La barre des tâches, le bureau,
  le menu Démarrer et PCPerfSuite ne comptent pas : cliquer sur la barre des tâches d'un
  autre écran ne fait pas sauter l'overlay.

L'ancrage et les marges valent pour tous les écrans, à l'échelle de chacun (24 px à 150 %
font 36 pixels réels). L'overlay se replace tout seul quand un écran est branché ou
débranché, ou que son échelle change. Si l'écran choisi est débranché, l'overlay passe sur
l'écran principal et l'onglet le dit ; il y retourne dès que l'écran revient. Le canal RTSS
n'est pas concerné : il dessine dans le jeu, donc sur l'écran du jeu. Un jeu en plein écran
exclusif masque toujours la fenêtre PCPerfSuite sur son écran (limite de Windows) ; posée sur
un autre écran, elle reste visible.

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
  changement de carte : l'app reconnaît la carte à sa marque, son nom et ses identifiants PCI
  quand le pilote les donne (les réglages d'une version précédente n'avaient que la marque).
  L'onglet le dit, et « Compatibilité de ce PC › Carte GPU pilotée » montre les deux identités.
- **Ce qui bride la carte** en direct (puissance, température, tension, pas de charge), comme la
  ligne « perf cap » de GPU-Z — fourni par NVIDIA seulement, « N/D » ailleurs.
- Ce que la carte n'expose pas est listé sous les curseurs avec la raison (« N/D sur cette
  carte, non exposé par le pilote… »), par exemple la limite de puissance des GPU portables,
  fixée par le constructeur du PC.
- Après chaque application, l'onglet **relit la carte** et affiche, réglage par réglage, ce qui a
  été demandé et ce qu'elle a retenu (« +250 MHz au lieu de +300 MHz demandés ») : les pilotes
  acceptent l'appel puis rabotent une demande hors plage sans prévenir, ce qui explique un
  overclock « qui ne monte pas ».
- Quand le pilote NVIDIA ne donne pas les limites des décalages, l'onglet utilise une **plage
  prudente par défaut** (-500/+1000 MHz cœur, -1000/+2000 MHz mémoire) et le dit : ce ne sont pas
  les vraies limites de la carte.
- **Sécurité thermique** : si la carte reste à 90 °C ou plus au cœur pendant 15 secondes (ou à
  105 °C au point chaud, quand la carte le publie, ce que les RTX 50 ne font pas) alors que l'app a
  relevé un réglage (décalage positif, puissance, température ou tension au-dessus de l'origine),
  l'app lui rend ses réglages d'origine, relit ce qu'elle a retenu et prévient ; « Appliquer au
  démarrage » est alors décoché, pour que le prochain lancement ne remette pas cet overclock. Elle
  continue quand la fenêtre est cachée ou dans la zone de notification. Si la température du GPU
  n'est plus lue pendant 15 secondes alors qu'elle l'était (ou si plus aucun relevé n'arrive, après
  un plantage du pilote par exemple), l'overclock est retiré aussi. Si le pilote refuse le retour
  d'origine, elle réessaie 15 secondes plus tard. Un overclock posé par un autre outil
  (Afterburner…) n'arme rien tant que l'app n'a pas écrit le même bloc de réglages (horloges,
  température et tension d'un côté, limite de puissance de l'autre) ; une fois armée, un
  déclenchement rend toute la carte d'origine. La durée d'une veille ne compte pas.
- **Réveil de veille** : l'onglet relit la carte quelques secondes après le réveil et affiche ses
  vraies valeurs. Les réglages enregistrés ne sont réappliqués que si « Appliquer au démarrage »
  est coché, et pas si la sécurité thermique les a retirés pendant la session.

Le ventilateur du GPU n'est pas dans cet onglet : il est dans **Ventilateurs** avec tous les autres
(avec passage forcé à 100 % au-delà de 88 °C tant que l'app le pilote). Ce passage à 100 % ne retire
pas l'overclock : c'est la sécurité thermique ci-dessus qui s'en charge.

## Processeur

L'onglet **Processeur** a deux sous-onglets : **Réglages** (deux étages, ci-dessous) et **Cœurs** (usage par
cœur et parking).

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
  — le même que règle le BIOS. L'écriture passe par le module IntelMSR de PawnIO.Modules 0.2.11,
  livré avec l'app dans `PawnIO\` (celui de LibreHardwareMonitorLib 0.9.6 ne sait que lire les MSR).
  Elle est marquée **expérimentale** tant qu'elle n'a pas été vérifiée sur une vraie machine : pour
  essayer, baisse une limite plutôt que de la relever (sur les Core de 13e et 14e génération,
  tensions et températures élevées aggravent l'instabilité « Vmin shift »).
- **Lecture seule** : si le BIOS a posé son verrou (bit 63), ou si le module chargé ne sait pas écrire
  (fichier `PawnIO\IntelMSR.bin` absent ou refusé par PawnIO), l'onglet affiche les limites en place,
  champs inactifs, avec la raison exacte, et n'écrit rien. Le verrou du BIOS ne se lève qu'au
  redémarrage, et encore, si le BIOS ne le repose pas.
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
  relevée (soutenue ou de pointe), l'app rétablit d'elle-même les limites d'origine et décoche
  « Appliquer au démarrage ».
- **Au réveil de veille**, les limites sont toujours relues (le firmware a pu reposer les siennes) ;
  elles ne sont réappliquées que si « Appliquer au démarrage » est coché, et pas si la sécurité
  thermique les a retirées pendant la session.
- Rien n'est appliqué au lancement et tout repart d'origine en quittant, sauf si tu coches
  « Appliquer au démarrage ». De toute façon, **les limites ne survivent pas à un redémarrage** :
  le firmware les repose à chaque démarrage, ce qui fait du bouton d'arrêt le filet de sécurité
  ultime.

Cet étage a besoin du pilote **PawnIO** (voir « Points d'attention ») : sans lui, l'onglet affiche
« N/D » et propose de l'installer.

### Cœurs : usage par cœur et parking (toutes plateformes)

Le sous-onglet **Cœurs** montre chaque fil d'exécution (processeur logique), rangé comme le matériel :
par cache L3 (un CCD chez AMD, marqué « CCD avec 3D V-Cache » sur un Ryzen X3D à deux CCD), puis par
classe de cœurs (« performants (P) » et « efficaces (E) », seulement s'il y en a au moins deux), puis par
cœur avec ses fils SMT côte à côte. Le remplissage de chaque jauge est la charge, la même mesure que la
vue des processeurs logiques du Gestionnaire des tâches (`% Processor Utility`, plafonnée à 100) ; les
hachures, la part du temps passé parqué sur les 5 dernières secondes (l'état bascule plusieurs fois par
seconde). L'info-bulle donne aussi la fréquence relative.

La lecture se fait sans pilote ni administrateur (CPU sets de Windows et compteurs de performances),
une fois par seconde, hors du thread d'interface, et seulement tant que le sous-onglet est affiché et la
fenêtre visible. Si le compteur « Parking Status » manque, l'état parqué vient des CPU sets ; si les CPU
sets manquent, le visuel affiche « N/D » avec la raison. Les classes de cœurs des Snapdragon X, des
Meteor Lake et Lunar Lake (cœurs LP-E) et des Strix Point, et le repérage du CCD avec 3D V-Cache, sont
marqués **expérimentaux** tant qu'ils n'ont pas été vérifiés sur une vraie machine.

Les réglages du parking sont ceux du plan d'alimentation actif, en part des cœurs, sur secteur et sur
batterie sur un portable : cœurs toujours actifs (CPMINCORES) et actifs au maximum (CPMAXCORES), leurs
variantes pour les cœurs performants sur un processeur hybride (CPMINCORES1, CPMAXCORES1), et en avancé
l'ordonnancement hybride (SCHEDPOLICY, SHORTSCHEDPOLICY, HETEROPOLICY). Trois préréglages :
**Windows (origine)**, **Tous les cœurs actifs** (100 % au minimum : d'après Microsoft, plus aucun cœur
n'est parqué ; sur un portable, seule la valeur sur secteur change) et **Économie** (la moitié des
cœurs au plus).

- L'écriture demande l'administrateur, et chaque valeur est relue.
- L'origine de chaque réglage est notée avant sa première modification ; « Windows (origine) » la rend,
  par le même chemin que le registre des modifications de l'app.
- **Ces réglages restent en place après la fermeture de l'app.** Ils ne portent que sur le plan actif :
  changer de plan (dont « Performances ultimes ») les perd, et les outils du fabricant (Armoury Crate,
  Lenovo Vantage…) ou les profils internes de Windows peuvent les écraser.
- Ils ne sont pas enregistrés dans les profils de l'onglet Réglages.
- « Tous les cœurs actifs » chauffe et consomme plus au repos. Sur un Ryzen X3D à deux CCD, il demande
  confirmation : le pilote AMD 3D V-Cache parque volontairement le CCD sans V-Cache en jeu, et l'en
  empêcher fait probablement perdre des images.

Le tweak « core parking » d'Optimisation Windows passe par les mêmes réglages : il met aussi à 100 %
le plancher des cœurs performants (CPMINCORES1), sans quoi ils restaient parqués sur un processeur hybride.

## Courbes de ventilation

Chaque ventilateur pilotable a sa carte, carte mère comme GPU :

- **Modes** Auto (firmware) / Manuel / Courbe, et presets Silencieux / Équilibré / Perf.
- **Éditeur de courbe** : on clique un point pour le sélectionner et on le glisse dans les deux
  axes. On en ajoute un au double-clic (à l'endroit voulu) ou avec « Ajouter un point » (au milieu
  du plus grand écart, sans changer la forme de la courbe) ; on en retire un au clic droit, avec
  Suppr ou avec « Retirer le point » (de 2 à 16 points).
- **Source de température** par ventilateur : CPU, GPU, la plus chaude des deux (le bon choix pour
  un ventilateur de boîtier) ou la carte mère.
- **Hystérésis** en °C : le ventilateur ne ralentit qu'une fois la température retombée d'autant,
  ce qui l'empêche de « pomper » autour d'un point de la courbe.
- **Accélération et décélération** en %/s : la vitesse à laquelle le ventilateur change de régime,
  réglée à part pour la montée et pour la descente (par exemple une montée rapide pour suivre la
  chauffe, une descente lente qui passe inaperçue). 100 %/s, le réglage par défaut, veut dire
  immédiat. Le rythme suit le temps réellement écoulé, quelle que soit la cadence du Monitoring ; un
  ventilateur à l'arrêt repart directement à sa consigne, et la protection thermique du GPU (100 %
  au-delà de 88 °C) n'attend pas. « Consigne actuelle » affiche la cible pendant la transition.
- **Vitesses mini et maxi**, pour un ventilateur qui cale trop bas ou qu'on ne veut jamais entendre
  à fond.
- **Arrêt complet à froid (0 RPM)** sous une température au choix — désactivé par défaut, tous les
  ventilateurs ne redémarrant pas proprement.
- **Appliquer à tous** recopie une courbe et ses réglages sur les autres ventilateurs, jamais sur
  une pompe.
- **Profils** : les courbes de tous les ventilateurs s'enregistrent sous un nom (mode, courbe,
  température suivie et réglages de chacun) et se rappellent en un clic ; on peut les renommer et
  les supprimer, et un profil du même nom est remplacé. Un profil venu d'une autre machine, ou
  d'avant qu'on débranche un ventilateur, s'applique quand même : ce qui n'existe pas ici est
  ignoré, et l'app dit quoi et pourquoi (ventilateur absent, portable, app sans administrateur).
  Les valeurs douteuses d'un profil (mode inconnu, pourcentage hors plage, points désordonnés) sont
  ramenées dans les limites plutôt que posées telles quelles, et les ventilateurs que le profil ne
  mentionne pas restent tels quels. Sur un portable, un profil n'atteint jamais le contrôleur
  embarqué : seuls les ventilateurs listés dans l'onglet sont pilotés.

Sécurité : par défaut **rien n'est réappliqué au démarrage et tout est rendu au pilote en
quittant**. La case « Appliquer au démarrage » rend l'overclock persistant dans les deux
sens (réappliqué au lancement, conservé à la fermeture). Un décalage trop ambitieux fige
l'écran ou fait planter le pilote sans rien casser : un redémarrage remet tout d'origine,
et l'overclock n'est pas réappliqué tant que cette case est décochée. Monte par paliers de
15 à 25 MHz et teste entre chaque. Chaque réglage refusé par le pilote est signalé dans
l'onglet plutôt que d'échouer en silence.

### Identifier ses ventilateurs

Une carte mère ne dit le nom de ses connecteurs (« CPU Fan », « AIO Pump ») que si
LibreHardwareMonitor a une table pour ce modèle. Sinon la puce ne donne qu'un numéro de canal, et
l'app ne peut pas deviner ce qui y est branché : le ventilateur est numéroté (« Ventilateur 6 ») et
rangé dans « Non identifiés ». Ce qu'on peut faire :

- **Repérer** fait tourner le ventilateur à 100 % pendant 5 secondes pour le retrouver dans le
  boîtier (sur un PC de bureau seulement), puis le rend à son mode.
- Le **crayon** de chaque carte permet de la **renommer** et de changer sa **catégorie** (processeur,
  pompe, boîtier…). Le choix est enregistré par ventilateur ; « Revenir à la détection » l'efface.
- Les **connecteurs sans ventilateur détecté** (0 tr/min alors que la carte les alimente) sont
  regroupés dans une section repliée, et restent pilotables : un ventilateur sans fil de vitesse
  (2 broches, ou branché sur un hub) lit aussi 0 tr/min.
- Une carte graphique n'apparaît qu'une fois par ventilateur, même si le pilote et
  LibreHardwareMonitor la décrivent tous les deux.
- Une **pompe** ne s'arrête jamais (pas d'arrêt complet à froid) et ne descend pas sous 30 %.

Ce que le logiciel ne peut pas savoir : à quel connecteur physique correspond un canal sans nom ;
combien de ventilateurs sont branchés sur un hub (un seul fil de vitesse remonte, et une seule
commande les pilote tous) ; si un ventilateur sans fil de vitesse est présent ou non ; et, sans
nom, si un connecteur porte une pompe. Paramètres › Compatibilité de ce PC détaille ce qui a été
détecté sur ta machine.

## Groupes de profils *(expérimental)*

Régler › Profils › Groupes réunit les réglages du processeur, de la carte graphique et de la
ventilation sous un nom, appliqués d'un clic. Pas encore vérifié sur une vraie machine.

- **Créer** : « Enregistrer l'état actuel » relit ce que le matériel a vraiment (une case par
  dimension ; la ventilation, une fois les ventilateurs relevés). « Modifier » compose chaque
  dimension : la garder, ne pas y toucher, la remettre d'origine, reprendre l'état actuel d'un
  onglet ou l'un de ses profils. « Régler dans l'onglet » applique le groupe et ouvre l'onglet :
  on ajuste, puis « Mettre à jour le groupe ».
- **Appliquer** passe toujours par les onglets Processeur, GPU et Ventilateurs, avec leurs
  sécurités. Le rapport dit, dimension par dimension, ce qui a été posé, rogné, refusé ou ignoré,
  et pourquoi. Des watts ou un overclock relevés sur un autre processeur ou une autre carte (même de
  la même marque) ne sont pas posés. Sans l'avertissement CPU accepté, les watts sont ignorés ; sans
  la renonciation Intel, l'overclock aussi. Sur un portable, seuls les ventilateurs de la carte
  graphique se règlent (par son pilote).
- **Ordre** : quand le groupe monte en puissance, les ventilateurs d'abord ; quand il descend, le
  processeur et la carte graphique d'abord.
- **Permanent** : les réglages du plan d'alimentation (mode boost, états min et max, préférence
  performance/économie) restent après la fermeture. Leur origine est notée, et Paramètres ›
  Compatibilité de ce PC › « Tout rétablir » la rend. Les watts, l'overclock et les ventilateurs
  sont rendus d'origine en quittant, sauf « Appliquer au démarrage » coché dans leur onglet.
- **Démarrage** : appliquer un groupe en fait les dernières valeurs des onglets ; ce sont leurs
  cases « Appliquer au démarrage » qui décident de ce qui est reposé au lancement. La page montre le
  dernier groupe appliqué, « conforme » ou ce qui a changé depuis.
- **Prudence** : un groupe qui relève l'overclock du GPU ou les watts est surveillé 30 minutes. Un
  arrêt anormal, un écran bleu, un redémarrage de cause inconnue ou un pilote graphique relancé
  (TDR) pendant ce temps le suspend, et décoche « Appliquer au démarrage » pour ce qu'il avait
  relevé. Le réappliquer demande une confirmation.
- **Bail de réglage** : quand un autre module pilote les réglages (bench, recherche d'OC, bascule
  automatique le temps d'appliquer un groupe), les onglets affichent « Réglages pilotés par…
  depuis… », leurs commandes sont grisées, et appliquer un groupe est refusé avec la raison. Les
  sécurités thermiques agissent toujours.

## Profils automatiques selon l'usage *(expérimental)*

Régler › Profils › Automatique bascule seule entre trois groupes : **bureautique**, **jeu léger** et
**jeu exigeant**. Désactivée par défaut ; tant qu'elle l'est, rien n'est relevé. Seuils et délais pas
encore vérifiés sur une vraie machine.

- **Détection** : un moteur déterministe et explicable décide, sans IA générative. Il regarde
  l'application au premier plan (son exécutable, lu sans ouvrir plus qu'un accès en lecture limité),
  le plein écran, les charges du processeur et de la carte graphique, les images mesurées par RTSS
  s'il tourne, et le secteur ou la batterie. Entrée en jeu après environ 30 s tenues, sortie après
  2 min sans signe de jeu (un menu en plein écran reste du jeu), au plus une bascule toutes les
  2 min. Une charge processeur lourde et longue hors jeu (compilation, rendu) va au jeu exigeant ;
  sur batterie, le jeu exigeant est ramené au jeu léger. PCPerfSuite et la barre des tâches au
  premier plan ne comptent pas.
- **Règles par application** : prioritaires. Une application (chemin complet par défaut, nom seul
  pour une application qui change de dossier à chaque mise à jour, éditeur facultatif) vise un usage
  ou un groupe précis ; à choisir parmi les applications vues ou par « Parcourir… ».
- **Groupes** : générés à l'activation, modifiables par « Modifier » comme tout groupe ; un groupe
  fait à la main pour un usage l'emporte. Ventilation : courbes Silencieux, Équilibré ou Perf, décalées
  d'après les températures relevées, seulement pour les ventilateurs déjà en courbe ou en manuel (une
  case prend en main ceux laissés au BIOS ; sur un portable, seule la ventilation de la carte
  graphique). Processeur : préférence performance/économie par usage, watts réduits en bureautique
  quand l'onglet sait les régler. Carte graphique : d'origine en bureautique ; en jeu, l'overclock
  enregistré dans l'onglet GPU avec « Appliquer au démarrage », sur la même carte (l'OC automatique le
  remplacera). Rien n'est inventé. Après quelques jours, la page propose « Régénérer » pour affiner ;
  un groupe modifié à la main n'est jamais écrasé.
- **Garde-fous** : chaque bascule passe par les onglets et leurs sécurités (accord de risque CPU,
  renonciation Intel, autre matériel : la partie est refusée et le journal le dit). Verrouillée après
  une sécurité thermique, jusqu'à « Déverrouiller » ou la relance. En pause pendant que le bench ou
  une recherche d'OC tient les réglages, et 10 min après un réglage à la main (l'usage en cours garde
  alors ce réglage). Quand une bascule relève l'overclock du GPU ou les watts, un arrêt anormal, un
  écran bleu ou un pilote graphique relancé dans les 30 min suspend le groupe, et tout groupe qui porte
  le même réglage : la bascule n'y revient pas d'elle-même, et le signale. Si ce réglage est celui que
  l'onglet repose au démarrage, sa case « Appliquer au démarrage » est décochée.
- **Fermeture** : une bascule n'est jamais l'état de démarrage. L'overclock, les watts et les
  ventilateurs sont rendus d'origine en quittant ; la première bascule du lancement suivant remplace
  l'état enregistré dans les onglets. Les réglages du plan d'alimentation sont permanents : **à la
  fermeture, le PC reste sur ceux du dernier groupe posé** (« Tout rétablir » les rend).
- **Suivi** : une bulle discrète à chaque bascule (désactivable, sans nom d'application), le journal
  des 20 dernières bascules avec ce qui n'a pas été posé, et la ligne « Bascule automatique » du
  diagnostic, qui ne nomme aucune application. « Effacer l'historique » vide `usage.json`.

## Boîte à outils

Outils › Boîte à outils réunit les outils tiers utiles au diagnostic et au réglage. Chacun a son rôle,
son éditeur, sa licence, la version proposée, sa taille, son état sur ce PC, et ses boutons :
**Installer** ou **Lancer**, **Télécharger** (le lien direct est affiché et copiable), **Page
officielle**.

| Rubrique | Outil | Ce que fait la Boîte à outils |
|---|---|---|
| Diagnostic | CPU-Z, CrystalDiskInfo | portable, signé (CPUID, CrystalMark Inc.) |
| | GPU-Z, HWiNFO | page officielle : TechPowerUp n'a que des liens qui expirent en 24 h, HWiNFO refuse ce qui n'est pas un navigateur |
| Stress et bench | OCCT, Cinebench R23, CrystalDiskMark | portable, signé (OCBASE, MAXON Computer GmbH, CrystalMark Inc.) |
| | Prime95, FurMark 2, y-cruncher | non signés : téléchargés dans tes Téléchargements, jamais lancés |
| GPU et pilotes | DDU | installeur signé (Wagnardsoft) |
| | RTSS, MSI Afterburner | installeur signé (MSI), extrait du zip du miroir officiel de Guru3D |
| | NVCleanstall | page officielle (TechPowerUp) |
| Divers | PawnIO | installeur signé (namazso) |
| | 7-Zip, OpenRGB | non signés : téléchargés, jamais lancés |
| | ISLC | archive auto-extractible signée (Wagnardsoft), déposée dans tes Téléchargements |
| | MemTest86 | page officielle : il démarre depuis une clé USB |
| | Diagnostic de mémoire Windows | lancé directement (mdsched.exe, fourni par Windows) |

Cinebench R23 est l'ancienne version : Maxon ne propose plus que Cinebench 2026 (2,7 Go, sans
adresse versionnée), sur sa page officielle.

**Ce qui est vérifié.** Les adresses de téléchargement sont versionnées : seuls PawnIO et OCCT ont
un lien « dernière version » stable, qui ne pourrait pas être accompagné d'une empreinte. Pour chaque
fichier :

- HTTPS à chaque étape, redirections comprises, vers les seuls hôtes de l'éditeur figés dans l'app ;
- taille bornée par outil (jusqu'à 400 Mo pour OCCT et Cinebench) ;
- empreinte SHA-256 du catalogue obligatoire : un fichier différent, même au même nom, est supprimé ;
- pour un programme, signature Authenticode valide et éditeur attendu, relevé sur le vrai fichier ;
- une archive est extraite sans que rien ne puisse sortir de son dossier (« zip-slip » refusé), puis
  son exe principal est vérifié.

En avril 2026, des installeurs piégés ont été servis quelques heures depuis le site d'un éditeur
d'outils de diagnostic : c'est ce que l'empreinte et la signature arrêtent. Un lien mort ou une
empreinte qui a changé affichent la raison et renvoient vers la page officielle, sans jamais contourner
une page à jeton. Pour un installeur signé, l'app propose aussi `winget install --id … --exact` si
winget est présent (il manque dans Windows Sandbox et avant la première ouverture de session).

**Où vont les outils.** Les outils portables vont dans `%ProgramData%\PCPerfSuite\Tools\<outil>\<version>`.
Ce dossier est créé avec une liste d'accès protégée (administrateurs et SYSTEM en écriture, utilisateurs
en lecture) et le niveau d'intégrité « Élevé ». Un dossier déjà créé par un autre compte, ou une
jonction, est refusé. « Supprimer » efface l'outil. Un installeur s'ouvre après confirmation
(« Non » par défaut) : l'app est administrateur, il n'y aura pas d'autre invite. Les fichiers
téléchargés seuls arrivent vérifiés dans tes Téléchargements, marqués comme venus d'Internet. La copie se
fait avec tes droits à toi (ceux du bureau), jamais en administrateur : même un dossier Téléchargements
redirigé ailleurs ne reçoit que ce que ton compte pourrait y écrire lui-même. Elle va dans les
Téléchargements du compte connecté, même si l'app tourne sous un autre compte administrateur.

**Lancer.** Un outil est lancé par le shell du bureau, avec tes droits habituels et non ceux,
administrateur, de PCPerfSuite. S'il a besoin d'être administrateur, Windows te le demande. Un
outil portable est revérifié avant chaque lancement.

**Licences.** Le badge « licence pro requise » signale les outils dont la version gratuite interdit
l'usage commercial, celui d'un technicien qui facture son intervention : HWiNFO, OCCT, FurMark,
y-cruncher, Cinebench R23. CPU-Z, GPU-Z, Prime95, CrystalDiskInfo et CrystalDiskMark (MIT), DDU (MIT),
7-Zip, OpenRGB, PawnIO et MemTest86 Free l'autorisent.

**Catalogue.** La liste des versions (adresse, SHA-256, taille) vient pour l'instant de la copie
livrée avec l'app (catalogue n° 1, relevé le 01/10/2026). Le catalogue en ligne est prêt mais **pas
encore activé** : son dépôt GitHub public reste à créer et sa clé publique à inscrire dans l'app
(`tools/catalogue/LISEZMOI.md`), ce que la page et le diagnostic disent. Une fois activé, une GitHub
Action le régénère chaque jour depuis winget et les versions GitHub, et Denis le signe en local, avec
une clé qui n'entre jamais dans la CI (ECDSA P-256). L'app ne le prendra que si sa signature est
valide et si son numéro dépasse le plus haut déjà accepté : un ancien catalogue servi de nouveau est
refusé. Sans réseau, elle utilise sa dernière liste connue. Même signé, le catalogue ne peut changer
ni les hôtes ni les éditeurs, figés dans l'app.

Tout est **expérimental** tant que chaque parcours n'a pas été essayé sur une vraie machine. La ligne
« Boîte à outils » de Paramètres › Compatibilité de ce PC donne l'origine du catalogue, l'état du
dossier sécurisé, la présence de winget et les outils portables déposés. « Tout rétablir » (registre des
modifications) supprime les outils portables. Un outil posé par son propre installeur ne se
désinstalle pas d'office : il se retire depuis Paramètres Windows › Applications, et l'app le dit.

## Compatibilité et capteurs non disponibles

Tous les PC n'exposent pas les mêmes capteurs : cela dépend du CPU, du GPU, de la carte mère,
des pilotes et, sur un portable, de la marque. L'app le dit toujours clairement :

- `--` : valeur pas encore lue (juste après le lancement) ;
- `N/D` : **ce PC ne fournit pas cette valeur**. La tuile du Monitoring explique pourquoi, et le
  choix des métriques (Monitoring et overlay) signale « Non disponible sur ce PC ». Exemple : la
  température mémoire (junction) n'existe que sur les GPU NVIDIA en GDDR6X.
- **Paramètres › Compatibilité de ce PC** résume ce qui est lu et pilotable sur la machine, et
  pourquoi le reste manque. « Copier le rapport » en fait un texte à joindre à un signalement.
  On y trouve aussi :
  - **Raisons de bridage CPU** : ce qui bridait le processeur au dernier relevé (thermique,
    PROCHOT, puissance), TjMax, fréquences, et les limitations par le micrologiciel journalisées
    par Windows. Lu dans les registres MSR (Intel) ou la PM table (AMD, **expérimental**, Ryzen
    3000, 5000 et 5000H seulement) avec PawnIO, sinon dans les compteurs de Windows, qui ne
    donnent qu'un indice. Rien n'est jamais écrit dans le processeur.
  - **Journaux Windows** : arrêts brutaux, écrans bleus, arrêts inattendus, TDR du pilote
    graphique, erreurs matérielles WHEA et erreurs de disque des 30 derniers jours. Seuls les
    types et les codes sont repris, jamais le texte des messages, qui peut nommer des applications.
  - **Journal de session** : les opérations risquées interrompues au dernier arrêt, et ce qui leur
    est arrivé d'après Windows (interrompu, arrêt brutal, écran bleu…). Un « arrêt brutal » dit un
    indice compatible avec une coupure ou une alimentation qui décroche, jamais une certitude.
  - **Animations et effets** : la valeur lue de chaque effet visuel, ceux que PCPerfSuite a
    modifiés avec leur valeur d'origine, et le compte dont le profil est réglé (masqué dans le
    rapport copié).
- **Paramètres › Installations** liste les deux logiciels externes dont l'app a besoin, avec leur
  état (installé ou non, version) et à quoi ils servent :
  - **PawnIO** : le bouton télécharge la dernière version de l'installeur depuis le dépôt GitHub
    officiel de son auteur (HTTPS uniquement, redirections comprises), vérifie sa signature
    Authenticode et l'éditeur (`namazso`), puis le lance. Le même bouton existe dans l'onglet
    Processeur. « Mettre à jour » lit d'abord la dernière version publiée (dans la redirection de
    la page « releases/latest », sans rien télécharger) et ne lance rien si PawnIO est déjà à jour ;
    l'installeur, lui, refuse de réinstaller une version déjà en place (code 183), ce que l'app
    explique au lieu de l'afficher comme une panne.
  - **RTSS** : Guru3D ne propose pas de lien stable vers sa *dernière* version (pages à jeton, nom
    de fichier différent à chaque version). Son miroir officiel (ftp.nluug.nl) sert en revanche un zip
    par version : la Boîte à outils l'installe d'après son catalogue, en vérifiant l'empreinte et la
    signature (MSI). Ici, le bouton ouvre la page de téléchargement officielle ; l'état se met à jour
    dès le retour dans l'app, et RTSS peut être lancé depuis là.

  L'état est aussi repris dans « Compatibilité de ce PC ». Rien n'est jamais installé sans action
  de ta part.

  **Rappel visuel** : tant qu'un de ces logiciels n'est pas installé, le bouton Paramètres de la
  barre latérale clignote (son fond passe progressivement à l'orange, puis revient), avec une
  info-bulle qui dit quoi installer et pourquoi ; une fois dans Paramètres, c'est l'onglet
  Installations qui clignote. Tout s'arrête dès que tout est installé, et aussi quand la fenêtre
  est rangée dans la zone de notification ou réduite.

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

Le **contrôle GPU** (onglet GPU) passe par NVAPI pour NVIDIA, ADLX pour AMD Radeon et IGCL pour
Intel Arc. AMD et Intel sont encore **expérimentaux** (codés, pas encore vérifiés sur de vraies
cartes) : l'onglet l'affiche dans un bandeau. Le monitoring GPU couvre les trois marques.

## Points d'attention importants

- **L'app doit tourner en administrateur.** Le manifeste (`app.manifest`) le demande déjà
  automatiquement au lancement — Windows affichera l'invite UAC. Sans ça, la plupart des
  capteurs et tous les réglages système resteront inaccessibles (l'app te le signale dans
  l'interface plutôt que de planter).
- **Lancement au démarrage de Windows** (Paramètres › Général) : l'app exigeant les droits
  administrateur, ni la clé de registre « Run » ni le dossier Démarrage ne conviennent (Windows
  bloque ces lancements, ou il faudrait accepter une invite UAC à chaque ouverture de session).
  L'option crée, une seule fois, une tâche planifiée « à l'ouverture de session, avec les
  autorisations maximales » ; le Planificateur de tâches lance ensuite l'app directement avec le
  jeton administrateur de la session, sans invite, et elle démarre dans la zone de notification.
  Désactiver l'option supprime la tâche. L'option est grisée, avec sa raison, si l'app n'est pas
  lancée en administrateur ou si elle tourne sous un autre compte que celui de la session.
- **Pilote PawnIO** : les capteurs bas niveau et la limite de puissance du processeur passent
  par [PawnIO](https://pawnio.eu/), un pilote signé et à jour. Il remplace WinRing0, que Windows
  Defender signale depuis 2025 comme pilote vulnérable (CVE-2020-14979) et que la liste de blocage
  des pilotes refuse de charger. PawnIO n'ouvre pas un accès brut au matériel : il exécute des
  modules signés qui décident eux-mêmes de ce qu'ils autorisent, et un accès refusé est affiché
  comme tel par l'app. Prends simplement la dernière version, avec le bouton de Paramètres ›
  Installations ou depuis son site (testé avec la 2.2.0). L'onglet Processeur affiche la version installée, et entre parenthèses la version de
  l'interface de programmation, qui est celle que renvoie le pilote lui-même.
- **Modules PawnIO** : chaque version d'un module a sa propre liste de registres autorisés, et PawnIO
  ne charge que des modules signés par leur auteur (l'app ne peut pas écrire les siens). Les modules
  viennent de LibreHardwareMonitorLib, sauf IntelMSR : l'app livre celui de
  [PawnIO.Modules 0.2.11](https://github.com/namazso/PawnIO.Modules/tree/0.2.11) (LGPL-2.1, signé par
  namazso) dans `PawnIO\IntelMSR.bin`, à côté de l'exe, parce que celui de LHM 0.9.6 ne sait pas écrire
  PL1/PL2. C'est un fichier à part, remplaçable par une autre version signée ; sans lui, l'app reprend
  celui de LHM et affiche les limites en lecture seule. « Paramètres › Compatibilité de ce PC ›
  Modules PawnIO » liste, pour chaque module chargé, sa provenance, sa version et ses fonctions.
- **Isolation du noyau / Intégrité de la mémoire (HVCI ou "Memory Integrity")** : cette option
  n'empêche pas PawnIO de fonctionner (contrairement à WinRing0), mais l'app affiche quand même
  l'état du réglage dans l'onglet Optimisation Windows, avec un lien direct vers le réglage Windows.
- **Fermer la fenêtre n'arrête pas l'app** (voir « Zone de notification » plus haut). Tant
  qu'elle tourne, les ventilateurs pilotés par une courbe restent sous son contrôle : c'est
  « Quitter » depuis l'icône qui les repasse en automatique et rend la carte au pilote.
- Les réglages sont stockés dans
  `%LOCALAPPDATA%\PCPerfSuite\settings.json` (lisible et modifiable à la main).
- **Rapport de compatibilité** : « Copier le rapport » masque le nom du compte Windows, y compris
  dans les chemins des profils (remplacés par `%LOCALAPPDATA%`, `%USERPROFILE%` ou
  `C:\Users\(compte)`). Relis-le quand même avant de le coller dans un signalement public : un
  nom de PC ou de dossier personnel peut apparaître ailleurs.

## Compiler et lancer

Prérequis : SDK .NET 8.

```
cd PCPerfSuite
dotnet restore
dotnet build -c Release
dotnet test
```

Puis lance `src\PCPerfSuite.App\bin\Release\net8.0-windows\PCPerfSuite.exe` (clic droit →
Exécuter en tant qu'administrateur si l'invite UAC ne se déclenche pas automatiquement), ou
ouvre `PCPerfSuite.sln` dans Visual Studio et F5.

La solution compile sans erreur (C# et XAML), mais elle n'a pas pu être *exécutée* sur une
vraie machine Windows depuis cette session : le comportement des appels NVAPI, ADLX et IGCL
dépend de ta carte et de ton pilote. Si un réglage ne prend pas, l'onglet GPU affiche le refus — copie
le message, on ajuste.

## Licence

PCPerfSuite est un logiciel propriétaire, à utilisation gratuite (voir `LICENSE`). Les composants
tiers livrés avec l'app restent sous leur propre licence (voir `THIRD-PARTY-NOTICES.md`).
