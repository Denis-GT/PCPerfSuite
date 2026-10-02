using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce que les onglets Processeur, GPU et Ventilateurs affichent en tête, partagé par les trois et par la page Profils :
/// le bail de réglage tenu par un autre (« Réglages pilotés par X depuis HH:MM », commandes grisées, écriture manuelle
/// refusée), et le réglage d'un groupe en cours (« Régler dans l'onglet », puis « Mettre à jour le groupe »).
///
/// Le bail peut changer depuis n'importe quel thread : la bannière suit sur le fil d'interface. Une minuterie balaie le
/// bail toutes les 5 s, pour que la bannière tombe d'elle-même quand son détenteur meurt.
/// </summary>
public sealed partial class TuningStatusViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _sweep;

    public TuningStatusViewModel(TuningLease lease)
    {
        Lease = lease;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Lease.Changed += OnLeaseChanged;

        _sweep = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = SweepInterval };
        _sweep.Tick += (_, _) =>
        {
            Lease.Sweep();
            Refresh();
        };
        _sweep.Start();

        Refresh();
    }

    public TuningLease Lease { get; }

    /// <summary>Faux tant qu'un autre que l'utilisateur tient le bail : les commandes des onglets sont grisées.</summary>
    [ObservableProperty] private bool isManualTuningAllowed = true;

    /// <summary>« Réglages pilotés par le bench depuis 14:05 (mesure en cours). », null sans bail.</summary>
    [ObservableProperty] private string? leaseText;

    /// <summary>Le groupe en cours de réglage dans les onglets, null sinon.</summary>
    [ObservableProperty] private string? groupTuningText;

    /// <summary>« Mettre à jour le groupe » depuis l'onglet : posé par la page Profils pendant un réglage.</summary>
    [ObservableProperty] private IRelayCommand? updateGroupCommand;

    public bool ShowBanner => LeaseText is not null || GroupTuningText is not null;

    partial void OnLeaseTextChanged(string? value) => OnPropertyChanged(nameof(ShowBanner));

    partial void OnGroupTuningTextChanged(string? value) => OnPropertyChanged(nameof(ShowBanner));

    /// <summary>Pourquoi une écriture manuelle est refusée maintenant, null si elle est permise.</summary>
    public string? ManualWriteRefusal() => Lease.RefusalText(null);

    /// <summary>L'utilisateur vient de régler quelque chose à la main dans un onglet, ou d'appliquer un groupe d'un clic
    /// (sur le fil d'interface) : la bascule automatique (#9) se met en pause. Levé au geste, pas à l'écriture différée,
    /// pour qu'une bascule ne démarre pas entre les deux. Le booléen : un groupe appliqué à la main en état de démarrage.</summary>
    public event Action<string, bool>? ManualWrite;

    /// <summary>Signale un réglage manuel (<paramref name="source"/> : « réglage manuel dans l'onglet GPU »).</summary>
    /// <param name="makesStartupState">Un groupe appliqué à la main en en faisant l'état de démarrage des onglets.</param>
    public void NoteManualWrite(string source, bool makesStartupState = false)
    {
        try
        {
            ManualWrite?.Invoke(source, makesStartupState);
        }
        catch
        {
            // Un abonné en échec ne doit pas empêcher le réglage de l'utilisateur.
        }
    }

    private void OnLeaseChanged()
    {
        if (_dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }

        try { _dispatcher.BeginInvoke(Refresh); }
        catch { /* interface déjà fermée */ }
    }

    private void Refresh()
    {
        TuningLeaseHolder? holder = Lease.Holder;
        IsManualTuningAllowed = holder is null;
        LeaseText = holder?.Describe(Lease.UtcNow, Lease.LocalTimeZone);
    }

    public void Dispose()
    {
        _sweep.Stop();
        Lease.Changed -= OnLeaseChanged;
    }
}
