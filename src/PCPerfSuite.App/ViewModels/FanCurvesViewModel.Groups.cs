using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Profiles;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce que l'onglet Ventilateurs expose aux groupes de profils (<see cref="IFanGroupTarget"/>) : il relit les
/// ventilateurs listés, pose un plan déjà décidé (<see cref="FanGroupPlanner"/>) par le même chemin qu'un profil de
/// l'onglet, et rend les configurations en place. Rien ne peut atteindre un ventilateur qui n'est pas listé ici, donc
/// jamais le contrôleur embarqué d'un portable (règle 5). L'onglet reste seul propriétaire de sa partie de
/// settings.json. Aucun statut d'onglet ni boîte de dialogue ici : c'est l'appelant qui dit le résultat.
/// </summary>
public sealed partial class FanCurvesViewModel : IFanGroupTarget
{
    /// <summary>Terminée au premier relevé : on sait alors quels ventilateurs existent.</summary>
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Configurations de démarrage mises de côté pendant qu'un groupe est appliqué sans en faire l'état de
    /// démarrage (D7) : c'est elles qui partent sur le disque, pas la configuration vivante.</summary>
    private readonly FanStartupOverrides _startupOverrides = new();

    /// <summary>Bail de réglage et réglage d'un groupe en cours : la bannière de l'onglet.</summary>
    public TuningStatusViewModel Tuning { get; }

    public ProfileDimension Dimension => ProfileDimension.Fans;

    /// <summary>Les ventilateurs ne relèvent rien au-delà d'une origine : ils ne chauffent pas la machine.</summary>
    public bool IsRaised => false;

    public Task WhenReady => _ready.Task;

    /// <summary>Les modifications des ventilateurs partent tout de suite : rien n'attend.</summary>
    public void FlushPendingManualWrites()
    {
    }

    public FanTargetState ReadState()
    {
        var current = new FanProfile { Name = "actuel" };
        foreach (FanControlItemViewModel item in Fans)
        {
            current.Fans.Add(item.Capture());
            current.FanNames[item.FanId] = item.DisplayName;
        }

        return new FanTargetState(
            _hasSnapshot,
            Fans.Select(f => new FanTargetFan(f.FanId, f.DisplayName, IsGpuCooler(f.FanId))).ToList(),
            MachineInfo.Current.SoftwareFanControlRefused,
            NoFansMessage,
            current);
    }

    string IFanGroupTarget.AbsenceReason(string fanId) => AbsenceReason(fanId);

    public string Describe(FanProfile profile) => BuildProfileSummary(profile);

    public FanApplyOutcome Apply(FanGroupPlan plan, ProfileGroupApplyContext context)
    {
        var items = new List<ReportItem>(plan.Items);

        // Un seul enregistrement du fichier de réglages pour tout le groupe, pas un par propriété modifiée.
        _applyingProfile = true;
        try
        {
            if (plan.RestoreAuto)
            {
                int restored = 0;
                foreach (FanControlItemViewModel item in Fans)
                {
                    NoteStartupState(item, context);
                    if (item.Mode == FanControlMode.Auto) continue;
                    item.Mode = FanControlMode.Auto;
                    restored++;
                }

                items.Add(ReportItem.Applied(DimensionReport.Title(ProfileDimension.Fans), restored == 0
                    ? "tous les ventilateurs sont déjà au BIOS"
                    : $"{Plural(restored, "ventilateur rendu", "ventilateurs rendus")} au BIOS (leurs courbes sont gardées)"));
            }

            foreach (FanPlanEntry entry in plan.Entries)
            {
                FanControlItemViewModel? item = Fans.FirstOrDefault(f => f.FanId == entry.FanId);
                if (item is null)
                {
                    items.Add(ReportItem.Ignored(entry.Name, $"« {entry.Name} » non réglé (disparu depuis le relevé)"));
                    continue;
                }

                NoteStartupState(item, context);
                var notes = new List<string>(entry.SanitizeNotes);
                notes.AddRange(item.ApplyFromProfile(entry.Config, HasGpu));

                string text = $"« {item.DisplayName} » : {DescribeMode(entry.Config)}";
                items.Add(notes.Count == 0
                    ? ReportItem.Applied(item.DisplayName, text)
                    : new ReportItem(item.DisplayName, ReportItemStatus.Trimmed, $"{text} ({string.Join(", ", notes)})"));
            }
        }
        finally
        {
            _applyingProfile = false;
            Persist();
        }

        var report = new DimensionReport(ProfileDimension.Fans, items, plan.Notes);
        LastProfileReport = report.Describe();
        return new FanApplyOutcome(report, Retain(plan.TouchedFanIds ?? []));
    }

    /// <summary>Appliqué sans en faire l'état de démarrage : la configuration d'avant est mise de côté (la plus ancienne
    /// reste). Sinon, la configuration posée redevient celle du démarrage.</summary>
    private void NoteStartupState(FanControlItemViewModel item, ProfileGroupApplyContext context)
    {
        if (context.MakeStartupState) _startupOverrides.Release(item.FanId);
        else _startupOverrides.Hold(item.FanId, item.Capture());
    }

    /// <summary>Un ventilateur modifié à la main : sa configuration vivante redevient celle du démarrage. Pendant
    /// l'application d'un profil ou d'un groupe, rien : c'est l'application qui décide, et elle enregistre à la fin.</summary>
    private void OnFanEdited(string fanId)
    {
        if (_applyingProfile) return;
        _startupOverrides.Release(fanId);
        Persist();

        // Un ventilateur pas encore listé est en cours de création (règles des pompes à la découverte) : pas un geste
        // de l'utilisateur, la bascule automatique n'a pas à se mettre en pause.
        if (Fans.Any(f => f.FanId == fanId)) Tuning.NoteManualWrite("réglage manuel dans l'onglet Ventilateurs");
    }

    /// <summary>Ce qui part sur le disque : les configurations vivantes, sauf celles mises de côté.</summary>
    private List<FanCurveConfig> StartupCurves()
        => _startupOverrides.IsEmpty ? _settings.FanCurves : _startupOverrides.Resolve(_settings.FanCurves);

    private FanProfile Retain(IReadOnlyList<string> fanIds)
    {
        var retained = new FanProfile { Name = "retenu" };
        foreach (FanControlItemViewModel item in Fans.Where(f => fanIds.Contains(f.FanId)))
        {
            retained.Fans.Add(item.Capture());
            retained.FanNames[item.FanId] = item.DisplayName;
        }

        return retained;
    }

    /// <summary>Un profil de l'onglet, par le même chemin qu'un groupe, en faisant l'état de démarrage, comme avant.</summary>
    private string ApplyTabProfile(FanProfile model)
    {
        if (Tuning.ManualWriteRefusal() is { } refusal) return $"non appliqué : {refusal}";

        Tuning.NoteManualWrite("profil appliqué dans l'onglet Ventilateurs");
        FanGroupPlan plan = FanGroupPlanner.Plan(ProfileGroupEditor.FanValues(model), ReadState(), AbsenceReason);
        FanApplyOutcome outcome = Apply(plan, new ProfileGroupApplyContext(ProfileGroupRequesters.Tab, true, true, null));
        return outcome.Report.Body;
    }

    /// <summary>Au premier relevé.</summary>
    private void MarkReady() => _ready.TrySetResult();
}
