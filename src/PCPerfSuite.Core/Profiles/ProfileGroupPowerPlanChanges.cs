using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Où l'origine des réglages du plan est gardée, abstrait pour les tests. Clé de valeur : « GUID du réglage/ac »
/// et « GUID du réglage/dc », rangées par plan.</summary>
public interface IPowerPlanOriginStore
{
    /// <summary>Origines notées, par plan.</summary>
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, uint>> All { get; }

    /// <summary>Ajoute les valeurs encore absentes de ce plan, et seulement elles : réécrire une origine déjà notée
    /// retiendrait la valeur que l'app vient de poser. False si l'enregistrement a échoué : rien ne doit alors être
    /// écrit dans Windows.</summary>
    bool Remember(Guid scheme, IReadOnlyDictionary<string, uint> values);

    /// <summary>Oublie ces clés de ce plan ; un plan sans plus aucune clé disparaît.</summary>
    void Forget(Guid scheme, IReadOnlyCollection<string> keys);
}

/// <summary>Origine gardée dans settings.json, sous <see cref="AppSettings.ProfileGroupPowerOrigins"/>, écrite par ce seul
/// propriétaire.</summary>
public sealed class AppSettingsPowerPlanOriginStore : IPowerPlanOriginStore
{
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, uint>> All
    {
        get
        {
            var all = new Dictionary<Guid, IReadOnlyDictionary<string, uint>>();
            foreach ((string scheme, Dictionary<string, uint>? values) in AppSettingsStore.Load().ProfileGroupPowerOrigins ?? new())
            {
                if (values is { Count: > 0 } && Guid.TryParse(scheme, out Guid guid)) all[guid] = values;
            }

            return all;
        }
    }

    public bool Remember(Guid scheme, IReadOnlyDictionary<string, uint> values)
        => AppSettingsStore.TryUpdate(settings =>
        {
            settings.ProfileGroupPowerOrigins ??= new Dictionary<string, Dictionary<string, uint>>();
            string key = scheme.ToString("D");
            if (!settings.ProfileGroupPowerOrigins.TryGetValue(key, out Dictionary<string, uint>? noted) || noted is null)
            {
                noted = new Dictionary<string, uint>();
                settings.ProfileGroupPowerOrigins[key] = noted;
            }

            foreach ((string name, uint value) in values) noted.TryAdd(name, value);
        });

    public void Forget(Guid scheme, IReadOnlyCollection<string> keys)
        => AppSettingsStore.Update(settings =>
        {
            string key = scheme.ToString("D");
            if (settings.ProfileGroupPowerOrigins?.TryGetValue(key, out Dictionary<string, uint>? noted) != true || noted is null) return;

            foreach (string name in keys) noted.Remove(name);
            if (noted.Count == 0) settings.ProfileGroupPowerOrigins.Remove(key);
        });
}

/// <summary>Ce qu'un réglage du plan a retenu après une écriture. <paramref name="Ac"/> et <paramref name="Dc"/> null :
/// relecture impossible.</summary>
public sealed record PowerPlanSettingResult(CpuPowerSetting Setting, uint RequestedAc, uint RequestedDc, uint? Ac, uint? Dc)
{
    public bool Retained => Ac == RequestedAc && Dc == RequestedDc;
}

/// <summary>Résultat d'une écriture ou d'un retour à l'origine dans le plan actif.</summary>
public sealed record PowerPlanWriteResult(bool Succeeded, IReadOnlyList<PowerPlanSettingResult> Settings, string? Error, string? PlanName)
{
    public static PowerPlanWriteResult Failed(string error) => new(false, [], error, null);
}

