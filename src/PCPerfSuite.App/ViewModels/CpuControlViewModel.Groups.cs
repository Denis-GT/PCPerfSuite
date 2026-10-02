using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Profiles;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce que l'onglet Processeur expose aux groupes de profils (<see cref="ICpuGroupTarget"/>) : il relit l'état, pose un
/// plan déjà décidé (<see cref="CpuGroupPlanner"/>) et rend ce que le matériel a retenu. L'onglet reste seul propriétaire
/// de sa partie de settings.json et de sa sécurité thermique. Aucun statut d'onglet ni boîte de dialogue ici : c'est
/// l'appelant qui dit le résultat.
/// </summary>
public sealed partial class CpuControlViewModel : ICpuGroupTarget
{
    private readonly CpuIdentity _identity;

    /// <summary>Le processeur de ce PC, lu une fois au lancement : la page Profils s'en sert sans relire le matériel.</summary>
    public CpuIdentity Identity => _identity;

    /// <summary>Les watts en place viennent d'un groupe appliqué sans en faire l'état de démarrage : ils ne sont pas
    /// enregistrés, et sont rendus d'origine à la fermeture (D7). Retombe à faux dès qu'on les touche à la main.</summary>
    private bool _wattsTransient;

    /// <summary>Bail de réglage et réglage d'un groupe en cours : la bannière de l'onglet.</summary>
    public TuningStatusViewModel Tuning { get; }

    /// <summary>Réglages du plan d'alimentation écrits par un groupe (origine notée), inscrit au registre des
    /// modifications par MainViewModel.</summary>
    public ProfileGroupPowerPlanChanges GroupPlanChanges { get; }

    /// <summary>L'onglet vient de relire le processeur après un changement qui ne vient pas de lui (réveil de veille,
    /// sécurité thermique) : la page Profils revérifie alors son groupe actif.</summary>
    public event Action? HardwareResynced;

    public ProfileDimension Dimension => ProfileDimension.Cpu;

    public bool IsRaised => _cpu.NeedsTemperatureWatch;

    /// <summary>La sécurité thermique a retiré les limites pendant cette session.</summary>
    public bool EmergencyThisSession => _emergencyThisSession;

    /// <summary>D7 : seuls les réglages dont « Appliquer au démarrage » est coché restent en place à la fermeture, et un
    /// état transitoire n'en fait pas partie.</summary>
    private void UpdateKeepLimitsOnExit() => _cpu.KeepLimitsOnExit = ApplyAtStartup && !_wattsTransient;

    /// <summary>Bail tenu par un autre : l'écriture manuelle est refusée, la raison affichée, et l'affichage revient à
    /// ce que le processeur a vraiment.</summary>
    private bool RefuseManualWrite()
    {
        if (Tuning.ManualWriteRefusal() is not { } refusal) return false;

        ShowLimits(_cpu.ReadPowerLimits());
        Status = $"Modification refusée : {refusal}";
        return true;
    }

    public void FlushPendingManualWrites()
    {
        _applyDebounce.Flush();
        foreach (CpuPowerSettingViewModel setting in PowerSettings) setting.FlushPendingWrite();
    }

    /// <summary>Un geste de l'utilisateur dans l'onglet (curseur, champ, bouton) : la bascule automatique se met en
    /// pause. Jamais pour une relecture, une réapplication au lancement ou au réveil, ni un groupe.</summary>
    private void NoteManualEdit() => Tuning.NoteManualWrite("réglage manuel dans l'onglet Processeur");

    public CpuTargetState ReadState()
    {
        // Relu dans Windows, pas sur les curseurs : ceux-ci ne sont lus qu'au lancement, et le plan a pu changer depuis.
        var readings = new List<CpuPowerSettingReading>();
        foreach (CpuPowerSettingViewModel setting in PowerSettings)
        {
            if (_powerTuning.TryRead(setting.Setting, out uint ac, out uint dc)) readings.Add(new CpuPowerSettingReading(setting.Setting, ac, dc));
        }

        CpuPowerLimitSnapshot? limits = ShowPowerLimits ? _cpu.ReadPowerLimits() : null;
        CpuWattsReading? watts = limits is null
            ? null
            : new CpuWattsReading(limits.SustainedWatts, limits.BurstWatts, limits.DefaultSustainedWatts, limits.DefaultBurstWatts,
                limits.MinWatts, limits.MaxWatts);

        string? reason = IsPowerLimitAvailable ? null : UnavailableReason.Length > 0 ? UnavailableReason : null;
        return new CpuTargetState(_identity, _powerTuning.HasBattery, readings, watts, IsPowerLimitAvailable, reason, RiskAccepted,
            _emergencyThisSession);
    }

    public string Describe(CpuProfile profile) => BuildSummary(profile);

