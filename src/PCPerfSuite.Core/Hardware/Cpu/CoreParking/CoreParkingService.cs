using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemChanges;

namespace PCPerfSuite.Core.Hardware.Cpu.CoreParking;

/// <summary>Où l'origine des réglages est gardée, abstrait pour les tests.</summary>
public interface ICoreParkingOriginStore
{
    /// <summary>Valeurs d'origine, clé = « sous-groupe/réglage/ac|dc » (et l'ancienne clé sans suffixe du tweak).</summary>
    IReadOnlyDictionary<string, uint> Values { get; }

    /// <summary>Plan où l'origine a été prise, null pour une origine notée par une version qui ne le retenait pas (le
    /// plan actif fait alors foi).</summary>
    Guid? Scheme { get; }

    /// <summary>Ajoute les valeurs encore absentes, et seulement elles : réécrire une origine déjà notée retiendrait la
    /// valeur que l'app vient de poser. Retient le plan s'il ne l'était pas. False si l'enregistrement a échoué :
    /// rien ne doit alors être écrit dans Windows.</summary>
    bool Remember(Guid scheme, IReadOnlyDictionary<string, uint> values);

    /// <summary>Oublie ces clés ; <paramref name="forgetScheme"/> oublie aussi le plan (plus aucune origine notée).</summary>
    void Forget(IReadOnlyCollection<string> keys, bool forgetScheme);
}

/// <summary>Origine gardée dans settings.json, sous <see cref="AppSettings.OriginalPowerValues"/> : celle que le tweak
/// d'Optimisation Windows notait déjà y est reprise telle quelle.</summary>
public sealed class AppSettingsCoreParkingOriginStore : ICoreParkingOriginStore
{
    public IReadOnlyDictionary<string, uint> Values => AppSettingsStore.Load().OriginalPowerValues;

    public Guid? Scheme => Guid.TryParse(AppSettingsStore.Load().CoreParkingOriginScheme, out Guid scheme) ? scheme : null;

    public bool Remember(Guid scheme, IReadOnlyDictionary<string, uint> values)
        => AppSettingsStore.TryUpdate(settings =>
        {
            foreach ((string key, uint value) in values) settings.OriginalPowerValues.TryAdd(key, value);
            settings.CoreParkingOriginScheme ??= scheme.ToString("D");
        });

    public void Forget(IReadOnlyCollection<string> keys, bool forgetScheme)
        => AppSettingsStore.Update(settings =>
        {
            foreach (string key in keys) settings.OriginalPowerValues.Remove(key);
            if (forgetScheme) settings.CoreParkingOriginScheme = null;
        });
}

/// <summary>Ce qu'un réglage a retenu après une écriture.</summary>
/// <param name="Applied">Valeur relue, null si la relecture a échoué.</param>
public sealed record CoreParkingApplied(CoreParkingSetting Setting, CoreParkingValue Requested, CoreParkingValue? Applied)
{
    public bool Matches => Applied == Requested;
}

/// <summary>Résultat d'une écriture : ce qui a été retenu, l'erreur éventuelle, et une remarque à afficher.</summary>
public sealed record CoreParkingWriteResult(bool Succeeded, IReadOnlyList<CoreParkingApplied> Applied, string? Error, string? Note);

/// <summary>
/// Parking des cœurs du plan d'alimentation Windows : CPMINCORES et CPMAXCORES, leurs variantes pour les cœurs
/// performants (CPMINCORES1, CPMAXCORES1) et l'ordonnancement hybride, en pourcentage de cœurs, sur secteur et sur
/// batterie, par powrprof (qui voit ces réglages masqués). Le tweak « core-parking » d'Optimisation Windows en est une
/// façade.
///
/// Ces réglages restent après la fermeture de l'app (D7) : l'origine de chacun est notée avant sa première écriture,
/// et le service est inscrit au registre des modifications (<see cref="ISystemChangeOwner"/>). L'origine n'est gardée
/// que pour un plan à la fois : écrire dans un autre plan rend d'abord au précédent ses valeurs.
///
/// Les écritures sont rares et lourdes (le plan est réactivé) ; elles passent sous un verrou, l'interface et le tweak
/// pouvant écrire depuis deux threads. Best-effort (règle 2) : rien ne lève.
/// </summary>
public sealed class CoreParkingService : ISystemChangeOwner
{
    public const string OwnerId = "core-parking";

    private readonly IPowerPlanValues _plan;
    private readonly ICoreParkingOriginStore _origins;
    private readonly object _gate = new();

