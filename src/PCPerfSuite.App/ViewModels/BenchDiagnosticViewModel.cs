using CommunityToolkit.Mvvm.ComponentModel;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Un sous-onglet de la page Bench et diagnostic.</summary>
public sealed record BenchDiagnosticSection(string Key, string Title);

/// <summary>
/// Page « Bench et diagnostic » (Diagnostiquer) : trois sous-onglets en pastilles. « Bench » est livré par #10
/// (<see cref="BenchViewModel"/>) ; « Diagnostic » (#12) et « Technicien » (#13) attendent, avec leur texte. Le
/// sous-onglet Bench ne relève en direct que page affichée et pastille choisie (modèle : Paramètres › Compatibilité).
/// </summary>
public sealed partial class BenchDiagnosticViewModel : ObservableObject, IPageLifecycle, IDisposable
{
    public const string BenchKey = "bench";
    public const string DiagnosticKey = "diagnostic";
    public const string TechnicianKey = "technician";

    public IReadOnlyList<BenchDiagnosticSection> Sections { get; } =
    [
        new BenchDiagnosticSection(BenchKey, "Bench"),
        new BenchDiagnosticSection(DiagnosticKey, "Diagnostic"),
        new BenchDiagnosticSection(TechnicianKey, "Technicien"),
    ];

    [ObservableProperty] private BenchDiagnosticSection? selectedSection;

    [ObservableProperty] private bool isPageShown;

    public BenchDiagnosticViewModel(BenchViewModel bench)
    {
        Bench = bench;
        selectedSection = Sections[0];
    }

    public BenchViewModel Bench { get; }

    public ComingSoonViewModel Diagnostic { get; } = new(
        "Diagnostic",
        "Dire ce qui est normal, anormal ou très bien sur ce PC, et pourquoi, à partir des mesures du bench et des signaux relevés.",
        new[]
        {
            "Verdict par composant, comparé à une table de référence de machines semblables.",
            "Causes : bridage thermique ou de puissance, refroidissement, alimentation, réglages Windows.",
            "Rapport à partager, sans donnée personnelle.",
        });

    public ComingSoonViewModel Technician { get; } = new(
        "Technicien",
        "Un parcours guidé pour un PC confié en atelier : inventaire, bench, diagnostic et compte rendu.",
        new[]
        {
            "Inventaire du matériel et des pilotes, bench et diagnostic enchaînés.",
            "Compte rendu rédigé à partir des constats (une IA facultative rédige, elle ne décide de rien).",
        });

    partial void OnIsPageShownChanged(bool value) => UpdateShown();

    partial void OnSelectedSectionChanged(BenchDiagnosticSection? value) => UpdateShown();

    /// <summary>Le sous-onglet Bench est « sous les yeux » seulement si la page l'est ET qu'il est la pastille choisie.
    /// SelectedSection peut être null : Ctrl+clic sur la pastille active la désélectionne.</summary>
    private void UpdateShown() => Bench.IsPageShown = IsPageShown && SelectedSection?.Key == BenchKey;

    public void Dispose() => Bench.Dispose();
}
