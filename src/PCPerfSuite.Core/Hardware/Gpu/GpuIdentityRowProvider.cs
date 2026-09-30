using PCPerfSuite.Core.Compatibility;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Diagnostic « Carte GPU pilotée » : l'identité de la carte (<see cref="GpuIdentity"/>) et, quand un overclock est
/// enregistré, s'il a été fait sur cette carte. C'est ce qui explique qu'« Appliquer au démarrage » ne réapplique rien
/// après un changement de carte.
/// </summary>
public sealed class GpuIdentityRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Carte GPU pilotée";

    private readonly GpuControlService _gpu;

    /// <summary>Réglages GPU relus par <see cref="RefreshAsync"/> (fichier) ; null tant que non lus.</summary>
    private GpuControlSettings? _saved;

    public GpuIdentityRowProvider(GpuControlService gpu) => _gpu = gpu;

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _saved, AppSettingsStore.Load().Gpu);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows() => [BuildRow(_gpu.Identity, Volatile.Read(ref _saved))];

    /// <summary>La ligne, isolée ici pour être testée sans carte. Un fichier d'avant n'a que la marque (null = NVIDIA).</summary>
    public static CompatibilityRow BuildRow(GpuIdentity? current, GpuControlSettings? saved)
    {
        if (current is null)
            return new CompatibilityRow(RowTitle, "Aucune", "Aucun GPU pilotable : rien n'est identifié.", true);

        string detail = $"{current.Describe()}.";
        if (saved is null) return new CompatibilityRow(RowTitle, current.Name ?? current.Vendor.ToString(), detail, true);

        GpuIdentity savedIdentity = saved.OverclockGpu ?? new GpuIdentity(saved.OverclockVendor ?? GpuVendor.Nvidia);
        bool matches = GpuIdentity.Matches(savedIdentity, current);
        if (!saved.ApplyOverclockAtStartup)
        {
            detail += " « Appliquer au démarrage » n'est pas coché : rien n'est réappliqué au lancement.";
            return new CompatibilityRow(RowTitle, current.Name ?? current.Vendor.ToString(), detail, true);
        }

        detail += matches
            ? " L'overclock enregistré a été fait sur cette carte : il est réappliqué au lancement."
            : $" L'overclock enregistré a été fait sur une autre carte ({savedIdentity.Describe()}) : il n'est pas réappliqué.";
        return new CompatibilityRow(RowTitle, matches ? "Réglages de cette carte" : "Réglages d'une autre carte", detail, matches);
    }
}