    public CoreParkingService(bool isHybrid, bool hasBattery, IPowerPlanValues? plan = null, ICoreParkingOriginStore? origins = null)
    {
        IsHybrid = isHybrid;
        HasBattery = hasBattery;
        _plan = plan ?? PowerPlanValues.Instance;
        _origins = origins ?? new AppSettingsCoreParkingOriginStore();
        Settings = CoreParkingCatalog.For(isHybrid);
    }

    public bool IsHybrid { get; }

    /// <summary>Un PC à batterie : secteur et batterie se règlent séparément. Sinon, la valeur secteur est écrite aux
    /// deux, comme le font les autres réglages d'alimentation de l'app.</summary>
    public bool HasBattery { get; }

    /// <summary>Réglages qui ont un sens sur ce processeur (voir <see cref="CoreParkingCatalog.For"/>).</summary>
    public IReadOnlyList<CoreParkingSetting> Settings { get; }

    public string Id => OwnerId;
    public string Title => "Parking des cœurs";

    public Guid? ActiveScheme() => _plan.ActiveScheme();

    public string? PlanName(Guid scheme) => _plan.FriendlyName(scheme);

    /// <summary>Valeurs du plan actif, null si ce Windows ne connaît pas ce réglage.</summary>
    public CoreParkingValue? Read(CoreParkingSetting setting)
        => _plan.ActiveScheme() is { } scheme ? Read(scheme, setting) : null;

    /// <summary>Valeurs d'avant PCPerfSuite, null si l'app n'a jamais écrit ce réglage.</summary>
    public CoreParkingValue? Origin(CoreParkingSetting setting) => Origin(_origins.Values, setting);

    /// <summary>Plan dont l'origine est notée, ou le plan actif.</summary>
    public Guid? ModifiedScheme() => _origins.Scheme ?? _plan.ActiveScheme();

    public bool HasChanges
    {
        get
        {
            IReadOnlyDictionary<string, uint> values = _origins.Values;
            return CoreParkingCatalog.All.Any(s => Origin(values, s) is not null);
        }
    }

    /// <summary>
    /// Écrit ces valeurs dans le plan actif, en une fois, après avoir noté l'origine de chaque réglage encore jamais
    /// écrit, puis relit. Un réglage revenu à son origine est oublié : il n'y a plus rien à rendre.
    /// </summary>
    public CoreParkingWriteResult Write(IReadOnlyList<CoreParkingTarget> targets)
    {
        lock (_gate)
        {
            if (targets.Count == 0) return new CoreParkingWriteResult(true, [], null, null);
            if (_plan.ActiveScheme() is not { } scheme)
            {
                return new CoreParkingWriteResult(false, [], "Windows n'indique pas le plan d'alimentation actif.", null);
            }

            string? note = null;
            if (_origins.Scheme is { } previous && previous != scheme && HasChanges)
            {
                note = HandOverPreviousPlan(previous);
                if (note is null)
                {
                    return new CoreParkingWriteResult(false, [],
                        $"Le plan « {PlanLabel(previous)} » n'a pas pu retrouver ses valeurs d'origine : rien n'a été écrit dans le plan actif, pour ne pas perdre cette origine.", null);
                }
            }

            IReadOnlyDictionary<string, uint> known = _origins.Values;
            var toRemember = new Dictionary<string, uint>();
            var writes = new List<(CoreParkingSetting Setting, CoreParkingValue Value)>();
            var unknown = new List<string>();

            foreach (CoreParkingTarget target in targets)
            {
                CoreParkingSetting setting = target.Setting;
                if (Read(scheme, setting) is not { } now)
                {
                    unknown.Add(setting.LabelFor(IsHybrid));
                    continue;
                }

                if (setting.Sanitize(target.Value.Ac) is not { } ac
                    || setting.Sanitize(HasBattery ? target.Value.Dc : target.Value.Ac) is not { } dc)
                {
                    unknown.Add(setting.LabelFor(IsHybrid));
                    continue;
                }

                if (Origin(known, setting) is null)
                {
                    string key = CoreParkingCatalog.OriginKey(setting);
                    toRemember[$"{key}/ac"] = now.Ac;
                    toRemember[$"{key}/dc"] = now.Dc;
                }

                writes.Add((setting, new CoreParkingValue(ac, dc)));
            }

            // L'origine d'abord : une écriture faite sans elle ne pourrait plus être rendue.
            if (toRemember.Count > 0 && !_origins.Remember(scheme, toRemember))
            {
                return new CoreParkingWriteResult(false, [],
                    $"Les valeurs d'origine n'ont pas pu être enregistrées ({AppSettingsStore.LastError ?? "settings.json"}) : rien n'a été écrit, pour pouvoir toujours les rendre.", note);
            }

            bool written = _plan.TryWrite(scheme, CoreParkingCatalog.SubGroup,
                writes.Select(w => new PowerValueWrite(w.Setting.Guid, w.Value.Ac, w.Value.Dc)).ToList());

            var applied = writes.Select(w => new CoreParkingApplied(w.Setting, w.Value, Read(scheme, w.Setting))).ToList();
            ForgetSettingsBackAtOrigin(applied.Select(a => (a.Setting, a.Applied)));

            string? error = !written
                ? "Windows a refusé le réglage (app lancée sans les droits administrateur ?)."
                : unknown.Count > 0 ? $"Réglage inconnu de ce Windows : {string.Join(", ", unknown)}." : null;
            return new CoreParkingWriteResult(written && unknown.Count == 0, applied, error, note);
        }
    }

