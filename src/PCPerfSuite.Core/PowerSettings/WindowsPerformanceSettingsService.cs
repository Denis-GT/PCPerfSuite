using System.Diagnostics;
using Microsoft.Win32;
using PCPerfSuite.Core.Hardware.Cpu.CoreParking;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.PowerSettings;

/// <summary>
/// Catalogue des réglages de performance Windows, y compris ceux masqués dans l'interface
/// standard (plan d'alimentation "Performances ultimes", planification GPU matérielle, etc.).
/// Toutes les écritures HKLM nécessitent que l'app tourne en administrateur (voir app.manifest).
/// </summary>
public sealed class WindowsPerformanceSettingsService
{
    private readonly PowerPlanService _powerPlans = new();

    /// <summary>Le tweak « core-parking » en est une façade : même origine, même registre des modifications, et les
    /// cœurs performants des processeurs hybrides (CPMINCORES1) avec.</summary>
    private readonly CoreParkingService _coreParking;

    public WindowsPerformanceSettingsService(CoreParkingService coreParking) => _coreParking = coreParking;

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

        // Secteur et batterie peuvent avoir des valeurs d'origine différentes sur un portable : les clés
        // "/ac" et "/dc" les gardent séparées. La clé sans suffixe (avant ce correctif) ne portait que la
        // valeur secteur ; encore présente sur une machine mise à jour, elle sert de repli pour les deux
        // tant que le réglage n'a pas été réactivé depuis.
        AppSettings settings = AppSettingsStore.Load();
        uint legacy = settings.OriginalPowerValues.TryGetValue(key, out uint legacyValue) ? legacyValue : defaultValue;
        uint restoredAc = settings.OriginalPowerValues.TryGetValue($"{key}/ac", out uint ac) ? ac : legacy;
        uint restoredDc = settings.OriginalPowerValues.TryGetValue($"{key}/dc", out uint dc) ? dc : legacy;
        _powerPlans.SetValueIndicesAsync(subGroup, setting, restoredAc, restoredDc).GetAwaiter().GetResult();
    }

    /// <summary>Retient les valeurs secteur et batterie d'avant la première écriture, et elles seules :
    /// réécrire à chaque activation mémoriserait la valeur que l'app vient elle-même de poser.</summary>
    private void RememberOriginalValue(string key, string subGroup, string setting)
    {
        if (AppSettingsStore.Load().OriginalPowerValues.ContainsKey($"{key}/ac")) return;

        // powercfg est lu avant le verrou : le lambda d'Update ne fait que des affectations, et ne réécrit pas une
        // origine notée entre-temps.
        if (_powerPlans.GetValueIndicesAsync(subGroup, setting).GetAwaiter().GetResult() is not { } current) return;

        bool saved = AppSettingsStore.TryUpdate(settings =>
        {
            if (!settings.OriginalPowerValues.TryAdd($"{key}/ac", current.Ac)) return;
            settings.OriginalPowerValues[$"{key}/dc"] = current.Dc;
        });

        // Sans origine sur le disque, décocher ne pourrait plus rendre ce que le PC avait : on n'écrit rien.
        if (!saved) throw new InvalidOperationException(AppSettingsStore.LastError ?? "Valeur d'origine non enregistrée.");
    }

    /// <summary>Mémorise le plan actif juste avant d'activer « Performances ultimes », et lui seul : une
    /// activation déjà faite ne doit pas écraser le plan d'origine par « Performances ultimes »
    /// lui-même si l'utilisateur active/désactive plusieurs fois.</summary>
    private void RememberActiveSchemeBeforeUltimate()
    {
        if (_powerPlans.IsUltimatePerformanceActiveAsync().GetAwaiter().GetResult()) return;

        IReadOnlyList<PowerPlanService.PowerScheme> schemes = _powerPlans.ListSchemesAsync().GetAwaiter().GetResult();
        string? active = schemes.FirstOrDefault(s => s.IsActive)?.Guid;
        if (active is null) return;

        AppSettingsStore.Update(settings => settings.PreUltimatePerformanceSchemeGuid = active);
    }

    /// <summary>Planchers du parking que le tweak met à 100 % : celui des cœurs efficaces (ou de tous les cœurs), et
    /// celui des cœurs performants sur un processeur hybride.</summary>
    private IReadOnlyList<CoreParkingSetting> CoreParkingFloors => _coreParking.Settings.Where(s => s.IsCoreCount && s.IsMinimum).ToList();

    /// <summary>Activé quand chaque plancher est à 100 % sur secteur, lu par powrprof : powercfg ne montre pas ces
    /// réglages masqués et rendait « inconnu ».</summary>
    private TweakState GetCoreParkingState()
    {
        List<CoreParkingValue?> values = CoreParkingFloors.Select(_coreParking.Read).ToList();
        if (values.Any(v => v is null)) return TweakState.Unknown;
        return values.All(v => v!.Value.Ac == 100) ? TweakState.Enabled : TweakState.Disabled;
    }

    /// <summary>Activer pose 100 % sur secteur et sur batterie ; désactiver rend l'origine notée, avec le repli des
    /// premières versions (5 %) pour CPMINCORES quand aucune ne l'a été.</summary>
    private void ApplyCoreParking(bool enable)
    {
        if (enable)
        {
            CoreParkingWriteResult result = _coreParking.Write(CoreParkingFloors
                .Select(s => new CoreParkingTarget(s, new CoreParkingValue(100, 100))).ToList());
            if (!result.Succeeded) throw new InvalidOperationException(result.Error ?? "Windows a refusé le réglage.");
            return;
        }

        SystemRestoreResult restored = _coreParking.Restore(CoreParkingFloors, fallbackMinCores: 5);
        if (restored.Status is SystemRestoreStatus.Failed or SystemRestoreStatus.Partial)
        {
            throw new InvalidOperationException(restored.Message ?? "Les valeurs d'origine n'ont pas pu être rendues.");
        }

        // L'origine notée était celle d'un autre plan (rendu à l'instant), ou le plan actif a de lui-même ses planchers à
        // 100 % (« Performances ultimes ») : l'interrupteur ne doit pas se croire décoché.
        if (GetCoreParkingState() == TweakState.Enabled)
        {
            string other = restored.Status == SystemRestoreStatus.Restored ? " (un autre plan a, lui, retrouvé ses valeurs)" : "";
            throw new InvalidOperationException(
                $"Le plan d'alimentation actif garde tous ses cœurs actifs : PCPerfSuite n'a pas d'origine à lui rendre{other}. Règle-le dans Processeur › Cœurs.");
        }
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
                        RememberActiveSchemeBeforeUltimate();
                        _powerPlans.EnableUltimatePerformanceAsync().GetAwaiter().GetResult();
                    }
                    else
                    {
                        // Revient sur le plan actif avant l'activation (mémorisé ci-dessous), pas
                        // systématiquement "Équilibré" : un plan OEM ou personnalisé actif avant coup
                        // était sinon remplacé par un plan que l'utilisateur n'avait pas choisi.
                        AppSettings settings = AppSettingsStore.Load();
                        string restore = settings.PreUltimatePerformanceSchemeGuid ?? WellKnownSchemeGuids.Balanced;
                        _powerPlans.SetActiveSchemeAsync(restore).GetAwaiter().GetResult();
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
                TargetsUserProfile = true,
                Apply = enable => RegistryHelper.WriteDword(RegistryHive.CurrentUser,
                    @"Software\Microsoft\GameBar", "AutoGameModeEnabled", enable ? 1 : 0),
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
                Description = "Force tous les cœurs à rester disponibles au lieu d'être parqués par Windows selon la charge, cœurs performants des processeurs hybrides compris (CPMINCORES et CPMINCORES1 à 100 %, sur secteur comme sur batterie ; sur un hybride, il n'apparaît activé que si les deux le sont). Utile pour des charges très en dents de scie (jeux avec pics CPU soudains), au prix d'une chauffe et d'une consommation plus élevées au repos. Réglage plus fin et visuel par cœur : Processeur › Cœurs. Ne porte que sur le plan d'alimentation actif : changer de plan (dont activer « Performances ultimes », qui en crée un nouveau) le remet à sa valeur par défaut, et les outils du fabricant (Armoury Crate, Vantage…) peuvent l'écraser.",
                GetState = GetCoreParkingState,
                Apply = ApplyCoreParking,
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