/// <summary>
/// Réglages du plan d'alimentation qu'un groupe de profils écrit (mode boost, états min et max, fréquence maximale,
/// préférence performance / économie) : ils restent après la fermeture de l'app (D7), donc leur origine est notée avant
/// la première écriture d'un groupe, et ce propriétaire est inscrit au registre des modifications (« Tout rétablir »).
///
/// L'origine est gardée par plan : un groupe appliqué sous « Équilibré », puis sous « Performances élevées », garde
/// l'origine des deux. Chaque lot ne réactive le plan qu'une fois (<see cref="IPowerPlanValues.TryWrite"/>), et une
/// valeur déjà en place n'est pas réécrite. Un réglage revenu à son origine est oublié : il n'y a plus rien à rendre.
///
/// Le parking des cœurs n'en fait pas partie (D12, <c>CoreParkingService</c>). Les écritures manuelles de l'onglet
/// Processeur n'y passent pas : seules celles d'un groupe sont notées (décision de Denis, 01/10/2026). Best-effort
/// (règle 2) : rien ne lève.
/// </summary>
public sealed class ProfileGroupPowerPlanChanges : ISystemChangeOwner
{
    public const string OwnerId = "groupes-plan-alimentation";

    private readonly IReadOnlyList<CpuPowerSetting> _catalog;
    private readonly bool _hasBattery;
    private readonly IPowerPlanValues _plan;
    private readonly IPowerPlanOriginStore _origins;
    private readonly object _gate = new();

    public ProfileGroupPowerPlanChanges(
        IReadOnlyList<CpuPowerSetting> catalog, bool hasBattery, IPowerPlanValues? plan = null, IPowerPlanOriginStore? origins = null)
    {
        _catalog = catalog;
        _hasBattery = hasBattery;
        _plan = plan ?? PowerPlanValues.Instance;
        _origins = origins ?? new AppSettingsPowerPlanOriginStore();
    }

    public string Id => OwnerId;

    public string Title => "Plan d'alimentation (groupes de profils)";

