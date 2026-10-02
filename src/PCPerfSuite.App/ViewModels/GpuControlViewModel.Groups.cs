using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce que l'onglet GPU expose aux groupes de profils (<see cref="IGpuGroupTarget"/>) : il relit la carte, pose une demande
/// déjà décidée (<see cref="GpuGroupPlanner"/>) par <see cref="GpuControlService.ApplyAndVerify"/> et rend ce que la
/// carte a retenu. L'onglet reste seul propriétaire de sa partie de settings.json et de sa sécurité thermique. Aucun
/// statut d'onglet ni boîte de dialogue ici : c'est l'appelant qui dit le résultat.
/// </summary>
public sealed partial class GpuControlViewModel : IGpuGroupTarget
{
    /// <summary>L'overclock en place vient d'un groupe appliqué sans en faire l'état de démarrage : il n'est pas
    /// enregistré, et il est rendu d'origine à la fermeture (D7). Retombe à faux dès qu'on le touche à la main.</summary>
    private bool _overclockTransient;

    /// <summary>Bail de réglage et réglage d'un groupe en cours : la bannière de l'onglet.</summary>
    public TuningStatusViewModel Tuning { get; }

    /// <summary>L'onglet vient de relire la carte après un changement qui ne vient pas de lui (réveil de veille,
    /// sécurité thermique) : la page Profils revérifie alors son groupe actif.</summary>
    public event Action? HardwareResynced;

    public ProfileDimension Dimension => ProfileDimension.Gpu;

    public bool IsRaised => IsAvailable && GpuOverclockRaise.IsRaised(_gpuControl.GetOverclock(), _gpuControl.GetSnapshot());

    /// <summary>La sécurité thermique a retiré l'overclock pendant cette session.</summary>
    public bool EmergencyThisSession => _emergencyThisSession;

    /// <summary>D7 : seul l'overclock dont « Appliquer au démarrage » est coché reste en place à la fermeture, et un état
    /// transitoire n'en fait pas partie.</summary>
    private void UpdateKeepOverclockOnExit() => _gpuControl.KeepOverclockOnExit = ApplyOverclockAtStartup && !_overclockTransient;

    /// <summary>Une écriture à la main : refusée sous le bail d'un autre (l'affichage revient alors à ce que la carte a
    /// vraiment), sinon elle redevient l'état de démarrage, même après un groupe appliqué sans l'être.</summary>
    private bool RefuseManualWrite()
    {
        if (Tuning.ManualWriteRefusal() is { } refusal)
        {
            LoadPowerLimit();
            LoadOverclock();
            OverclockStatus = $"Modification refusée : {refusal}";
            return true;
        }

        _overclockTransient = false;
        UpdateKeepOverclockOnExit();
        return false;
    }

    public void FlushPendingManualWrites() => _applyDebounce.Flush();

    /// <summary>Un geste de l'utilisateur dans l'onglet (curseur, champ, bouton) : la bascule automatique se met en
    /// pause. Jamais pour une relecture, une réapplication au lancement ou au réveil, ni un groupe.</summary>
    private void NoteManualEdit() => Tuning.NoteManualWrite("réglage manuel dans l'onglet GPU");

    public GpuTargetState ReadState()
        => new(IsAvailable, IsAvailable ? null : UnavailableMessage, CanOverclock, _gpuControl.Identity,
            IsAvailable ? _gpuControl.GetOverclock() : null, IsAvailable ? _gpuControl.GetSnapshot() : null, _emergencyThisSession);

    public string Describe(GpuOverclockProfile profile) => new GpuProfileViewModel(profile).Summary;

