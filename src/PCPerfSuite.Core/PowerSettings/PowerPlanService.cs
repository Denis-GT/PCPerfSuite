using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PCPerfSuite.Core.PowerSettings;

/// <summary>
/// Pilote powercfg.exe. La sortie de powercfg est traduite selon la langue de Windows
/// (français inclus) : tout le parsing ci-dessous s'appuie donc uniquement sur des motifs
/// indépendants de la langue (GUID, valeurs hexadécimales, astérisque "actif"), jamais sur
/// des libellés anglais qui casseraient sur un Windows en français.
/// </summary>
public sealed partial class PowerPlanService
{
    // GUID interne fixe du plan "Performances ultimes", masqué par défaut sur la plupart des installations Windows.
    private const string UltimatePerformanceSourceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public sealed record PowerScheme(string Guid, string Name, bool IsActive);

    public async Task<IReadOnlyList<PowerScheme>> ListSchemesAsync()
    {
        string output = await RunPowercfgAsync("/list");
        var schemes = new List<PowerScheme>();

        foreach (Match m in SchemeLineRegex().Matches(output))
        {
            schemes.Add(new PowerScheme(
                Guid: m.Groups["guid"].Value.ToLowerInvariant(),
                Name: m.Groups["name"].Value.Trim(),
                IsActive: m.Groups["active"].Success));
        }

        return schemes;
    }

    /// <summary>Duplique et active le plan "Performances ultimes" s'il n'a pas déjà été créé par cette app, puis l'active.</summary>
    public async Task<string> EnableUltimatePerformanceAsync()
    {
        AppSettings settings = AppSettingsStore.Load();
        IReadOnlyList<PowerScheme> existing = await ListSchemesAsync();

        if (settings.UltimatePerformanceGuid is not null &&
            existing.Any(s => s.Guid == settings.UltimatePerformanceGuid))
        {
            await RunPowercfgAsync($"-setactive {settings.UltimatePerformanceGuid}");
            return settings.UltimatePerformanceGuid;
        }

        string output = await RunPowercfgAsync($"-duplicatescheme {UltimatePerformanceSourceGuid}");
        Match m = GuidRegex().Match(output);
        if (!m.Success)
            throw new InvalidOperationException("powercfg n'a pas retourné de GUID pour le nouveau plan.");

        string guid = m.Value.ToLowerInvariant();
        await RunPowercfgAsync($"-setactive {guid}");

        settings.UltimatePerformanceGuid = guid;
        AppSettingsStore.Save(settings);
        return guid;
    }

    /// <summary>
    /// Vrai si le plan actif est "Performances ultimes". On vérifie d'abord le GUID mémorisé par
    /// cette app ; mais comme ce plan est masqué et ne peut être obtenu qu'en dupliquant le schéma
    /// source (ce qui génère un GUID différent à chaque duplication, y compris si l'utilisateur ou
    /// un autre outil l'a activé avant ou en dehors de cette app), on retombe sur une détection par
    /// élimination : si le plan actif n'est aucun des 3 plans standards de Windows, c'est forcément
    /// une copie de Performances ultimes. On mémorise alors son GUID pour la prochaine fois.
    /// </summary>
    public async Task<bool> IsUltimatePerformanceActiveAsync()
    {
        IReadOnlyList<PowerScheme> schemes = await ListSchemesAsync();
        PowerScheme? active = schemes.FirstOrDefault(s => s.IsActive);
        if (active is null) return false;

        AppSettings settings = AppSettingsStore.Load();
        if (active.Guid == settings.UltimatePerformanceGuid) return true;

        if (!WellKnownSchemeGuids.Standard.Contains(active.Guid))
        {
            settings.UltimatePerformanceGuid = active.Guid;
            AppSettingsStore.Save(settings);
            return true;
        }

        return false;
    }

    public async Task SetActiveSchemeAsync(string guid) => await RunPowercfgAsync($"-setactive {guid}");

    /// <summary>Règle une valeur de sous-réglage d'alimentation (AC et DC) puis réapplique le plan actif.</summary>
    public async Task SetValueIndexAsync(string subGroupGuid, string settingGuid, uint value)
    {
        await RunPowercfgAsync($"/setacvalueindex scheme_current {subGroupGuid} {settingGuid} {value}");
        await RunPowercfgAsync($"/setdcvalueindex scheme_current {subGroupGuid} {settingGuid} {value}");
        await RunPowercfgAsync("/S scheme_current");
    }

    /// <summary>Lit la valeur AC courante d'un sous-réglage. On prend la 1ère valeur hexadécimale de la sortie
    /// (toujours l'index AC en 1er, quelle que soit la langue).</summary>
    public async Task<uint?> GetValueIndexAsync(string subGroupGuid, string settingGuid)
    {
        string output = await RunPowercfgAsync($"/query scheme_current {subGroupGuid} {settingGuid}");
        Match m = HexValueRegex().Match(output);
        if (!m.Success) return null;
        return Convert.ToUInt32(m.Groups["hex"].Value, 16);
    }

    private static async Task<string> RunPowercfgAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de démarrer powercfg.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"powercfg {arguments} a échoué ({process.ExitCode}): {stderr}");

        return stdout;
    }

    // "<guid> (<nom localisé>) *" — l'astérisque marque le plan actif, ni le GUID ni l'astérisque ne sont traduits.
    [GeneratedRegex(@"(?<guid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((?<name>[^)]*)\)\s*(?<active>\*)?")]
    private static partial Regex SchemeLineRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"0x(?<hex>[0-9a-fA-F]{1,8})")]
    private static partial Regex HexValueRegex();
}

/// <summary>Les 3 GUID des plans d'alimentation standards livrés par Windows (fixes, jamais localisés).</summary>
public static class WellKnownSchemeGuids
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static readonly IReadOnlySet<string> Standard = new HashSet<string>
    {
        Balanced, HighPerformance, PowerSaver,
    };
}

/// <summary>GUID connus des sous-réglages d'alimentation Windows, valables quelle que soit la langue du système.</summary>
public static class PowerSubGroups
{
    public const string Usb = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public const string Processor = "54533251-82be-4824-96c1-47b60b740d00";
    public const string ProcessorMinCoreParkingState = "0cc5b647-c1df-4637-891a-dec35c318583";

    public const string PciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string PciExpressAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
}