    /// <summary>Rend leur origine à tous les réglages que l'app a changés, dans le plan où elle les a changés.</summary>
    public SystemRestoreResult RestoreAll() => Restore(CoreParkingCatalog.All);

    /// <summary>
    /// Rend leur origine à ces réglages. <paramref name="fallbackMinCores"/> est la valeur posée sur CPMINCORES quand
    /// aucune origine n'a été notée (le tweak, activé ailleurs) : elle n'est alors pas retenue comme une modification.
    /// </summary>
    public SystemRestoreResult Restore(IReadOnlyList<CoreParkingSetting> settings, uint? fallbackMinCores = null)
    {
        lock (_gate)
        {
            if (ModifiedScheme() is not { } scheme)
            {
                return new SystemRestoreResult(SystemRestoreStatus.Failed, Describe(),
                    "Windows n'indique pas le plan d'alimentation actif.");
            }

            IReadOnlyDictionary<string, uint> values = _origins.Values;
            var restores = new List<(CoreParkingSetting Setting, CoreParkingValue Value, bool IsOrigin)>();
            foreach (CoreParkingSetting setting in settings)
            {
                if (Origin(values, setting) is { } origin) restores.Add((setting, origin, true));
                else if (setting == CoreParkingCatalog.MinCores && fallbackMinCores is { } fallback)
                {
                    restores.Add((setting, new CoreParkingValue(fallback, fallback), false));
                }
            }

            if (restores.Count == 0) return SystemRestoreResult.Nothing;

            // Le plan modifié a été supprimé depuis : il n'y a plus rien à rendre, et l'origine ne servirait plus.
            if (restores.All(r => Read(scheme, r.Setting) is null))
            {
                ForgetAll(restores.Where(r => r.IsOrigin).Select(r => r.Setting));
                return new SystemRestoreResult(SystemRestoreStatus.NothingToRestore, [],
                    "Le plan d'alimentation modifié n'existe plus : rien à rendre.");
            }

            bool written = _plan.TryWrite(scheme, CoreParkingCatalog.SubGroup,
                restores.Select(r => new PowerValueWrite(r.Setting.Guid, r.Value.Ac, r.Value.Dc)).ToList());

            var notRestored = new List<SystemChange>();
            var restored = new List<CoreParkingSetting>();
            foreach ((CoreParkingSetting setting, CoreParkingValue wanted, bool isOrigin) in restores)
            {
                CoreParkingValue? now = Read(scheme, setting);
                if (now == wanted)
                {
                    if (isOrigin) restored.Add(setting);
                    continue;
                }

                notRestored.Add(DescribeSetting(setting, wanted, now, PlanLabel(scheme)));
            }

            ForgetAll(restored);

            SystemRestoreStatus status = notRestored.Count == 0 ? SystemRestoreStatus.Restored
                : restored.Count == 0 ? SystemRestoreStatus.Failed : SystemRestoreStatus.Partial;
            string? message = status == SystemRestoreStatus.Restored ? null
                : !written ? "Windows a refusé l'écriture (app lancée sans les droits administrateur ?)."
                : "Windows a retenu d'autres valeurs (réglage piloté par le fabricant du PC ?).";
            return new SystemRestoreResult(status, notRestored, message);
        }
    }

