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

    /// <summary>
    /// Écrit une valeur de sous-réglage d'alimentation, en mémorisant à la première activation celle qui
    /// était en place.
    ///
    /// Décocher rend alors exactement ce que le PC avait avant l'app, et pas une valeur par défaut
    /// supposée : les constructeurs livrent souvent leurs propres réglages d'alimentation, et « 5 % de
    /// cœurs parqués au minimum » n'est pas forcément ce que cette machine avait.
    /// </summary>
    private void ApplyPowerValue(string subGroup, string setting, bool enable, uint enabledValue, uint defaultValue)
    {
        string key = $"{subGroup}/{setting}";

        if (enable)
        {
            RememberOriginalValue(key, subGroup, setting);
            _powerPlans.SetValueIndexAsync(subGroup, setting, enabledValue).GetAwaiter().GetResult();
            return;
        }

        AppSettings settings = AppSettingsStore.Load();
        uint restored = settings.OriginalPowerValues.TryGetValue(key, out uint original) ? original : defaultValue;
        _powerPlans.SetValueIndexAsync(subGroup, setting, restored).GetAwaiter().GetResult();
    }

    /// <summary>Retient la valeur d'avant la première écriture, et elle seule : réécrire à chaque
    /// activation mémoriserait la valeur que l'app vient elle-même de poser.</summary>
    private void RememberOriginalValue(string key, string subGroup, string setting)
    {
        AppSettings settings = AppSettingsStore.Load();
        if (settings.OriginalPowerValues.ContainsKey(key)) return;

        if (_powerPlans.GetValueIndexAsync(subGroup, setting).GetAwaiter().GetResult() is not { } current) return;

        settings.OriginalPowerValues[key] = current;
        AppSettingsStore.Save(settings);
    }

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
                // HKCU : le réglage appartient au profil de l'utilisateur, pas à la machine.
                RequiresElevation = false,
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
                // HKCU, comme le Mode Jeu : aucune élévation nécessaire.
                RequiresElevation = false,
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
                    // 0xFFFFFFFF lu en DWORD signé vaut -1 : c'est la même valeur, écrite des deux façons
                    // selon les guides. Valeur absente = limite par défaut de Windows (~10), donc
                    // "désactivé", comme 10 l'est explicitement.
                    return value == -1 ? TweakState.Enabled : TweakState.Disabled;
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
                Description = "Empêche Windows de mettre en veille les périphériques USB inactifs (souris/clavier gaming, DAC audio, contrôleurs). Évite les micro-latences au réveil. Ne porte que sur le plan d'alimentation actif : changer de plan (dont activer « Performances ultimes », qui en crée un nouveau) le remet à sa valeur par défaut.",
                GetState = () =>
                {
                    uint? value = _powerPlans.GetValueIndexAsync(PowerSubGroups.Usb, PowerSubGroups.UsbSelectiveSuspend)
                        .GetAwaiter().GetResult();
                    return value switch { 0 => TweakState.Enabled, > 0 => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => ApplyPowerValue(
                    PowerSubGroups.Usb, PowerSubGroups.UsbSelectiveSuspend, enable, enabledValue: 0u, defaultValue: 1u),
            },

            new()
            {
                Id = "core-parking",
                Name = "Désactiver la mise en veille des cœurs CPU (core parking)",
                Category = "CPU",
                Description = "Force tous les cœurs à rester disponibles au lieu d'être parqués par Windows selon la charge. Utile pour des charges très en dents de scie (jeux avec pics CPU soudains). Ne porte que sur le plan d'alimentation actif : changer de plan (dont activer « Performances ultimes », qui en crée un nouveau) le remet à sa valeur par défaut.",
                GetState = () =>
                {
                    uint? value = _powerPlans.GetValueIndexAsync(PowerSubGroups.Processor, PowerSubGroups.ProcessorMinCoreParkingState)
                        .GetAwaiter().GetResult();
                    return value switch { 100 => TweakState.Enabled, not null => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => ApplyPowerValue(
                    PowerSubGroups.Processor, PowerSubGroups.ProcessorMinCoreParkingState, enable, enabledValue: 100u, defaultValue: 5u),
            },

            new()
            {
                Id = "pcie-aspm",
                Name = "Désactiver l'économie d'énergie PCIe (ASPM)",
                Category = "Alimentation",
                Description = "L'ASPM met en veille légère le bus PCIe (GPU, NVMe) entre les pics d'activité. Le désactiver élimine de micro-latences au prix d'une consommation/chaleur un peu plus élevée en idle. Ne porte que sur le plan d'alimentation actif : changer de plan (dont activer « Performances ultimes », qui en crée un nouveau) le remet à sa valeur par défaut.",
                IsRisky = true,
                GetState = () =>
                {
                    uint? value = _powerPlans.GetValueIndexAsync(PowerSubGroups.PciExpress, PowerSubGroups.PciExpressAspm)
                        .GetAwaiter().GetResult();
                    return value switch { 0 => TweakState.Enabled, > 0 => TweakState.Disabled, _ => TweakState.Unknown };
                },
                Apply = enable => ApplyPowerValue(
                    PowerSubGroups.PciExpress, PowerSubGroups.PciExpressAspm, enable, enabledValue: 0u, defaultValue: 1u),
            },

            new()
            {
                Id = "core-isolation-info",
                Name = "Isolation du noyau / Intégrité de la mémoire (HVCI)",
                Category = "Sécurité ↔ Performances",
                Description = "Fonctionnalité de sécurité qui peut réduire les perf CPU de quelques % et bloque certains pilotes non signés récents (dont celui utilisé par le monitoring capteurs de cette app sur certaines cartes mères). Lecture seule ici — clique pour ouvrir le réglage Windows.",
                // L'interrupteur n'écrit rien : il ouvre la page Windows, puis revient à l'état réel.
                IsReadOnly = true,
                RequiresElevation = false,
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
