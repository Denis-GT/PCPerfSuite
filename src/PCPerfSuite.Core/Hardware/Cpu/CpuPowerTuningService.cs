using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>Un choix possible pour un réglage à liste.</summary>
public sealed record CpuPowerChoice(uint Value, string Label);

/// <summary>Un réglage processeur du plan d'alimentation Windows actif.</summary>
public sealed class CpuPowerSetting
{
    public required string Id { get; init; }
    public required Guid Guid { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }

    /// <summary>Valeurs possibles pour un réglage à liste, null pour un réglage numérique.</summary>
    public IReadOnlyList<CpuPowerChoice>? Choices { get; init; }

    public uint Min { get; init; }
    public uint Max { get; init; } = 100;
    public string Unit { get; init; } = "";

    /// <summary>Ce que veut dire la valeur 0 quand elle est particulière ("Illimitée" pour une fréquence).</summary>
    public string? ZeroLabel { get; init; }
}

/// <summary>
/// Réglages d'alimentation du processeur, la seule voie disponible sur les trois plateformes — y
/// compris Snapdragon, où tout le reste est verrouillé par le firmware. Aucun pilote, aucun risque
/// matériel : ce sont les réglages que Windows expose déjà, appliqués au plan d'alimentation actif.
///
/// Tout passe par l'API powrprof plutôt que par l'exécutable powercfg, pour deux raisons : la moitié de
/// ces réglages sont masqués par défaut et n'apparaissent pas dans la sortie de powercfg, et cette
/// sortie est traduite dans la langue de Windows, donc impossible à analyser de façon fiable.
///
/// Chaque réglage a une valeur "sur secteur" et une valeur "sur batterie" ; sur une machine sans
/// batterie, seule la première a un sens.
/// </summary>
public sealed class CpuPowerTuningService
{
    /// <summary>Sous-groupe "Gestion de l'alimentation du processeur", identique sur tous les Windows.</summary>
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid MinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid MaxFrequency = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    private static readonly Guid EnergyPreference = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

    // Mêmes réglages appliqués à la classe de cœurs la plus performante (P-cores Intel, cœurs "prime"
    // des Snapdragon X) : leur GUID est celui du réglage général avec le dernier chiffre incrémenté.
    private static readonly Guid MaxFrequencyPerfCores = new("75b0ae3f-bce0-45a7-8c89-c9611c25e101");
    private static readonly Guid EnergyPreferencePerfCores = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");

    private readonly CpuPlatform _platform;

    public CpuPowerTuningService(CpuPlatform platform) => _platform = platform;

    /// <summary>Vrai si la machine a une batterie : l'interface propose alors les deux valeurs.</summary>
    public bool HasBattery => _platform.HasBattery;

    public IReadOnlyList<CpuPowerSetting> GetSettings()
    {
        var settings = new List<CpuPowerSetting>
        {
            new()
            {
                Id = "boost",
                Guid = BoostMode,
                Label = "Mode boost",
                Description = "Comment le processeur a le droit de dépasser sa fréquence de base. « Désactivé » le fige à sa fréquence de base : c'est le réglage qui fait le plus baisser la chauffe, au prix des performances en pointe.",
                Choices =
                [
                    new(0, "Désactivé"),
                    new(1, "Activé"),
                    new(2, "Agressif"),
                    new(3, "Efficace"),
                    new(4, "Efficace agressif"),
                ],
            },
            new()
            {
                Id = "max-state",
                Guid = MaxState,
                Label = "État maximal",
                Description = "Plafond de fréquence, en pourcentage de la fréquence de base. En dessous de 100 %, le processeur ne boostera jamais.",
                Min = 5,
                Max = 100,
                Unit = "%",
            },
            new()
            {
                Id = "min-state",
                Guid = MinState,
                Label = "État minimal",
                Description = "Plancher de fréquence. Le laisser bas permet au processeur de redescendre au repos, ce qui économise batterie et chauffe.",
                Min = 0,
                Max = 100,
                Unit = "%",
            },
            new()
            {
                Id = "max-frequency",
                Guid = MaxFrequency,
                Label = "Fréquence maximale",
                Description = "Plafond de fréquence en mégahertz, plus direct que le pourcentage ci-dessus. 0 laisse le processeur libre.",
                Min = 0,
                Max = 6000,
                Unit = " MHz",
                ZeroLabel = "Illimitée",
            },
            new()
            {
                Id = "epp",
                Guid = EnergyPreference,
                Label = "Préférence performance / économie",
                Description = "Arbitrage confié au processeur : 0 privilégie la performance, 100 l'économie d'énergie. N'a d'effet que sur les processeurs qui gèrent cet arbitrage (Intel Speed Shift, AMD CPPC, Snapdragon).",
                Min = 0,
                Max = 100,
            },
        };

        // Sur un processeur hybride, les mêmes réglages existent pour les cœurs rapides seuls : les
        // afficher ailleurs n'aurait pas de sens, ils n'y font rien.
        if (_platform.IsHybrid)
        {
            settings.Add(new CpuPowerSetting
            {
                Id = "max-frequency-perf",
                Guid = MaxFrequencyPerfCores,
                Label = "Fréquence maximale (cœurs rapides)",
                Description = "Même plafond, appliqué aux seuls cœurs performants (P-cores).",
                Min = 0,
                Max = 6000,
                Unit = " MHz",
                ZeroLabel = "Illimitée",
            });

            settings.Add(new CpuPowerSetting
            {
                Id = "epp-perf",
                Guid = EnergyPreferencePerfCores,
                Label = "Préférence performance / économie (cœurs rapides)",
                Description = "Même arbitrage, appliqué aux seuls cœurs performants.",
                Min = 0,
                Max = 100,
            });
        }

        return settings;
    }

    /// <summary>Lit les valeurs secteur et batterie du réglage. False si Windows ne connaît pas ce
    /// réglage sur cette machine — il n'y a alors rien à afficher, et surtout rien à écrire.</summary>
    public bool TryRead(CpuPowerSetting setting, out uint onAc, out uint onBattery)
    {
        onAc = 0;
        onBattery = 0;

        IntPtr scheme = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0) return false;

            Guid schemeGuid = Marshal.PtrToStructure<Guid>(scheme);
            Guid sub = SubProcessor;
            Guid id = setting.Guid;

            if (PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref id, out onAc) != 0) return false;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref id, out onBattery) != 0) return false;

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (scheme != IntPtr.Zero) LocalFree(scheme);
        }
    }

    /// <summary>
    /// Écrit les deux valeurs puis réactive le plan : sans cette réactivation, Windows enregistre le
    /// réglage mais ne l'applique qu'au prochain changement de plan.
    /// </summary>
    public bool TryWrite(CpuPowerSetting setting, uint onAc, uint onBattery)
    {
        uint ac = Math.Clamp(onAc, setting.Min, setting.Max);
        uint dc = Math.Clamp(onBattery, setting.Min, setting.Max);

        IntPtr scheme = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0) return false;

            Guid schemeGuid = Marshal.PtrToStructure<Guid>(scheme);
            Guid sub = SubProcessor;
            Guid id = setting.Guid;

            if (PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref id, ac) != 0) return false;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref id, dc) != 0) return false;

            return PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (scheme != IntPtr.Zero) LocalFree(scheme);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
