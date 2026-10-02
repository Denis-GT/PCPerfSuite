using System.Globalization;

namespace PCPerfSuite.Core.Profiles;

/// <summary>Une règle par application qui correspond à l'application au premier plan : elle vise un usage, ou un groupe
/// précis. <paramref name="Label"/> est le nom de l'application (donnée personnelle : page et journal local seulement).</summary>
public sealed record UsageRuleTarget(string RuleId, string? Usage, string? GroupId, string Label);

/// <summary>Un relevé vu par le classifieur. Une charge est null quand son groupe de capteurs n'a pas été relu par ce
/// relevé (mode éco, GPU muet) : elle ne compte alors pas, plutôt que de compter pour une vieille valeur.</summary>
/// <param name="AppKey">Chemin normalisé de l'application au premier plan, null sans application ou s'il est illisible.</param>
public sealed record UsageSample(
    DateTimeOffset TimeUtc,
    string? AppKey,
    bool IsFullscreen,
    bool IsExclusiveFullscreen,
    float? CpuLoad,
    float? GpuLoad,
    double? RtssFps,
    bool OnBattery,
    UsageRuleTarget? Rule = null);

/// <summary>Pourquoi le classifieur conclut à un usage.</summary>
public enum UsageReasonKind
{
    /// <summary>Premier relevé, ou aucun signe de jeu ni de charge lourde.</summary>
    NoGame,

    /// <summary>RTSS mesure des images pour l'application au premier plan, en plein écran ou avec le GPU chargé.</summary>
    Rtss,

    ExclusiveFullscreen,

    /// <summary>Fenêtre sans bord qui couvre son écran, GPU chargé.</summary>
    BorderlessFullscreen,

    /// <summary>GPU chargé longtemps, même en fenêtre (jeu fenêtré, charge graphique).</summary>
    GpuLoad,

    /// <summary>Le jeu détecté reste au premier plan en plein écran (menu, pause) : il le reste.</summary>
    StickyFullscreen,

    /// <summary>Processeur chargé longtemps hors jeu (compilation, rendu, encodage).</summary>
    CpuLoad,

    /// <summary>Une règle par application.</summary>
    Rule,

    /// <summary>Sur batterie, le jeu exigeant est ramené au jeu léger.</summary>
    BatteryCap,
}

/// <summary>Ce que vise un verdict : un usage (<see cref="ProfileGroupUsage"/>), ou le groupe d'une règle.</summary>
public readonly record struct UsageTarget(string? Usage, string? GroupId)
{
    public static UsageTarget Office => new(ProfileGroupUsage.Office, null);

    public bool IsGaming => Usage is ProfileGroupUsage.LightGaming or ProfileGroupUsage.HeavyGaming;
}

/// <summary>Le verdict stable : l'usage retenu, depuis quand, et pourquoi (texte sans nom d'application).</summary>
public sealed record UsageVerdict(UsageTarget Target, UsageReasonKind Reason, string Detail, DateTimeOffset SinceUtc, UsageRuleTarget? Rule = null)
{
    public string? Usage => Target.Usage;
}

/// <summary>Un changement en attente de son délai de maintien.</summary>
public sealed record UsagePending(UsageTarget Target, UsageReasonKind Reason, string Detail, DateTimeOffset SinceUtc, TimeSpan Hold)
{
    public DateTimeOffset DueUtc => SinceUtc + Hold;
}

/// <summary>Seuils et délais du classifieur, à calibrer sur de vraies machines (règle 6 : expérimental).</summary>
public sealed record UsageThresholds
{
    public static UsageThresholds Default { get; } = new();

