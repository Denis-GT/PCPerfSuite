# Composants tiers

PCPerfSuite est propriétaire (voir [LICENSE](LICENSE)). Les composants ci-dessous, livrés avec l'app ou dont elle
reprend des déclarations, restent sous leur propre licence. **Chaque conversation qui ajoute une dépendance complète
ce fichier** (paquet, version, licence, auteur, source), en relevant la licence dans le `.nuspec` du paquet plutôt que
de mémoire.

Relevé du 30/09/2026 : `dotnet list src/PCPerfSuite.App/PCPerfSuite.App.csproj package --include-transitive`.

## Conséquences des licences

- **LGPL (NvAPIWrapper, modules PawnIO)** : la bibliothèque doit rester remplaçable par l'utilisateur. Les DLL LGPL
  restent des fichiers à part, à côté de l'exe : pas de publication en fichier unique qui les engloberait.
- **MPL-2.0 (LibreHardwareMonitor et ses bibliothèques)** : les fichiers MPL modifiés devraient être publiés. PCPerfSuite
  les utilise sans modification ; le lien vers leurs sources suffit.
- **Apache-2.0 (HidSharp)** et **MIT** : garder la mention de copyright et le texte de la licence.

## Paquets livrés

| Composant | Version | Licence | Auteur | Source |
|---|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | LibreHardwareMonitor | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| Modules PawnIO embarqués par LibreHardwareMonitorLib (IntelMSR, RyzenSMU, AMDFamily17…) | ceux de LHM 0.9.6 | LGPL-2.1 | namazso | https://github.com/namazso/PawnIO.Modules |
| Module PawnIO IntelMSR, livré à part dans `PawnIO\IntelMSR.bin` (avec `COPYING.LGPL-2.1.txt` et `LISEZMOI.txt`), fichier de release_0_2_11.zip tel quel, SHA-256 figé par `PawnIoModuleTests` | PawnIO.Modules 0.2.11 | LGPL-2.1 | namazso | https://github.com/namazso/PawnIO.Modules/tree/0.2.11 |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | Florian K. | https://github.com/Blacktempel/BlackSharp |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | Florian K. | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | Florian K. | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp | 2.6.4 | Apache-2.0 | James F. Bellinger | https://software.seekye.com/hidsharp |
| NvAPIWrapper.Net | 0.8.1.101 | LGPL-3.0 | Soroush Falahati | https://github.com/falahati/NvAPIWrapper |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.6 | MIT | Microsoft | https://github.com/Microsoft/perfview |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | MIT | Microsoft | https://github.com/dotnet/diagnostics |
| CommunityToolkit.Mvvm | 8.2.2 | MIT | .NET Foundation | https://github.com/CommunityToolkit/dotnet |
| System.Management, System.IO.Ports, System.CodeDom, System.Text.Json et autres paquets System.* / Microsoft.Extensions.* | voir le relevé | MIT | Microsoft | https://github.com/dotnet/runtime |
| Mono.Posix.NETStandard | 1.0.0 | MIT | Microsoft | https://github.com/mono/mono |

Le pilote **PawnIO** lui-même n'est pas redistribué : l'utilisateur l'installe depuis la source officielle de son
auteur (Paramètres › Installations).

## Déclarations reprises de SDK

Aucune DLL de ces SDK n'est livrée : l'app appelle celles qu'installe le pilote graphique, avec des déclarations
recopiées des en-têtes publics.

| SDK | Repris dans | Licence | Source |
|---|---|---|---|
| AMD ADLX | `Core/Hardware/Gpu/AdlxNative.cs` (ordre des tables de fonctions, structures) | MIT | https://github.com/GPUOpen-LibrariesAndSDKs/ADLX |
| Intel Graphics Control Library | `Core/Hardware/Gpu/IgclNative.cs` (structures d'`igcl_api.h`) | MIT | https://github.com/intel/drivers.gpu.control-library |
| RTSS (RivaTuner Statistics Server) | `Core/Overlay/RtssSharedMemory.cs`, `RtssOsdClient.cs` (disposition de `RTSSSharedMemory.h`) | SDK livré avec RTSS, conditions à vérifier | https://www.guru3d.com/page/rivatuner-rtss-homepage/ |