    public bool HasChanges
    {
        get
        {
            try { return _origins.All.Values.Any(values => values.Count > 0); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Le réglage dans le plan actif, null si ce Windows ne le connaît pas.</summary>
    public CpuPowerSettingReading? Read(CpuPowerSetting setting)
        => _plan.ActiveScheme() is { } scheme && TryRead(scheme, setting, out uint ac, out uint dc)
            ? new CpuPowerSettingReading(setting, ac, dc)
            : null;

    /// <summary>
    /// Écrit ces valeurs dans le plan actif, en un lot : relit l'état, note d'abord l'origine de chaque réglage encore
    /// jamais écrit par un groupe (rien n'est écrit si elle ne peut pas l'être), n'écrit que ce qui change, relit, et
    /// oublie ce qui est revenu à l'origine.
    /// </summary>
    public PowerPlanWriteResult Write(IReadOnlyList<CpuPlanWrite> writes)
    {
        try
        {
            lock (_gate) return WriteLocked(writes);
        }
        catch (Exception ex)
        {
            return PowerPlanWriteResult.Failed($"écriture du plan impossible ({ex.GetType().Name})");
        }
    }

    /// <summary>« Origine » de la partie processeur d'un groupe : rend au plan actif les valeurs notées avant qu'un groupe
    /// n'y touche. Sans origine notée, rien n'est écrit.</summary>
    public PowerPlanWriteResult RestoreActiveScheme()
    {
        try
        {
            lock (_gate)
            {
                if (_plan.ActiveScheme() is not { } scheme) return PowerPlanWriteResult.Failed("Windows n'indique pas le plan d'alimentation actif");

                IReadOnlyList<(CpuPowerSetting Setting, uint Ac, uint Dc)> noted = OriginsOf(scheme);
                if (noted.Count == 0) return new PowerPlanWriteResult(true, [], null, PlanLabel(scheme));

                return WriteLocked(noted.Select(n => new CpuPlanWrite(n.Setting, n.Ac, n.Dc)).ToList());
            }
        }
        catch (Exception ex)
        {
            return PowerPlanWriteResult.Failed($"retour à l'origine impossible ({ex.GetType().Name})");
        }
    }

    public SystemRestoreResult RestoreAll()
    {
        try
        {
            lock (_gate)
            {
                IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, uint>> all = _origins.All;
                if (all.Count == 0) return SystemRestoreResult.Nothing;

                var notRestored = new List<SystemChange>();
                int restored = 0;
                string? message = null;
                foreach (Guid scheme in all.Keys)
                {
                    IReadOnlyList<(CpuPowerSetting Setting, uint Ac, uint Dc)> noted = OriginsOf(scheme);
                    if (noted.Count == 0)
                    {
                        _origins.Forget(scheme, all[scheme].Keys.ToList());
                        continue;
                    }

                    // Le plan modifié a été supprimé depuis : il n'y a plus rien à rendre, et l'origine ne servirait plus.
                    if (noted.All(n => !TryRead(scheme, n.Setting, out _, out _)))
                    {
                        _origins.Forget(scheme, all[scheme].Keys.ToList());
                        continue;
                    }

                    bool written = _plan.TryWrite(scheme, CpuPowerTuningService.SubGroup,
                        noted.Select(n => new PowerValueWrite(n.Setting.Guid, n.Ac, n.Dc)).ToList());
                    if (!written) message = "Windows a refusé l'écriture (app lancée sans les droits administrateur ?).";

                    foreach ((CpuPowerSetting setting, uint ac, uint dc) in noted)
                    {
                        if (TryRead(scheme, setting, out uint nowAc, out uint nowDc) && nowAc == ac && nowDc == dc)
                        {
                            restored++;
                            _origins.Forget(scheme, Keys(setting));
                            continue;
                        }

                        notRestored.Add(DescribeSetting(scheme, setting, ac, dc));
                    }
                }

                if (notRestored.Count == 0) return restored == 0 ? SystemRestoreResult.Nothing : new SystemRestoreResult(SystemRestoreStatus.Restored, [], null);
                SystemRestoreStatus status = restored == 0 ? SystemRestoreStatus.Failed : SystemRestoreStatus.Partial;
                return new SystemRestoreResult(status, notRestored,
                    message ?? "Windows a retenu d'autres valeurs (réglage piloté par le fabricant du PC ?).");
            }
        }
        catch (Exception ex)
        {
            return new SystemRestoreResult(SystemRestoreStatus.Failed, [], $"Rétablissement impossible ({ex.GetType().Name}).");
        }
    }

    public IReadOnlyList<SystemChange> Describe()
    {
        try
        {
            var changes = new List<SystemChange>();
            foreach (Guid scheme in _origins.All.Keys)
            {
                foreach ((CpuPowerSetting setting, uint ac, uint dc) in OriginsOf(scheme))
                {
                    if (TryRead(scheme, setting, out uint nowAc, out uint nowDc) && nowAc == ac && nowDc == dc) continue;
                    changes.Add(DescribeSetting(scheme, setting, ac, dc));
                }
            }

            return changes;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private PowerPlanWriteResult WriteLocked(IReadOnlyList<CpuPlanWrite> writes)
    {
        if (writes.Count == 0) return new PowerPlanWriteResult(true, [], null, null);
        if (_plan.ActiveScheme() is not { } scheme) return PowerPlanWriteResult.Failed("Windows n'indique pas le plan d'alimentation actif");

        IReadOnlyDictionary<string, uint> known = _origins.All.TryGetValue(scheme, out IReadOnlyDictionary<string, uint>? noted)
            ? noted
            : new Dictionary<string, uint>();

        var toRemember = new Dictionary<string, uint>();
        var changed = new List<CpuPlanWrite>();
        var unknown = new List<string>();
        foreach (CpuPlanWrite write in writes)
        {
            if (!TryRead(scheme, write.Setting, out uint ac, out uint dc))
            {
                unknown.Add(write.Setting.Label);
                continue;
            }

            if (ac == write.Ac && dc == write.Dc) continue;

            string[] keys = Keys(write.Setting);
            if (!known.ContainsKey(keys[0]))
            {
                toRemember[keys[0]] = ac;
                toRemember[keys[1]] = dc;
            }

            changed.Add(write);
        }

        // L'origine d'abord : une écriture faite sans elle ne pourrait plus être rendue.
        if (toRemember.Count > 0 && !_origins.Remember(scheme, toRemember))
        {
            return PowerPlanWriteResult.Failed(
                $"les valeurs d'origine n'ont pas pu être enregistrées ({AppSettingsStore.LastError ?? "settings.json"}) : rien n'a été écrit, pour pouvoir toujours les rendre");
        }

        bool written = changed.Count == 0 || _plan.TryWrite(scheme, CpuPowerTuningService.SubGroup,
            changed.Select(w => new PowerValueWrite(w.Setting.Guid, w.Ac, w.Dc)).ToList());

        var results = new List<PowerPlanSettingResult>();
        foreach (CpuPlanWrite write in writes)
        {
            bool read = TryRead(scheme, write.Setting, out uint ac, out uint dc);
            results.Add(new PowerPlanSettingResult(write.Setting, write.Ac, write.Dc, read ? ac : null, read ? dc : null));
        }

        ForgetBackAtOrigin(scheme, results);

        string? error = !written
            ? "Windows a refusé le réglage (app lancée sans les droits administrateur ?)"
            : unknown.Count > 0 ? $"réglage inconnu de ce Windows : {string.Join(", ", unknown)}" : null;
        return new PowerPlanWriteResult(written && unknown.Count == 0, results, error, PlanLabel(scheme));
    }

    private void ForgetBackAtOrigin(Guid scheme, IEnumerable<PowerPlanSettingResult> results)
    {
        if (!_origins.All.TryGetValue(scheme, out IReadOnlyDictionary<string, uint>? noted)) return;

        var keys = new List<string>();
        foreach (PowerPlanSettingResult result in results)
        {
            string[] names = Keys(result.Setting);
            if (result.Ac is { } ac && result.Dc is { } dc
                && noted.TryGetValue(names[0], out uint originAc) && noted.TryGetValue(names[1], out uint originDc)
                && ac == originAc && dc == originDc)
            {
                keys.AddRange(names);
            }
        }

        if (keys.Count > 0) _origins.Forget(scheme, keys);
    }

    /// <summary>Les origines notées de ce plan, rattachées aux réglages du catalogue ; une clé inconnue (réglage d'une
    /// autre version) est laissée de côté.</summary>
    private IReadOnlyList<(CpuPowerSetting Setting, uint Ac, uint Dc)> OriginsOf(Guid scheme)
    {
        if (!_origins.All.TryGetValue(scheme, out IReadOnlyDictionary<string, uint>? noted)) return [];

        var origins = new List<(CpuPowerSetting, uint, uint)>();
        foreach (CpuPowerSetting setting in _catalog)
        {
            string[] keys = Keys(setting);
            bool hasAc = noted.TryGetValue(keys[0], out uint ac);
            bool hasDc = noted.TryGetValue(keys[1], out uint dc);
            if (!hasAc && !hasDc) continue;
            origins.Add((setting, hasAc ? ac : dc, hasDc ? dc : ac));
        }

        return origins;
    }

    private SystemChange DescribeSetting(Guid scheme, CpuPowerSetting setting, uint originAc, uint originDc)
    {
        string plan = PlanLabel(scheme);
        if (!TryRead(scheme, setting, out uint ac, out uint dc))
        {
            return new SystemChange(setting.Label, $"Plan « {plan} » : réglage illisible, le plan a peut-être été supprimé.", CanRestore: false);
        }

        string detail = _hasBattery
            ? $"Plan « {plan} » : sur secteur {setting.Describe(ac)} (origine {setting.Describe(originAc)}), "
              + $"sur batterie {setting.Describe(dc)} (origine {setting.Describe(originDc)})."
            : $"Plan « {plan} » : {setting.Describe(ac)} (origine {setting.Describe(originAc)}).";
        return new SystemChange(setting.Label, detail, CanRestore: true);
    }

    private bool TryRead(Guid scheme, CpuPowerSetting setting, out uint ac, out uint dc)
        => _plan.TryRead(scheme, CpuPowerTuningService.SubGroup, setting.Guid, out ac, out dc);

    private string PlanLabel(Guid scheme) => _plan.FriendlyName(scheme) ?? "plan d'alimentation";

    private static string[] Keys(CpuPowerSetting setting)
    {
        string key = setting.Guid.ToString("D");
        return [$"{key}/ac", $"{key}/dc"];
    }
}