    public CpuApplyOutcome Apply(CpuGroupPlan plan, ProfileGroupApplyContext context)
    {
        var items = new List<ReportItem>(plan.Items);
        var touched = new List<CpuPowerSetting>(plan.TouchedSettings ?? []);
        bool permanent = false;

        if (plan.PlanWrites.Count > 0)
        {
            PowerPlanWriteResult result = GroupPlanChanges.Write(plan.PlanWrites);
            permanent = result.Settings.Any(s => s.Retained) || result.Succeeded;
            AddPlanItems(result, items, restoring: false);
        }

        if (plan.RestorePlanOrigin)
        {
            PowerPlanWriteResult result = GroupPlanChanges.RestoreActiveScheme();
            touched.AddRange(result.Settings.Select(s => s.Setting));
            if (result.Settings.Count == 0)
            {
                items.Add(result.Succeeded
                    ? ReportItem.Applied(CpuGroupPlanner.PlanLabel, "aucun réglage du plan changé par un groupe : rien à rendre")
                    : ReportItem.Refused(CpuGroupPlanner.PlanLabel, $"plan d'alimentation non rendu : {result.Error}"));
            }
            else
            {
                AddPlanItems(result, items, restoring: true);
            }
        }

        if (plan.Watts is { } watts) items.Add(ApplyWatts(watts, context));
        if (plan.RestoreWatts) items.Add(RestoreWatts(context));

        return new CpuApplyOutcome(new DimensionReport(ProfileDimension.Cpu, items, plan.Notes, permanent), Retain(touched, plan.TouchesWatts));
    }

    /// <summary>Un profil de l'onglet, par le même chemin qu'un groupe, sans contrôle d'identité (il n'en porte pas) et en
    /// faisant l'état de démarrage, comme avant. Renvoie le compte rendu, pour le statut de l'onglet.</summary>
    private string ApplyTabProfile(CpuProfile model)
    {
        if (Tuning.ManualWriteRefusal() is { } refusal) return $"non appliqué : {refusal}";

        NoteManualEdit();
        FlushPendingManualWrites();
        CpuGroupPlan plan = CpuGroupPlanner.Plan(ProfileGroupEditor.CpuValues(model, _identity), ReadState(), isManual: true, checkIdentity: false);
        CpuApplyOutcome outcome = Apply(plan, new ProfileGroupApplyContext(ProfileGroupRequesters.Tab, true, true, null));

        string permanent = outcome.Report.IsPermanent ? " ; réglages du plan permanents, ils restent après la fermeture" : "";
        return outcome.Report.Body + permanent;
    }

    private void AddPlanItems(PowerPlanWriteResult result, List<ReportItem> items, bool restoring)
    {
        if (result.Settings.Count == 0 && !result.Succeeded)
        {
            items.Add(ReportItem.Refused(CpuGroupPlanner.PlanLabel, $"réglages du plan non écrits : {result.Error}"));
            return;
        }

        foreach (PowerPlanSettingResult setting in result.Settings)
        {
            CpuPowerSetting definition = setting.Setting;
            string label = definition.Label;
            if (setting.Ac is { } ac && setting.Dc is { } dc)
            {
                PowerSettings.FirstOrDefault(p => p.Id == definition.Id)?.ShowRetained(ac, dc);
            }

            string value = setting.Ac is { } shown ? DescribeValue(definition, shown, setting.Dc) : "";
            items.Add(setting switch
            {
                { Retained: true } => ReportItem.Applied(label, restoring ? $"« {label} » rendu à son origine ({value})" : $"« {label} » : {value}"),
                { Ac: null } => new ReportItem(label, ReportItemStatus.NotReadBack, $"« {label} » envoyé, non relu"),
                _ when !result.Succeeded => ReportItem.Refused(label, $"« {label} » refusé : {result.Error}"),
                _ => new ReportItem(label, ReportItemStatus.Trimmed,
                    $"« {label} » : Windows a retenu {value} au lieu de {DescribeValue(definition, setting.RequestedAc, setting.RequestedDc)}"),
            });
        }
    }

    private string DescribeValue(CpuPowerSetting setting, uint ac, uint? dc)
        => _powerTuning.HasBattery && dc is { } battery && battery != ac
            ? $"{setting.Describe(ac)} sur secteur, {setting.Describe(battery)} sur batterie"
            : setting.Describe(ac);

