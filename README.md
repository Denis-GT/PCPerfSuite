# PCPerfSuite

App Windows (WPF, .NET 8) de monitoring et de tuning PC. Construite pour ta config
(i5-14600K, RTX 5070 Ti, ASUS TUF B760-PLUS WIFI) mais faite pour rester compatible
avec la plupart des configs Intel/AMD + NVIDIA/AMD/Intel.

## Ce qui est livré dans cette v1 (MVP)

- **Monitoring** — dashboard temps réel plus complet et plus lisible que le Gestionnaire
  des tâches : CPU (charge, température, puissance, fréquence), GPU (charge, températures
  cœur + hot spot, horloges cœur/mémoire, VRAM, ventilo), RAM, carte mère, et la liste de
  tous les ventilateurs détectés avec RPM + %.
- **Nettoyage** — cache shaders NVIDIA/AMD/Intel, cache shaders DirectX (D3DSCache), cache
  Steam, fichiers temporaires (%TEMP% et système), Prefetch, cache Windows Update, rapports
  d'erreurs Windows, cache des miniatures, cache Delivery Optimization, + un bouton "Vider
  la corbeille". Chaque catégorie affiche la taille réelle occupée et propose "Ouvrir le
  dossier" et "Nettoyer".
- **Paramètres Windows** — y compris les réglages masqués : plan d'alimentation
  "Performances ultimes" (cette app le débloque et l'active), planification GPU accélérée
  par le matériel (HAGS), Mode Jeu, effets visuels, power throttling, limitation réseau
  multimédia, démarrage rapide, suspension sélective USB, core parking CPU, ASPM PCIe, et
  un indicateur pour l'isolation du noyau (HVCI).

## Prévu ensuite (pas encore dans cette v1)

Les onglets "Ventilateurs", "GPU" et "Overlay" sont déjà dans l'app (avec le détail de ce
qui est prévu) mais pas encore fonctionnels :

1. **Courbes de ventilation** (CPU/boîtier/watercooling) — nécessite d'aller chercher,
   pour ta carte mère précise, quels canaux de la puce Super I/O LibreHardwareMonitor sait
   piloter en écriture (pas juste lire). C'est variable d'un modèle à l'autre.
2. **Overclock/contrôle GPU** — fréquences, tension, power limit et courbe ventilo GPU via
   NVAPI pour ta RTX 5070 Ti (AMD/Intel ensuite via ADL/Intel Control Library).
3. **Overlay façon MSI Afterburner** — le plus gros morceau technique. RTSS utilise un
   pilote noyau pour s'accrocher à n'importe quel jeu, y compris en plein écran exclusif.
   Sans réinventer ça, on commencera par un overlay fenêtre transparente toujours au-dessus
   (fonctionne en jeu fenêtré/sans bordure, qui couvre la grande majorité des jeux
   modernes), avec police/taille/couleur/position personnalisables par métrique.

## Points d'attention importants

- **L'app doit tourner en administrateur.** Le manifeste (`app.manifest`) le demande déjà
  automatiquement au lancement — Windows affichera l'invite UAC. Sans ça, la plupart des
  capteurs et tous les réglages système resteront inaccessibles (l'app te le signale dans
  l'interface plutôt que de planter).
- **Isolation du noyau / Intégrité de la mémoire (HVCI ou "Memory Integrity")** : si cette
  option est activée dans Windows, elle peut bloquer le pilote (WinRing0) utilisé par
  LibreHardwareMonitor pour lire certains capteurs bas niveau. L'app affiche l'état de ce
  réglage dans l'onglet Paramètres avec un lien direct vers le réglage Windows concerné —
  à toi de juger le compromis sécurité/monitoring.
- Les valeurs manquantes s'affichent en `--` plutôt qu'un plantage : selon ta carte mère,
  tous les capteurs ne sont pas forcément exposés par LibreHardwareMonitorLib.

## Compiler et lancer

Prérequis : SDK .NET 8 (présent sur ta machine).

```
cd PCPerfSuite
dotnet restore
dotnet build -c Release
```

Puis lance `src\PCPerfSuite.App\bin\Release\net8.0-windows\PCPerfSuite.exe` (clic droit →
Exécuter en tant qu'administrateur si l'invite UAC ne se déclenche pas automatiquement), ou
ouvre `PCPerfSuite.sln` dans Visual Studio et F5.

Comme ce projet n'a pas encore pu être compilé sur une vraie machine Windows depuis cette
session, il est possible qu'une erreur de build apparaisse au premier essai (version exacte
d'un package NuGet, détail d'API) — si ça arrive, copie-colle l'erreur, je corrige.