    public GpuApplyOutcome Apply(GpuGroupPlan plan, ProfileGroupApplyContext context)
    {
        var items = new List<ReportItem>(plan.Items);

        if (plan.Request is { } request)
        {
            // Une consigne de curseur encore en attente écraserait le groupe juste après.
            CancelPendingApplies();
            GpuApplyReport report = _gpuControl.ApplyAndVerify(request);
            items.AddRange(report.Items.Select(ToReportItem));
            AfterGroupWrite(context);
        }
        else if (plan.RestoreOrigin)
        {
            CancelPendingApplies();
            _gpuControl.RestoreOverclockDefaults();
            GpuOverclockSnapshot? overclock = _gpuControl.GetOverclock();
            GpuControlSnapshot? power = _gpuControl.GetSnapshot();
            string readBack = GpuOverclockRaise.DescribeReadBack(overclock, power);
            items.Add(GpuOverclockRaise.IsRaised(overclock, power)
                ? new ReportItem(DimensionReport.Title(ProfileDimension.Gpu), ReportItemStatus.Trimmed,
                    $"retour d'origine demandé, mais la carte garde un réglage relevé : {readBack}")
                : ReportItem.Applied(DimensionReport.Title(ProfileDimension.Gpu), $"réglages d'origine reposés : {readBack}"));
            AfterGroupWrite(context);
        }

        GpuRetainedValues retained = plan.Retain(ReadState().ToRetained());
        return new GpuApplyOutcome(new DimensionReport(ProfileDimension.Gpu, items, plan.Notes), retained);
    }

    /// <summary>Après une écriture d'un groupe : l'affichage suit la carte, et l'enregistrement suit l'option « état de
    /// démarrage ».</summary>
    private void AfterGroupWrite(ProfileGroupApplyContext context)
    {
        LoadPowerLimit();
        LoadOverclock();
        _overclockTransient = !context.MakeStartupState;
        UpdateKeepOverclockOnExit();
        Persist();
    }

    private static ReportItem ToReportItem(GpuApplyItem item) => item.Status switch
    {
        GpuApplyStatus.Retained => ReportItem.Applied(item.Label, item.Describe()),
        GpuApplyStatus.Trimmed => new ReportItem(item.Label, ReportItemStatus.Trimmed, item.Describe()),
        GpuApplyStatus.Refused => ReportItem.Refused(item.Label, item.Describe()),
        _ => new ReportItem(item.Label, ReportItemStatus.NotReadBack, item.Describe()),
    };

    /// <summary>
    /// Un profil de l'onglet, par le même chemin qu'un groupe, sans contrôle d'identité (il n'en porte pas) et en faisant
    /// l'état de démarrage, comme avant. Les limites de puissance et de température absentes du profil sont reposées aux
    /// valeurs affichées, comme avant ; dans un groupe, une limite absente veut dire « ne pas toucher ».
    /// </summary>
    private string ApplyTabProfile(GpuOverclockProfile model)
    {
        if (Tuning.ManualWriteRefusal() is { } refusal) return $"non appliqué : {refusal}";

        NoteManualEdit();
        FlushPendingManualWrites();
        var withLimits = new GpuOverclockProfile
        {
            Name = model.Name,
            CoreClockOffsetMhz = model.CoreClockOffsetMhz,
            MemoryClockOffsetMhz = model.MemoryClockOffsetMhz,
            PowerLimitPercent = model.PowerLimitPercent ?? (IsPowerLimitSupported ? (float)PowerLimitPercent : null),
            TemperatureLimitC = model.TemperatureLimitC ?? (IsTemperatureLimitSupported ? (int)Math.Round(TemperatureLimitC) : null),
            VoltageValue = model.VoltageValue,
            VoltageUnit = model.VoltageUnit,
            VoltageBoostPercent = model.VoltageBoostPercent,
        };

        GpuGroupPlan plan = GpuGroupPlanner.Plan(ProfileGroupEditor.GpuValues(withLimits, _gpuControl.Identity), ReadState(),
            isManual: true, checkIdentity: false);
        GpuApplyOutcome outcome = Apply(plan, new ProfileGroupApplyContext(ProfileGroupRequesters.Tab, true, true, null));
        return outcome.Report.Body;
    }

    /// <summary>Après une relecture au réveil ou une sécurité thermique.</summary>
    private void RaiseHardwareResynced() => HardwareResynced?.Invoke();
}
