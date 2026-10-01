using PCPerfSuite.Core.SystemChanges;
using PCPerfSuite.Core.SystemInfo;

namespace PCPerfSuite.Core.PowerSettings.Animations;

/// <summary>Un réglage lu : sa valeur actuelle (null si illisible) et, si l'app l'a modifié, sa valeur d'origine.</summary>
public sealed record AnimationReading(AnimationSetting Setting, bool? Current, bool? Original)
{
    /// <summary>Modifié par l'app et pas encore revenu à l'origine.</summary>
    public bool IsChanged => Original is { } original && Current != original;
}

/// <summary>Tous les réglages lus d'un coup, et pourquoi la carte est grisée sur ce PC (null si elle ne l'est pas).</summary>
public sealed record AnimationSnapshot(IReadOnlyList<AnimationReading> Readings, string? UnavailableReason)
{
    public AnimationReading? Find(string key)
        => Readings.FirstOrDefault(r => string.Equals(r.Setting.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Vrai quand l'interrupteur général est coupé : les effets qui en dépendent ne jouent plus.</summary>
    public bool UiEffectsOff => Find(WindowsAnimationCatalog.UiEffectsKey)?.Current == false;

    public int ChangedCount => Readings.Count(r => r.IsChanged);
}

/// <summary>Ce qu'une écriture demandée a donné : la valeur relue après coup, et pourquoi elle n'a pas pris.</summary>
public sealed record AnimationWriteOutcome(AnimationSetting Setting, bool Requested, bool? Actual, string? Error)
{
    public bool Succeeded => Error is null && Actual == Requested;
}

/// <summary>Résultat d'un passage d'écriture : chaque réglage demandé, puis l'état complet relu après coup.</summary>
public sealed record AnimationApplyResult(IReadOnlyList<AnimationWriteOutcome> Outcomes, AnimationSnapshot After)
{
    public IReadOnlyList<AnimationWriteOutcome> Failures => Outcomes.Where(o => !o.Succeeded).ToList();
}

/// <summary>
/// Carte « Animations et effets » d'Optimisation Windows : lit et écrit les effets visuels de Windows, retient la
/// valeur d'origine de chacun juste avant que l'app n'y touche, et la rend sur demande ou par « Tout rétablir » (elle
/// est inscrite au registre des modifications de Windows). Ces réglages sont permanents : ils restent quand l'app se
/// ferme (D7).
///
/// Un passage (un interrupteur, le préréglage, la restauration) écrit tout ce qu'il a à écrire puis relit TOUT :
/// l'interrupteur général fait taire les effets qui en dépendent, et Windows peut refuser une valeur sans le dire.
/// Utilisable depuis n'importe quel thread ; à appeler hors du thread d'interface (chaque écriture diffuse
/// WM_SETTINGCHANGE).
/// </summary>
public sealed class WindowsAnimationSettings : ISystemChangeOwner
{
    public const string OwnerId = "animations";
    public const string OwnerTitle = "Animations et effets";

    private readonly IAnimationSettingsAccess _access;
    private readonly IAnimationOriginStore _origins;
    private readonly Func<string?> _unavailableReason;
    private readonly object _gate = new();

    /// <param name="unavailableReason">Pourquoi la carte est grisée sur ce PC, ou null : app élevée sous un autre
    /// compte que celui devant l'écran, dont elle réglerait le profil.</param>
    public WindowsAnimationSettings(IAnimationSettingsAccess access, IAnimationOriginStore origins, Func<string?> unavailableReason,
        IReadOnlyList<AnimationSetting>? settings = null)
    {
        _access = access;
        _origins = origins;
        _unavailableReason = unavailableReason;
        Settings = settings ?? WindowsAnimationCatalog.All;
    }

    /// <summary>Accès réel : SystemParametersInfo, settings.json, et le compte de la session.</summary>
    public static WindowsAnimationSettings CreateDefault()
        => new(new Win32AnimationSettingsAccess(), new AppSettingsAnimationOriginStore(), () => SessionUser.OtherProfileSettingMessage);

    public IReadOnlyList<AnimationSetting> Settings { get; }

    public string Id => OwnerId;
    public string Title => OwnerTitle;

    public string? UnavailableReason => SafeUnavailableReason();

    public AnimationSnapshot Read()
    {
        lock (_gate) return ReadUnlocked(SafeUnavailableReason());
    }

    /// <summary>Écrit les valeurs demandées (clé = <see cref="AnimationSetting.Key"/>) en un seul passage.</summary>
    public AnimationApplyResult Apply(IReadOnlyDictionary<string, bool> targets)
    {
        lock (_gate)
        {
            string? reason = SafeUnavailableReason();
            var outcomes = new List<(AnimationSetting Setting, bool Requested, string? Error)>();

            // Dans l'ordre du catalogue, quel que soit celui du dictionnaire : l'interrupteur général passe en premier.
            foreach (AnimationSetting setting in Settings)
            {
                if (!TryGetTarget(targets, setting.Key, out bool requested)) continue;
                outcomes.Add((setting, requested, reason ?? Write(setting, requested, rememberOrigin: true)));
            }

            return Finish(outcomes, reason);
        }
    }

    /// <summary>Préréglage « Réactif » : coupe toutes les animations, ombres et la transparence, en un passage. Le
    /// lissage des polices n'est pas sur la carte, et l'interrupteur général reste tel quel : le couper retirerait aussi
    /// des effets que la carte ne montre pas (suivi du survol, dégradés des titres).</summary>
    public AnimationApplyResult ApplyResponsivePreset()
        => Apply(Settings.Where(s => s.InResponsivePreset).ToDictionary(s => s.Key, _ => false, StringComparer.OrdinalIgnoreCase));

    public bool HasChanges
    {
        get
        {
            try
            {
                lock (_gate) return ReadUnlocked(null).ChangedCount > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public IReadOnlyList<SystemChange> Describe()
    {
        try
        {
            lock (_gate)
            {
                string? reason = SafeUnavailableReason();
                return ReadUnlocked(reason).Readings.Where(r => r.IsChanged).Select(r => ToChange(r, reason)).ToList();
            }
        }
        catch (Exception ex)
        {
            return new[] { new SystemChange(OwnerTitle, $"État illisible ({ex.Message}).", CanRestore: false) };
        }
    }

    /// <summary>Rend à chaque réglage modifié sa valeur d'origine, puis relit tout. Même chemin que le bouton
    /// « Rétablir mes réglages d'origine ». Ne lève jamais.</summary>
    public SystemRestoreResult RestoreAll()
    {
        try
        {
            lock (_gate) return RestoreUnlocked();
        }
        catch (Exception ex)
        {
            return new SystemRestoreResult(SystemRestoreStatus.Failed, Describe(), $"Rétablissement impossible ({ex.Message}).");
        }
    }

    private SystemRestoreResult RestoreUnlocked()
    {
        string? reason = SafeUnavailableReason();
        AnimationSnapshot before = ReadUnlocked(reason);
        List<AnimationReading> changed = before.Readings.Where(r => r.IsChanged).ToList();

        // Sous un autre compte, SPI rend l'état de la session affichée et settings.json est celui de l'administrateur :
        // comparer les deux ne dit rien, on n'oublie aucune origine.
        if (reason is not null)
        {
            return changed.Count == 0
                ? SystemRestoreResult.Nothing
                : new SystemRestoreResult(SystemRestoreStatus.Failed, changed.Select(r => ToChange(r, reason)).ToList(), reason);
        }

        // Revenus à l'origine par un autre chemin (Paramètres de Windows) : il n'y a plus rien à rendre.
        ForgetRestored(before);
        if (changed.Count == 0) return SystemRestoreResult.Nothing;

        var outcomes = changed.Select(r => (r.Setting, r.Original!.Value, Write(r.Setting, r.Original.Value, rememberOrigin: false))).ToList();
        AnimationApplyResult result = Finish(outcomes, reason);

        List<SystemChange> notRestored = result.After.Readings.Where(r => r.IsChanged).Select(r => ToChange(r, reason)).ToList();
        if (notRestored.Count == 0) return new SystemRestoreResult(SystemRestoreStatus.Restored, Array.Empty<SystemChange>(), null);

        string message = string.Join(" ", result.Failures.Select(f => $"{f.Setting.Name} : {f.Error ?? "Windows a gardé la valeur actuelle."}"));
        SystemRestoreStatus status = notRestored.Count < changed.Count ? SystemRestoreStatus.Partial : SystemRestoreStatus.Failed;
        return new SystemRestoreResult(status, notRestored, message);
    }

    /// <summary>Écrit un réglage, en retenant d'abord sa valeur d'origine si demandé. Rend l'erreur à afficher, ou
    /// null. Une valeur déjà en place n'est pas réécrite : pas de diffusion pour rien.</summary>
    private string? Write(AnimationSetting setting, bool requested, bool rememberOrigin)
    {
        bool? current = _access.Read(setting);
        if (current == requested) return null;

        if (rememberOrigin)
        {
            if (current is not { } original)
            {
                return "Windows ne rend pas l'état actuel de ce réglage : PCPerfSuite n'y touche pas, faute de pouvoir le rétablir.";
            }

            if (!_origins.TryRemember(setting.Key, original))
            {
                return "L'état d'origine n'a pas pu être enregistré (voir Paramètres › Compatibilité de ce PC) : "
                    + "PCPerfSuite n'y touche pas, faute de pouvoir le rétablir.";
            }
        }

        try
        {
            _access.Write(setting, requested);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Relit tout, oublie l'origine des réglages qui y sont revenus, et compare chaque demande à ce qui est
    /// relu.</summary>
    private AnimationApplyResult Finish(IReadOnlyList<(AnimationSetting Setting, bool Requested, string? Error)> writes, string? reason)
    {
        AnimationSnapshot after = ReadUnlocked(reason);
        if (reason is null)
        {
            ForgetRestored(after);
            after = ReadUnlocked(reason);
        }

        var outcomes = writes.Select(w =>
        {
            bool? actual = after.Find(w.Setting.Key)?.Current;
            string? error = w.Error ?? (actual == w.Requested ? null : NotTakenMessage(actual));
            return new AnimationWriteOutcome(w.Setting, w.Requested, actual, error);
        }).ToList();

        return new AnimationApplyResult(outcomes, after);
    }

    private void ForgetRestored(AnimationSnapshot snapshot)
    {
        // Une clé inconnue du catalogue (réglage retiré dans une version suivante) ne reviendra jamais : oubliée aussi.
        IReadOnlyDictionary<string, bool> origins = _origins.Load();
        List<string> restored = origins.Keys
            .Where(key => snapshot.Find(key) is not { } r || (r.Current is { } current && current == r.Original))
            .ToList();
        _origins.Forget(restored);
    }

    private AnimationSnapshot ReadUnlocked(string? reason)
    {
        IReadOnlyDictionary<string, bool> origins = _origins.Load();
        var readings = Settings
            .Select(s => new AnimationReading(s, _access.Read(s), TryGetTarget(origins, s.Key, out bool o) ? o : null))
            .ToList();
        return new AnimationSnapshot(readings, reason);
    }

    private static bool TryGetTarget(IReadOnlyDictionary<string, bool> values, string key, out bool value)
    {
        foreach (KeyValuePair<string, bool> pair in values)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = false;
        return false;
    }

    /// <summary>Sans les noms de compte de <paramref name="reason"/> : ce texte peut finir dans un rapport copié.</summary>
    private static SystemChange ToChange(AnimationReading reading, string? reason)
        => new(reading.Setting.Name,
            $"Valeur d'origine : {OnOff(reading.Original)} ; actuelle : {OnOff(reading.Current)}."
            + (reason is null ? "" : $" {OtherAccountNote}"),
            CanRestore: reason is null);

    /// <summary>Raison de la carte grisée, sans nom de compte, pour les textes qui peuvent quitter le PC.</summary>
    public const string OtherAccountNote =
        "PCPerfSuite tourne sous un autre compte que la personne connectée : rien n'est modifié ni rétabli d'ici.";

    private static string NotTakenMessage(bool? actual)
        => actual is null
            ? "Windows ne rend plus l'état de ce réglage après l'écriture."
            : $"Windows a gardé ce réglage {OnOff(actual)}.";

    public static string OnOff(bool? value) => value switch
    {
        true => "activé",
        false => "désactivé",
        null => "illisible",
    };

    private string? SafeUnavailableReason()
    {
        try { return _unavailableReason(); }
        catch { return null; }
    }
}