    public IReadOnlyList<SystemChange> Describe()
    {
        IReadOnlyDictionary<string, uint> values = _origins.Values;
        if (ModifiedScheme() is not { } scheme)
        {
            return CoreParkingCatalog.All.Where(s => Origin(values, s) is not null)
                .Select(s => new SystemChange(s.LabelFor(IsHybrid), "Plan d'alimentation actif inconnu.", CanRestore: false))
                .ToList();
        }

        string plan = PlanLabel(scheme);
        return CoreParkingCatalog.All
            .Select(s => (Setting: s, Origin: Origin(values, s)))
            .Where(s => s.Origin is not null)
            .Select(s => DescribeSetting(s.Setting, s.Origin!.Value, Read(scheme, s.Setting), plan))
            .ToList();
    }

    private SystemChange DescribeSetting(CoreParkingSetting setting, CoreParkingValue origin, CoreParkingValue? now, string plan)
    {
        if (now is not { } current)
        {
            return new SystemChange($"{setting.LabelFor(IsHybrid)} ({setting.Alias})",
                $"Plan « {plan} » : réglage illisible, le plan a peut-être été supprimé.", CanRestore: false);
        }

        string detail = HasBattery
            ? $"Plan « {plan} » : sur secteur {setting.Format(current.Ac)} (origine {setting.Format(origin.Ac)}), "
              + $"sur batterie {setting.Format(current.Dc)} (origine {setting.Format(origin.Dc)})."
            : $"Plan « {plan} » : {setting.Format(current.Ac)} (origine {setting.Format(origin.Ac)}).";
        return new SystemChange($"{setting.LabelFor(IsHybrid)} ({setting.Alias})", detail, CanRestore: true);
    }

    /// <summary>Rend au plan précédent ses valeurs avant d'écrire dans un autre. Rend la remarque à afficher, null si
    /// l'ancien plan existe encore mais a refusé.</summary>
    private string? HandOverPreviousPlan(Guid previous)
    {
        string label = PlanLabel(previous);
        SystemRestoreResult result = Restore(CoreParkingCatalog.All);
        return result.Status switch
        {
            SystemRestoreStatus.Restored => $"Le plan « {label} » a retrouvé ses valeurs d'origine : PCPerfSuite ne garde l'origine que d'un plan à la fois.",
            SystemRestoreStatus.NothingToRestore => $"Le plan « {label} », modifié auparavant, n'existe plus.",
            _ => null,
        };
    }

    private string PlanLabel(Guid scheme) => _plan.FriendlyName(scheme) ?? "plan d'alimentation";

    private CoreParkingValue? Read(Guid scheme, CoreParkingSetting setting)
        => _plan.TryRead(scheme, CoreParkingCatalog.SubGroup, setting.Guid, out uint ac, out uint dc) ? new CoreParkingValue(ac, dc) : null;

    /// <summary>Origine notée : les clés « /ac » et « /dc », avec repli sur la clé sans suffixe des premières versions
    /// du tweak, qui ne portait que la valeur secteur.</summary>
    private static CoreParkingValue? Origin(IReadOnlyDictionary<string, uint> values, CoreParkingSetting setting)
    {
        string key = CoreParkingCatalog.OriginKey(setting);
        uint? legacy = values.TryGetValue(key, out uint old) ? old : null;
        uint? ac = values.TryGetValue($"{key}/ac", out uint a) ? a : legacy;
        uint? dc = values.TryGetValue($"{key}/dc", out uint d) ? d : legacy;
        return ac is null && dc is null ? null : new CoreParkingValue(ac ?? dc!.Value, dc ?? ac!.Value);
    }

    private void ForgetSettingsBackAtOrigin(IEnumerable<(CoreParkingSetting Setting, CoreParkingValue? Applied)> applied)
    {
        IReadOnlyDictionary<string, uint> values = _origins.Values;
        ForgetAll(applied.Where(a => a.Applied is { } now && Origin(values, a.Setting) == now).Select(a => a.Setting));
    }

    private void ForgetAll(IEnumerable<CoreParkingSetting> settings)
    {
        var keys = new List<string>();
        foreach (CoreParkingSetting setting in settings)
        {
            string key = CoreParkingCatalog.OriginKey(setting);
            keys.AddRange([key, $"{key}/ac", $"{key}/dc"]);
        }

        if (keys.Count == 0) return;

        IReadOnlyDictionary<string, uint> values = _origins.Values;
        bool nothingLeft = CoreParkingCatalog.All
            .Where(s => !keys.Contains($"{CoreParkingCatalog.OriginKey(s)}/ac"))
            .All(s => Origin(values, s) is null);
        _origins.Forget(keys, forgetScheme: nothingLeft);
    }
}
