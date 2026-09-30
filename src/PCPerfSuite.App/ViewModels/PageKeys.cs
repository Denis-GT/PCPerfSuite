namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Clés stables des pages : jamais affichées ni traduites, elles relient une entrée du menu (<see cref="NavigationMenu"/>)
/// à sa vue dans MainWindow.xaml (ConverterParameter={x:Static vm:PageKeys.X}). Un titre peut changer ; une clé,
/// jamais : c'était la comparaison des titres qui cassait une page en silence à chaque renommage.
///
/// Les pages à venir ont déjà la leur (placement : docs/navigation.md) : la conversation qui livre la page la reprend
/// telle quelle.
/// </summary>
public static class PageKeys
{
    // Surveiller
    public const string Monitoring = "monitoring";
    public const string Processes = "processes";
    public const string Overlay = "overlay";

    // Régler
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Fans = "fans";
    public const string Profiles = "profiles";
    public const string AutoOverclock = "auto-overclock";
    public const string Displays = "displays";
    public const string Lighting = "lighting";
    public const string LaptopGpu = "laptop-gpu";

    // Diagnostiquer
    public const string BenchDiagnostic = "bench-diagnostic";

    // Outils
    public const string Optimization = "optimization";
    public const string Cleanup = "cleanup";
    public const string Storage = "storage";
    public const string Devices = "devices";
    public const string Toolbox = "toolbox";
    public const string Memory = "memory";

    // Pied de la barre latérale, hors de la liste
    public const string Settings = "settings";
}
