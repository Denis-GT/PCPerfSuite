using System.Diagnostics;
using Microsoft.Win32;

namespace PCPerfSuite.Core.PowerSettings;

/// <summary>
/// Catalogue des réglages de performance Windows, y compris ceux masqués dans l'interface
/// standard (plan d'alimentation "Performances ultimes", planification GPU matérielle, etc.).
/// Toutes les écritures HKLM nécessitent que l'app tourne en administrateur (voir app.manifest).
/// </summary>
public sealed class WindowsPerformanceSettingsService
{
    private readonly PowerPlanService _powerPlans = new();

    public IReadOnlyList<PerformanceTweak> GetTweaks()
    {
        return new List<PerformanceTweak>
        {
            new()
            {
                Id = "ultimate-performance",
                Name = "Plan d'alimentation \"Performances ultimes\"",
                Category = "Alimentation",
                Description = "Plan caché par Windows, dérivé de \"Performances élevées\" mais sans aucune micro-mise en veille des cœurs/périphériques. Idéal pour un PC de bureau branché sur secteur en permanence.",
                GetState = () => _powerPlans.IsUltimatePerformanceActiveAsync().GetAwaiter().GetResult()
                    ? TweakState.Enabled : TweakState.Disabled,
                Apply = enable =>
                {
                    if (enable)
                    {
                        _powerPlans.EnableUltimatePerformanceAsync().GetAwaiter().GetResult();
                    }
                    else
                    {
                        // Revient sur le plan "Équilibré" standard de Windows.
                        _powerPlans.SetActiveSchemeAsync(WellKnownSchemeGuids.Balanced).GetAwaiter().GetResult();
                    }
                },
            },

            new()
            {
                Id = "hags",
                Name = "Planification GPU accélérée par le matériel (HAGS)",
                Category = "Affichage / GPU",
                Description = "Laisse le GPU gérer lui-même sa mémoire vidéo et l'ordonnancement au lieu du pilote Windows. Peut réduire la latence d'entrée sur certaines configs, neutre voire négatif sur d'autres.",
                RequiresRestart = true,
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
                    return value switch { 2 => TweakState.Enabled, 1 => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => RegistryHelper.WriteDword(RegistryHive.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", enable ? 2 : 1),
            },

            new()
            {
                Id = "game-mode",
                Name = "Mode Jeu Windows",
                Category = "Jeux",
                Description = "Empêche Windows Update et les notifications système de perturber les jeux en cours, priorise le jeu au premier plan.",
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.CurrentUser,
                        @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                    // Le Mode Jeu est activé par défaut sur Windows 10/11 : tant que l'utilisateur ne
                    // l'a jamais désactivé explicitement, cette clé de registre n'existe pas du tout.
                    // Une valeur absente veut donc dire "activé", pas "état inconnu".
                    return value == 0 ? TweakState.Disabled : TweakState.Enabled;
                },
                Apply = enable => RegistryHelper.WriteDword(RegistryHive.CurrentUser,
                    @"Software\Microsoft\GameBar", "AutoGameModeEnabled", enable ? 1 : 0),
            },

            new()
            {
                Id = "visual-effects-performance",
                Name = "Effets visuels : privilégier les performances",
                Category = "Interface",
                Description = "Équivalent de Panneau de configuration → Performances → \"Ajuster afin d'obtenir les meilleures performances\" : coupe animations, ombres et transparences.",
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting");
                    // Valeur absente = réglage par défaut de Windows ("Laisser Windows choisir"), qui
                    // n'active pas ce mode "performances" (équivalent à 0/1/3).
                    return value == 2 ? TweakState.Enabled : TweakState.Disabled;
                },
                Apply = enable => RegistryHelper.WriteDword(RegistryHive.CurrentUser,
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", enable ? 2 : 0),
            },

            new()
            {
                Id = "power-throttling",
                Name = "Désactiver le power throttling",
                Category = "Alimentation",
                Description = "Windows peut brider les apps en arrière-plan pour économiser l'énergie (surtout sur laptop). Désactiver garantit qu'aucune tâche ne soit silencieusement ralentie sur un PC de bureau.",
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff");
                    // Valeur absente = comportement par défaut de Windows = throttling actif, donc ce
                    // réglage ("désactiver le throttling") n'est pas appliqué.
                    return value == 1 ? TweakState.Enabled : TweakState.Disabled;
                },
                Apply = enable => RegistryHelper.WriteDword(RegistryHive.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", enable ? 1 : 0),
            },

            new()
            {
                Id = "network-throttling",
                Name = "Désactiver la limitation réseau multimédia",
                Category = "Réseau",
                Description = "Windows réserve par défaut de la bande passante réseau pour le multimédia (NetworkThrottlingIndex) et priorise moins les jeux (SystemResponsiveness). Désactiver aide en jeu compétitif à basse latence.",
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex");
                    // Valeur absente = limite par défaut de Windows (~10), donc "désactivé" comme 10 l'est explicitement.
                    return value == -1 || value == unchecked((int)0xFFFFFFFF) ? TweakState.Enabled : TweakState.Disabled;
                },
                Apply = enable =>
                {
                    RegistryHelper.WriteDword(RegistryHive.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "NetworkThrottlingIndex", enable ? unchecked((int)0xFFFFFFFF) : 10);
                    RegistryHelper.WriteDword(RegistryHive.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "SystemResponsiveness", enable ? 0 : 20);
                },
            },

            new()
            {
                Id = "fast-startup",
                Name = "Démarrage rapide (Hiberboot)",
                Category = "Démarrage",
                Description = "Le démarrage rapide accélère le boot mais peut causer des soucis avec le dual-boot, certains pilotes, ou empêcher un vrai redémarrage à froid du matériel. Le désactiver garantit un état matériel toujours propre au démarrage.",
                RequiresRestart = true,
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
                    // Ici "Enabled" = démarrage rapide actif (comportement par défaut de Windows,
                    // y compris quand la clé n'existe pas encore).
                    return value == 0 ? TweakState.Disabled : TweakState.Enabled;
                },
                Apply = enable => RegistryHelper.WriteDword(RegistryHive.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", enable ? 1 : 0),
            },

            new()
            {
                Id = "usb-selective-suspend",
                Name = "Désactiver la suspension sélective USB",
                Category = "Alimentation",
                Description = "Empêche Windows de mettre en veille les périphériques USB inactifs (souris/clavier gaming, DAC audio, contrôleurs). Évite les micro-latences au réveil.",
                GetState = () =>
                {
                    uint? value = _powerPlans.GetValueIndexAsync(PowerSubGroups.Usb, PowerSubGroups.UsbSelectiveSuspend)
                        .GetAwaiter().GetResult();
                    return value switch { 0 => TweakState.Enabled, > 0 => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => _powerPlans
                    .SetValueIndexAsync(PowerSubGroups.Usb, PowerSubGroups.UsbSelectiveSuspend, enable ? 0u : 1u)
                    .GetAwaiter().GetResult(),
            },

            new()
            {
                Id = "core-parking",
                Name = "Désactiver la mise en veille des cœurs CPU (core parking)",
                Category = "CPU",
                Description = "Force tous les cœurs à rester disponibles au lieu d'être parqués par Windows selon la charge. Utile pour des charges très en dents de scie (jeux avec pics CPU soudains).",
                GetState = () =>
                {
                    uint? value = _powerPlans.GetValueIndexAsync(PowerSubGroups.Processor, PowerSubGroups.ProcessorMinCoreParkingState)
                        .GetAwaiter().GetResult();
                    return value switch { 100 => TweakState.Enabled, not null => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => _powerPlans
                    .SetValueIndexAsync(PowerSubGroups.Processor, PowerSubGroups.ProcessorMinCoreParkingState, enable ? 100u : 5u)
                    .GetAwaiter().GetResult(),
            },

            new()
            {
                Id = "pcie-aspm",
                Name = "Désactiver l'économie d'énergie PCIe (ASPM)",
                Category = "Alimentation",
                Description = "L'ASPM met en veille légère le bus PCIe (GPU, NVMe) entre les pics d'activité. Le désactiver élimine de micro-latences au prix d'une consommation/chaleur un peu plus élevée en idle.",
                IsRisky = true,
                GetState = () =>
                {
                    uint? value = _powerPlans.GetValueIndexAsync(PowerSubGroups.PciExpress, PowerSubGroups.PciExpressAspm)
                        .GetAwaiter().GetResult();
                    return value switch { 0 => TweakState.Enabled, > 0 => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => _powerPlans
                    .SetValueIndexAsync(PowerSubGroups.PciExpress, PowerSubGroups.PciExpressAspm, enable ? 0u : 1u)
                    .GetAwaiter().GetResult(),
            },

            new()
            {
                Id = "core-isolation-info",
                Name = "Isolation du noyau / Intégrité de la mémoire (HVCI)",
                Category = "Sécurité ↔ Performances",
                Description = "Fonctionnalité de sécurité qui peut réduire les perf CPU de quelques % et bloque certains pilotes non signés récents (dont celui utilisé par le monitoring capteurs de cette app sur certaines cartes mères). Lecture seule ici — clique pour ouvrir le réglage Windows.",
                GetState = () =>
                {
                    int? value = RegistryHelper.ReadDword(RegistryHive.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
                    // Valeur absente = HVCI non configuré, donc inactif par défaut sur la plupart des configs.
                    return value == 1 ? TweakState.Enabled : TweakState.Disabled;
                },
                Apply = _ => Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:windowsdefender-deviceguard-hvcimarketing",
                    UseShellExecute = true,
                }),
            },
        };
    }
}