    private ReportItem ApplyWatts(CpuWattsTarget watts, ProfileGroupApplyContext context)
    {
        _applyDebounce.Cancel(PowerLimitKey);
        bool ok = _cpu.TrySetPowerLimits(watts.Sustained, HasBurstLimit ? watts.Burst : null, out string message);
        CpuPowerLimitSnapshot? after = _cpu.ReadPowerLimits();
        ShowLimits(after);

        _wattsTransient = !context.MakeStartupState;
        UpdateKeepLimitsOnExit();

        // Transitoire (bascule automatique) : rien de ce qui est enregistré ne change, settings.json n'est pas réécrit.
        if (context.MakeStartupState) Persist();

        const string label = CpuGroupPlanner.WattsLabel;
        if (after is null) return new ReportItem(label, ReportItemStatus.NotReadBack, "limites en watts envoyées, non relues");

        bool sustainedHeld = Math.Abs(after.SustainedWatts - watts.Sustained) <= 1f;
        bool burstHeld = watts.Burst is not { } burst || after.BurstWatts is not { } readBurst || Math.Abs(readBurst - burst) <= 1f;
        if (sustainedHeld && burstHeld) return ReportItem.Applied(label, $"limites posées : {DescribeLimits(after)}");

        // La relecture fait foi : refusé par le backend, ou accepté puis rogné par le firmware.
        return ok
            ? new ReportItem(label, ReportItemStatus.Trimmed,
                $"limites relues : {DescribeLimits(after)} au lieu de {CpuGroupPlanner.Watts(watts.Sustained)} demandés")
            : ReportItem.Refused(label, $"limites refusées : {message} (relues : {DescribeLimits(after)})");
    }

    private ReportItem RestoreWatts(ProfileGroupApplyContext context)
    {
        _applyDebounce.Cancel(PowerLimitKey);
        bool ok = _cpu.TryRestoreDefaults(out string message);
        CpuPowerLimitSnapshot? after = _cpu.ReadPowerLimits();
        ShowLimits(after);

        if (context.MakeStartupState)
        {
            // Comme « Rétablir » dans l'onglet : plus rien à reposer au lancement.
            _wattsTransient = false;
            AppSettingsStore.Update(settings =>
            {
                settings.Cpu.SustainedWatts = null;
                settings.Cpu.BurstWatts = null;
            });
        }
        else
        {
            _wattsTransient = true;
        }

        UpdateKeepLimitsOnExit();

        const string label = CpuGroupPlanner.WattsLabel;
        if (!ok) return ReportItem.Refused(label, $"retour aux limites d'origine refusé : {message}");
        return after is null
            ? new ReportItem(label, ReportItemStatus.NotReadBack, "limites d'origine reposées, non relues")
            : ReportItem.Applied(label, $"limites d'origine reposées : {DescribeLimits(after)}");
    }

    private static string DescribeLimits(CpuPowerLimitSnapshot limits)
        => limits.BurstWatts is { } burst
            ? $"{CpuGroupPlanner.Watts(limits.SustainedWatts)} en soutenu, {CpuGroupPlanner.Watts(burst)} en pointe"
            : $"{CpuGroupPlanner.Watts(limits.SustainedWatts)} en soutenu";

    /// <summary>Les valeurs relues, sous <c>_suppressApply</c> : un simple affichage ne doit rien réécrire.</summary>
    private void ShowLimits(CpuPowerLimitSnapshot? limits)
    {
        if (limits is null) return;

        _suppressApply = true;
        SustainedWatts = limits.SustainedWatts;
        BurstWatts = limits.BurstWatts ?? limits.SustainedWatts;
        _suppressApply = false;
    }

    /// <summary>Ce que le matériel a retenu de ce que le groupe règle, relu après coup : l'état « conforme » de référence.</summary>
    private CpuProfile Retain(IEnumerable<CpuPowerSetting> touched, bool touchesWatts)
    {
        var retained = new CpuProfile { Name = "retenu" };
        foreach (CpuPowerSetting setting in touched.DistinctBy(s => s.Id))
        {
            if (!_powerTuning.TryRead(setting, out uint ac, out uint dc)) continue;
            retained.PowerSettings[setting.Id] = new CpuProfilePowerValue { Ac = ac, Battery = _powerTuning.HasBattery ? dc : null };
        }

        if (touchesWatts && _cpu.ReadPowerLimits() is { } limits)
        {
            retained.SustainedWatts = limits.SustainedWatts;
            retained.BurstWatts = limits.BurstWatts;
        }

        return retained;
    }
}

/// <summary>Ce qu'un réglage d'alimentation expose aux groupes de profils : son réglage, et un affichage recalé sur ce
/// que Windows a retenu sans rien réécrire.</summary>
public sealed partial class CpuPowerSettingViewModel
{
    public CpuPowerSetting Setting => _setting;

    /// <summary>Affiche ce que Windows a retenu, sans l'écrire : une écriture encore en attente est abandonnée, elle
    /// écraserait la valeur qui vient d'être posée.</summary>
    public void ShowRetained(uint ac, uint dc)
    {
        _writeDebounce.Cancel(Label);

        _suppressWrite = true;
        AcValue = ac;
        BatteryValue = dc;
        if (_setting.Choices is { } choices)
        {
            AcChoice = choices.FirstOrDefault(c => c.Value == ac) ?? AcChoice;
            BatteryChoice = choices.FirstOrDefault(c => c.Value == dc) ?? BatteryChoice;
        }

        _suppressWrite = false;
    }

    /// <summary>Relit le réglage dans Windows et l'affiche (écriture manuelle refusée).</summary>
    private void ReloadFromWindows()
    {
        if (_service.TryRead(_setting, out uint ac, out uint dc)) ShowRetained(ac, dc);
    }
}