    /// <summary>Entrée en jeu : signe de jeu tenu ce temps-là.</summary>
    public TimeSpan EnterGaming { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Sortie du jeu : aucun signe pendant ce temps-là (menus, chargements, alt-tab court).</summary>
    public TimeSpan ExitGaming { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan EnterHeavy { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ExitHeavy { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan EnterRule { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ExitRule { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Passage sur batterie : le plafond « jeu léger » s'applique plus vite qu'une baisse de charge.</summary>
    public TimeSpan BatteryCap { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Interruption tolérée d'un changement en attente (un creux de charge de deux relevés).</summary>
    public TimeSpan PendingGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Trou entre deux relevés au-delà duquel on repart de zéro (veille, app figée).</summary>
    public TimeSpan Gap { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Poids maximal d'une mesure dans les moyennes (charges relues toutes les 5 à 6 s en mode éco).</summary>
    public TimeSpan MaxSampleWeight { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Une charge ou des FPS lus il y a moins que cela valent encore pour les signes instantanés (mode éco).</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan LoadWindow { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CpuSustainedWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Part de la fenêtre qui doit être couverte de relevés pour qu'une moyenne compte.</summary>
    public double MinCoverage { get; init; } = 0.5;

    public float RtssGpuPercent { get; init; } = 40;
    public float BorderlessGpuPercent { get; init; } = 50;
    public float WindowedGpuPercent { get; init; } = 60;
    public float HeavyGpuEnterPercent { get; init; } = 85;
    public float HeavyGpuExitPercent { get; init; } = 70;
    public float HeavyCpuEnterPercent { get; init; } = 75;
    public float HeavyCpuExitPercent { get; init; } = 60;

    /// <summary>Charge processeur soutenue (moyenne sur <see cref="CpuSustainedWindow"/>) qui vaut « jeu exigeant » hors jeu.</summary>
    public float SustainedCpuPercent { get; init; } = 75;
}

/// <summary>
/// Classe l'usage du PC en bureautique, jeu léger ou jeu exigeant, en logique pure : chaque relevé donne un candidat,
/// qui ne devient le verdict qu'après son délai de maintien. Moteur déterministe et explicable (D3) : chaque verdict dit
/// pourquoi, sans nom d'application.
///
/// <list type="bullet">
/// <item>Une règle par application l'emporte (entrée 10 s, sortie 2 min).</item>
/// <item>Signes de jeu : images mesurées par RTSS avec plein écran ou GPU chargé (RTSS accroche aussi les navigateurs) ;
/// plein écran exclusif ; plein écran sans bord avec GPU chargé ; GPU chargé longtemps en fenêtre ; le jeu détecté qui
/// reste au premier plan en plein écran (menu, pause).</item>
/// <item>Exigeant si le GPU ou le processeur est très chargé (moyennes sur 60 s, avec hystérésis) ; une charge
/// processeur soutenue hors jeu (2 min) aussi. Sur batterie, l'exigeant est ramené au léger (pas une règle).</item>
/// <item>Entrée en jeu après 30 s, sortie après 2 min, léger ↔ exigeant après 60 s / 2 min ; un creux de 5 s ne remet
/// pas l'attente à zéro ; un trou de plus de 60 s entre deux relevés (veille) la remet à zéro.</item>
/// </list>
/// Seuils et délais expérimentaux (<see cref="UsageThresholds"/>). À appeler depuis un seul fil ; ne lève jamais.
/// </summary>
public sealed class UsageClassifier
{
    private readonly UsageThresholds _t;
    private readonly TimeWeightedMean _gpu60;
    private readonly TimeWeightedMean _cpu60;
    private readonly TimeWeightedMean _cpu120;
    private readonly RecentReading _gpu = new();
    private readonly RecentReading _cpu = new();
    private readonly RecentReading _fps = new();

    private DateTimeOffset? _lastSampleUtc;
    private UsagePending? _pending;
    private DateTimeOffset _pendingLastSeenUtc;

    /// <summary>L'application qui a fait entrer en jeu : la garder au premier plan en plein écran y maintient.</summary>
    private string? _gamingAppKey;

    public UsageClassifier(UsageThresholds? thresholds = null)
    {
        _t = thresholds ?? UsageThresholds.Default;
        _gpu60 = new TimeWeightedMean(_t.LoadWindow);
        _cpu60 = new TimeWeightedMean(_t.LoadWindow);
        _cpu120 = new TimeWeightedMean(_t.CpuSustainedWindow);
    }

    /// <summary>Le verdict stable, null avant le premier relevé.</summary>
    public UsageVerdict? Current { get; private set; }

    /// <summary>Le changement qui attend son délai, null sinon.</summary>
    public UsagePending? Pending => _pending;

    /// <summary>Le candidat du dernier relevé (ce que le relevé seul dirait), pour l'affichage.</summary>
    public UsageVerdict? LastCandidate { get; private set; }

    /// <summary>Moyennes glissantes de charge (null si la fenêtre n'est pas assez couverte).</summary>
    public double? GpuMean => _gpu60.Mean(_t.LoadWindow.TotalSeconds * _t.MinCoverage);

    public double? CpuMean => _cpu60.Mean(_t.LoadWindow.TotalSeconds * _t.MinCoverage);

    /// <summary>Prend un relevé ; renvoie vrai si le verdict stable a changé.</summary>
    public bool Add(UsageSample sample)
    {
        try
        {
            DateTimeOffset now = sample.TimeUtc;
            if (_lastSampleUtc is { } last)
            {
                TimeSpan elapsed = now - last;
                if (elapsed < TimeSpan.Zero || elapsed > _t.Gap)
                {
                    // Veille, horloge changée, app figée : les moyennes et l'attente ne valent plus rien.
                    ResetWindows();
                }
            }

            _lastSampleUtc = now;

            // Chaque mesure compte pour le temps écoulé depuis SA lecture précédente : en mode éco, les charges ne sont
            // relues qu'un relevé sur plusieurs, les autres relevés n'apportant que le premier plan.
            if (sample.GpuLoad is { } gpu && float.IsFinite(gpu))
            {
                double value = Math.Clamp(gpu, 0, 100);
                _gpu60.Add(now, _gpu.WeightAt(now, _t), value);
                _gpu.Set(value, now);
            }

            if (sample.CpuLoad is { } cpu && float.IsFinite(cpu))
            {
                double value = Math.Clamp(cpu, 0, 100);
                double weight = _cpu.WeightAt(now, _t);
                _cpu60.Add(now, weight, value);
                _cpu120.Add(now, weight, value);
                _cpu.Set(value, now);
            }

            if (sample.RtssFps is { } fps && double.IsFinite(fps)) _fps.Set(fps, now);

            _gpu60.Trim(now);
            _cpu60.Trim(now);
            _cpu120.Trim(now);

            UsageVerdict candidate = Candidate(sample);
            LastCandidate = candidate;

            bool initial = false;
            if (Current is null)
            {
                // Premier relevé : on part de la bureautique (une entrée en jeu attend son délai comme toute autre).
                Current = new UsageVerdict(UsageTarget.Office, UsageReasonKind.NoGame, "premier relevé", now);
                _gamingAppKey = null;
                initial = true;
            }

            if (candidate.Target == Current.Target)
            {
                // Le verdict se confirme : un changement en attente n'est abandonné qu'après le creux toléré.
                if (_pending is not null && now - _pendingLastSeenUtc > _t.PendingGrace) _pending = null;
                if (Current.Target.IsGaming && IsStrongGameSign(candidate.Reason) && sample.AppKey is { } key) _gamingAppKey = key;
                return initial;
            }

            // Entrée en jeu en cours : passer de léger à exigeant (la moyenne de charge vient d'être assez couverte) ne
            // relance pas l'attente, c'est la même entrée en jeu.
            bool sameEntry = _pending is not null && !Current.Target.IsGaming && _pending.Target.IsGaming && candidate.Target.IsGaming
                             && _pending.Reason != UsageReasonKind.Rule && candidate.Reason != UsageReasonKind.Rule;
            if (_pending is null || now - _pendingLastSeenUtc > _t.PendingGrace || (_pending.Target != candidate.Target && !sameEntry))
            {
                _pending = new UsagePending(candidate.Target, candidate.Reason, candidate.Detail, now, Hold(Current, candidate));
            }
            else
            {
                // Même changement toujours voulu : la cible et la raison les plus récentes seront celles du verdict.
                _pending = _pending with { Target = candidate.Target, Reason = candidate.Reason, Detail = candidate.Detail };
            }

            _pendingLastSeenUtc = now;
            if (now < _pending.DueUtc) return initial;

            Current = new UsageVerdict(_pending.Target, _pending.Reason, _pending.Detail, now, candidate.Rule);
            _gamingAppKey = Current.Target.IsGaming ? sample.AppKey : null;
            _pending = null;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Oublie l'attente et les moyennes (trou entre deux relevés) sans changer le verdict.</summary>
    public void ResetWindows()
    {
        _gpu60.Clear();
        _cpu60.Clear();
        _cpu120.Clear();
        _gpu.Clear();
        _cpu.Clear();
        _fps.Clear();
        _pending = null;
    }

    /// <summary>Oublie tout, verdict compris (réveil, réactivation) : le verdict d'avant ne vaut plus rien, et le
    /// relevé suivant repart de la bureautique ; un jeu toujours là est reconnu après son délai d'entrée.</summary>
    public void Reset()
    {
        ResetWindows();
        Current = null;
        LastCandidate = null;
        _gamingAppKey = null;
        _lastSampleUtc = null;
    }

    private static bool IsStrongGameSign(UsageReasonKind reason)
        => reason is UsageReasonKind.Rtss or UsageReasonKind.ExclusiveFullscreen or UsageReasonKind.BorderlessFullscreen;

    private TimeSpan Hold(UsageVerdict from, UsageVerdict to)
    {
        if (to.Reason == UsageReasonKind.Rule) return _t.EnterRule;
        if (from.Reason == UsageReasonKind.Rule) return _t.ExitRule;
        if (to.Reason == UsageReasonKind.BatteryCap) return _t.BatteryCap;

        bool fromGaming = from.Target.IsGaming, toGaming = to.Target.IsGaming;
        if (!fromGaming && toGaming) return _t.EnterGaming;
        if (fromGaming && !toGaming) return _t.ExitGaming;
        if (to.Target.Usage == ProfileGroupUsage.HeavyGaming) return _t.EnterHeavy;
        if (from.Target.Usage == ProfileGroupUsage.HeavyGaming) return _t.ExitHeavy;
        return _t.EnterGaming;
    }

    private UsageVerdict Candidate(UsageSample sample)
    {
        DateTimeOffset now = sample.TimeUtc;
        if (sample.Rule is { } rule && (rule.Usage is not null || rule.GroupId is not null))
        {
            return new UsageVerdict(new UsageTarget(rule.Usage, rule.GroupId), UsageReasonKind.Rule, "règle d'application", now, rule);
        }

        double? gpuMean = GpuMean;
        double? cpuMean = CpuMean;

        // Valeurs instantanées : celles du relevé, sinon la dernière lue s'il y a moins de StaleAfter (mode éco).
        double? gpu = _gpu.FreshAt(now, _t.StaleAfter);
        double? fps = _fps.FreshAt(now, _t.StaleAfter);

        (UsageReasonKind Kind, string Detail)? sign = null;
        if (fps is > 0 and var rate && (sample.IsFullscreen || gpu >= _t.RtssGpuPercent))
        {
            sign = (UsageReasonKind.Rtss, $"RTSS : {rate.ToString("0", CultureInfo.InvariantCulture)} images/s au premier plan");
        }
        else if (sample.IsExclusiveFullscreen)
        {
            sign = (UsageReasonKind.ExclusiveFullscreen, "application Direct3D en plein écran exclusif");
        }
        else if (sample.IsFullscreen && gpu >= _t.BorderlessGpuPercent)
        {
            sign = (UsageReasonKind.BorderlessFullscreen, $"plein écran, GPU à {Percent(gpu!.Value)}");
        }
        else if (gpuMean >= _t.WindowedGpuPercent)
        {
            sign = (UsageReasonKind.GpuLoad, $"GPU à {Percent(gpuMean!.Value)} en moyenne sur {Seconds(_t.LoadWindow)}");
        }
        else if (Current?.Target.IsGaming == true && sample.IsFullscreen && sample.AppKey is { } key
                 && string.Equals(key, _gamingAppKey, StringComparison.OrdinalIgnoreCase))
        {
            sign = (UsageReasonKind.StickyFullscreen, "le jeu reste au premier plan en plein écran");
        }

        double? sustainedCpu = _cpu120.Mean(_t.CpuSustainedWindow.TotalSeconds * _t.MinCoverage);
        if (sign is null)
        {
            if (sustainedCpu >= _t.SustainedCpuPercent)
            {
                return Capped(sample, new UsageVerdict(new UsageTarget(ProfileGroupUsage.HeavyGaming, null), UsageReasonKind.CpuLoad,
                    $"processeur à {Percent(sustainedCpu!.Value)} en moyenne sur {Seconds(_t.CpuSustainedWindow)}", now));
            }

            return new UsageVerdict(UsageTarget.Office, UsageReasonKind.NoGame, "aucun signe de jeu ni de charge lourde", now);
        }

        bool wasHeavy = Current?.Target.Usage == ProfileGroupUsage.HeavyGaming;
        bool heavy = wasHeavy
            ? gpuMean >= _t.HeavyGpuExitPercent || cpuMean >= _t.HeavyCpuExitPercent || sustainedCpu >= _t.SustainedCpuPercent
            : gpuMean >= _t.HeavyGpuEnterPercent || cpuMean >= _t.HeavyCpuEnterPercent;

        string detail = sign.Value.Detail;
        if (heavy)
        {
            detail += gpuMean >= cpuMean
                ? $" ; GPU à {Percent(gpuMean ?? 0)} en moyenne"
                : $" ; processeur à {Percent(cpuMean ?? 0)} en moyenne";
        }

        var verdict = new UsageVerdict(
            new UsageTarget(heavy ? ProfileGroupUsage.HeavyGaming : ProfileGroupUsage.LightGaming, null), sign.Value.Kind, detail, now);
        return Capped(sample, verdict);
    }

    /// <summary>Sur batterie, un jeu exigeant détecté est ramené au jeu léger (autonomie, chauffe).</summary>
    private static UsageVerdict Capped(UsageSample sample, UsageVerdict verdict)
        => sample.OnBattery && verdict.Target.Usage == ProfileGroupUsage.HeavyGaming
            ? verdict with
            {
                Target = new UsageTarget(ProfileGroupUsage.LightGaming, null),
                Reason = UsageReasonKind.BatteryCap,
                Detail = $"sur batterie, jeu exigeant ramené au jeu léger ({verdict.Detail})",
            }
            : verdict;

    private static string Percent(double value) => $"{value.ToString("0", CultureInfo.InvariantCulture)} %";

    private static string Seconds(TimeSpan span)
        => span.TotalSeconds >= 120 && span.TotalSeconds % 60 == 0
            ? $"{(span.TotalSeconds / 60).ToString("0", CultureInfo.InvariantCulture)} min"
            : $"{span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";
}

/// <summary>Moyenne glissante pondérée par le temps : chaque relevé compte pour la durée qu'il représente, pour que des
/// relevés espacés (mode éco) ne pèsent pas moins que des relevés serrés.</summary>
internal sealed class TimeWeightedMean
{
    private readonly record struct Point(DateTimeOffset Time, double Weight, double Value);

    private readonly TimeSpan _span;
    private readonly Queue<Point> _points = new();
    private double _sum;
    private double _weight;

    public TimeWeightedMean(TimeSpan span) => _span = span;

    public void Add(DateTimeOffset time, double weight, double value)
    {
        _points.Enqueue(new Point(time, weight, value));
        _sum += weight * value;
        _weight += weight;
    }

    public void Trim(DateTimeOffset now)
    {
        while (_points.Count > 0 && now - _points.Peek().Time > _span)
        {
            Point old = _points.Dequeue();
            _sum -= old.Weight * old.Value;
            _weight -= old.Weight;
        }

        if (_points.Count == 0) _sum = _weight = 0;
    }

    /// <summary>La moyenne, null si moins de <paramref name="minWeight"/> secondes de relevés.</summary>
    public double? Mean(double minWeight) => _weight >= minWeight && _weight > 0 ? _sum / _weight : null;

    public void Clear()
    {
        _points.Clear();
        _sum = _weight = 0;
    }
}

/// <summary>La dernière lecture d'une mesure et son heure : poids de la lecture suivante, et valeur encore fraîche pour un
/// relevé qui ne l'a pas relue (mode éco).</summary>
internal sealed class RecentReading
{
    private double? _value;
    private DateTimeOffset? _at;

    public void Set(double value, DateTimeOffset at)
    {
        _value = value;
        _at = at;
    }

    public void Clear()
    {
        _value = null;
        _at = null;
    }

    /// <summary>Le temps que représente une lecture faite à <paramref name="now"/> : l'écart depuis la précédente, borné
    /// (1 s pour la première).</summary>
    public double WeightAt(DateTimeOffset now, UsageThresholds thresholds)
        => _at is { } at && now > at
            ? Math.Clamp((now - at).TotalSeconds, 0.05, thresholds.MaxSampleWeight.TotalSeconds)
            : 1;

    /// <summary>La dernière valeur si elle a moins de <paramref name="staleAfter"/>, sinon null.</summary>
    public double? FreshAt(DateTimeOffset now, TimeSpan staleAfter)
        => _at is { } at && now >= at && now - at <= staleAfter ? _value : null;
}
