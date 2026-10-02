using System.Globalization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Fans;
using PCPerfSuite.Core.Hardware.Gpu;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.Safety;

namespace PCPerfSuite.Core.Profiles;

/// <summary>
/// Emplacement OC des groupes générés : ce qu'une recherche d'OC propose pour un usage (partie GPU, et l'explication).
/// Vide dans #9, qui n'invente aucune valeur d'OC : l'OC automatique GPU (#15) y mettra ses profils sûr / classique pour
/// les groupes de jeu, et le Curve Optimizer AMD (#16) y ajoutera sa partie processeur (nouveau membre).
/// </summary>
public sealed record UsageOverclock(ProfileGroupGpuPart? Gpu, string? Explanation);

/// <summary>Source de l'emplacement OC (docs/decisions.md). Un usage sans proposition rend null.</summary>
public interface IUsageOverclockSource
{
    UsageOverclock? For(string usage, GpuTargetState gpu);
}

/// <summary>Emplacement OC vide, jusqu'à #15 et #16.</summary>
public sealed class EmptyUsageOverclockSource : IUsageOverclockSource
{
    public static EmptyUsageOverclockSource Instance { get; } = new();

    public UsageOverclock? For(string usage, GpuTargetState gpu) => null;
}

/// <summary>Réglages enregistrés dans les onglets Processeur et GPU (ceux que « Appliquer au démarrage » repose) :
/// les groupes de jeu les reprennent (décision de Denis du 01/10/2026), la bureautique ne dépasse pas les watts.</summary>
public sealed record SavedTabTuning(
    float? CpuSustainedWatts,
    float? CpuBurstWatts,
    bool CpuApplyAtStartup,
    GpuOverclockProfile? GpuOverclock,
    GpuIdentity? GpuIdentity,
    bool GpuApplyAtStartup)
{
    public static SavedTabTuning None { get; } = new(null, null, false, null, null, false);

    public static SavedTabTuning From(AppSettings settings)
    {
        GpuControlSettings gpu = settings.Gpu;
        (int Value, GpuVoltageUnit Unit)? voltage = gpu.GetVoltage();
        return new SavedTabTuning(
            settings.Cpu.SustainedWatts,
            settings.Cpu.BurstWatts,
            settings.Cpu.ApplyAtStartup,
            new GpuOverclockProfile
            {
                Name = "onglet GPU",
                CoreClockOffsetMhz = gpu.CoreClockOffsetMhz,
                MemoryClockOffsetMhz = gpu.MemoryClockOffsetMhz,
                PowerLimitPercent = gpu.PowerLimitPercent,
                TemperatureLimitC = gpu.TemperatureLimitC,
                VoltageValue = voltage?.Value,
                VoltageUnit = voltage?.Unit,
            },
            gpu.OverclockGpu,
            gpu.ApplyOverclockAtStartup);
    }
}

/// <summary>Tout ce que le générateur lit, relevé au moment de générer.</summary>
/// <param name="IncludeBiosFans">Prendre aussi en main les ventilateurs laissés au BIOS (case décochée par défaut).</param>
public sealed record UsageGenerationInput(
    CpuTargetState Cpu,
    GpuTargetState Gpu,
    FanTargetState Fans,
    SavedTabTuning Saved,
    Func<string, UsageStats> Stats,
    bool IncludeBiosFans,
    IUsageOverclockSource Overclock,
    DateTimeOffset Now);

/// <summary>Un groupe produit par le générateur, et ce qu'on en dit.</summary>
/// <param name="Created">Nouveau groupe (sinon un groupe généré existant, mis à jour).</param>
public sealed record GeneratedUsageGroup(string Usage, ProfileGroup Group, IReadOnlyList<string> Explanation, bool Created);

/// <summary>Un usage que le générateur n'a pas touché, et pourquoi (groupe modifié à la main, autre groupe).</summary>
public sealed record SkippedUsage(string Usage, string Reason);

public sealed record UsageGenerationResult(IReadOnlyList<GeneratedUsageGroup> Groups, IReadOnlyList<SkippedUsage> Skipped);

/// <summary>
/// Génère les trois groupes de la bascule automatique (bureautique, jeu léger, jeu exigeant), en logique pure et
/// explicable (D3) : chaque partie dit d'où elle vient. Aucune valeur d'OC inventée.
///
/// <list type="bullet">
/// <item>Ventilation : seulement les ventilateurs listés par l'onglet (sur un portable, ceux de la carte graphique :
/// règle 5), et seulement ceux déjà en courbe ou en manuel, sauf si l'utilisateur demande les autres. Courbes
/// Silencieux / Équilibré / Perf, décalées d'après les températures relevées pour l'usage (vers plus de refroidissement
/// jusqu'à 5 °C, vers moins au plus 3 °C, jamais à chaud), et toutes à 100 % à chaud.</item>
/// <item>Processeur : préférence performance / économie (EPP) par usage ; watts seulement si l'onglet sait les écrire, que
/// son avertissement a été accepté et que l'origine n'est pas « sans limite » : bureautique à 65 % de l'origine (jamais
/// au-dessus des watts enregistrés), jeu aux watts enregistrés dans l'onglet sinon à l'origine.</item>
/// <item>Carte graphique : bureautique d'origine (jamais « ne pas toucher », sinon l'OC d'un groupe de jeu resterait) ;
/// jeu = l'emplacement OC, sinon l'overclock enregistré dans l'onglet GPU sur cette même carte, sinon rien.</item>
/// </list>
/// Régénérer garde l'identifiant des groupes générés non modifiés, ne touche jamais un groupe modifié à la main
/// (<see cref="ProfileGroup.EditedByUser"/>), et ne crée pas un usage qu'un autre groupe couvre déjà.
/// </summary>
public static class UsageProfileGenerator
{
    public const string NameSuffix = " (auto)";

    /// <summary>Historique minimal d'un usage pour décaler ses courbes : une heure, dont une demi-heure de températures.</summary>
    public static readonly TimeSpan MinHistoryForShift = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinTemperatureHistory = TimeSpan.FromMinutes(30);

    /// <summary>Part de l'origine retenue pour les watts de la bureautique.</summary>
    public const float OfficeWattsShare = 0.65f;

    /// <summary>EPP par usage (0 = performance, 100 = économie), sur secteur puis sur batterie. Expérimental.</summary>
    public static (uint Ac, uint Dc) Epp(string usage) => usage switch
    {
        ProfileGroupUsage.Office => (50, 70),
        ProfileGroupUsage.LightGaming => (25, 50),
        _ => (0, 33),
    };

    private static readonly string[] EppSettingIds = ["epp", "epp-perf"];

    public static UsageGenerationResult Generate(IReadOnlyList<ProfileGroup> existing, UsageGenerationInput input)
    {
        var groups = new List<GeneratedUsageGroup>();
        var skipped = new List<SkippedUsage>();
        var takenNames = existing.Select(g => g.Name).ToList();

        foreach (string usage in ProfileGroupUsage.All)
        {
            List<ProfileGroup> covering = existing.Where(g => string.Equals(g.Usage, usage, StringComparison.Ordinal)).ToList();
            ProfileGroup? target = covering.FirstOrDefault(g => g.IsGenerated && !g.EditedByUser);
            if (target is null && covering.Count > 0)
            {
                ProfileGroup kept = covering[0];
                skipped.Add(new SkippedUsage(usage, kept.IsGenerated
                    ? $"« {kept.Name} » a été modifié à la main : il est gardé tel quel"
                    : $"« {kept.Name} » couvre déjà cet usage"));
                continue;
            }

            (ProfileGroup built, List<string> explanation) = Build(usage, input);
            bool created = target is null;
            if (target is null)
            {
                string name = ProfileGroupEditor.UniqueName((ProfileGroupUsage.Label(usage) ?? usage) + NameSuffix, takenNames);
                takenNames.Add(name);
                target = new ProfileGroup
                {
                    Name = name,
                    Usage = usage,
                    Origin = ProfileGroupOrigin.Generated,
                    CreatedUtc = input.Now,
                    Revision = 0,
                };
            }

            ProfileGroup? previous = created ? null : target;
            if (previous is not null) target = target.Clone();

            // Mise à jour par le générateur : la révision monte si le contenu change, mais le groupe n'est pas « modifié à
            // la main ». Inchangé, il garde sa révision : la bascule ne le reposerait que pour rien.
            target.Cpu = built.Cpu;
            target.Gpu = built.Gpu;
            target.Fans = built.Fans;
            if (target.Fans?.Values is { } fans) fans.Name = target.Name;
            if (target.Cpu?.Values is { } cpu) cpu.Name = target.Name;
            if (target.Gpu?.Values is { } gpu) gpu.Name = target.Name;
            // Un renommage ne recopie pas le nom dans les parties enregistrées : ce n'est pas un changement de contenu.
            ProfileGroup? before = previous?.Clone();
            if (before?.Fans?.Values is { } beforeFans) beforeFans.Name = target.Name;
            if (before?.Cpu?.Values is { } beforeCpu) beforeCpu.Name = target.Name;
            if (before?.Gpu?.Values is { } beforeGpu) beforeGpu.Name = target.Name;
            bool changed = before is null
                           || !ProfileGroupJson.SameContent(before.Cpu, target.Cpu)
                           || !ProfileGroupJson.SameContent(before.Gpu, target.Gpu)
                           || !ProfileGroupJson.SameContent(before.Fans, target.Fans);
            if (previous is not null && changed) target.Revision++;
            if (changed) target.UpdatedUtc = input.Now;
            target.EditedByUser = false;
            groups.Add(new GeneratedUsageGroup(usage, target, explanation, created));
        }

        return new UsageGenerationResult(groups, skipped);
    }

    private static (ProfileGroup Group, List<string> Explanation) Build(string usage, UsageGenerationInput input)
    {
        var explanation = new List<string>();
        var group = new ProfileGroup
        {
            Fans = BuildFans(usage, input, explanation),
            Cpu = BuildCpu(usage, input, explanation),
            Gpu = BuildGpu(usage, input, explanation),
        };

        return (group, explanation);
    }

    // ---- Ventilation ----

    private static ProfileGroupFansPart? BuildFans(string usage, UsageGenerationInput input, List<string> explanation)
    {
        FanTargetState state = input.Fans;
        if (!state.IsReady)
        {
            explanation.Add("Ventilation : non touchée, aucun relevé des ventilateurs au moment de générer (régénère plus tard).");
            return null;
        }

        if (state.Fans.Count == 0)
        {
            explanation.Add($"Ventilation : non touchée, {Lower(state.NoFansReason)}");
            return null;
        }

        string preset = usage switch
        {
            ProfileGroupUsage.Office => "Silencieux",
            ProfileGroupUsage.LightGaming => "Équilibré",
            _ => "Perf",
        };

        UsageStats stats = input.Stats(usage);
        var profile = new FanProfile();
        var leftToBios = new List<string>();
        var shifts = new List<string>();
        foreach (FanTargetFan fan in state.Fans)
        {
            FanCurveConfig? current = state.Current.Fans?.FirstOrDefault(c => c is not null && c.ControlSensorId == fan.FanId);
            bool userControlled = current is { Mode: FanControlMode.Curve or FanControlMode.Manual };
            if (!userControlled && !input.IncludeBiosFans)
            {
                leftToBios.Add(fan.Name);
                continue;
            }

            FanCurveConfig config = current?.Clone() ?? new FanCurveConfig { ControlSensorId = fan.FanId };
            if (!userControlled) config.Source = fan.IsGpuCooler ? FanTempSource.GpuCore : FanTempSource.HottestOfCpuGpu;

            int? p95 = ReferenceTemp(config.Source, stats);
            List<FanCurvePoint> points = PresetPoints(usage);
            float shift = FanCurveShaper.Shift(points, p95, HasEnoughHistory(stats));
            config.Mode = FanControlMode.Curve;
            config.Points = FanCurveShaper.Shape(points, shift);
            profile.Fans.Add(config);
            profile.FanNames[fan.FanId] = fan.Name;
            if (shift != 0 && p95 is { } temp)
            {
                shifts.Add($"{fan.Name} {Signed(shift)} °C (p95 {temp} °C)");
            }
        }

        if (profile.Fans.Count == 0)
        {
            explanation.Add($"Ventilation : non touchée, tous les ventilateurs sont au BIOS ({string.Join(", ", leftToBios)}). "
                            + "Passe-les en courbe dans l'onglet Ventilateurs, ou coche « prendre aussi en main les ventilateurs au BIOS », puis régénère.");
            return null;
        }

        string history = HasEnoughHistory(stats)
            ? shifts.Count == 0 ? " sans décalage (températures relevées dans la norme)" : $", décalée d'après les températures relevées : {string.Join(", ", shifts)}"
            : " telle quelle (pas encore assez d'historique pour la décaler)";
        string line = $"Ventilation : courbe {preset}{history}.";
        if (leftToBios.Count > 0) line += $" Laissés au BIOS : {string.Join(", ", leftToBios)}.";
        if (state.MotherboardFansRefused) line += " " + FanGroupPlanner.LaptopNote;
        explanation.Add(line);
        return ProfileGroupEditor.FanValues(profile);
    }

    private static List<FanCurvePoint> PresetPoints(string usage) => usage switch
    {
        ProfileGroupUsage.Office => FanCurveMath.SilencieuxPoints(),
        ProfileGroupUsage.LightGaming => FanCurveMath.EquilibrePoints(),
        _ => FanCurveMath.PerfPoints(),
    };

    private static int? ReferenceTemp(FanTempSource source, UsageStats stats) => source switch
    {
        FanTempSource.CpuPackage => stats.CpuTempSeconds >= MinTemperatureHistory.TotalSeconds ? stats.CpuTempP95 : null,
        FanTempSource.GpuCore => stats.GpuTempSeconds >= MinTemperatureHistory.TotalSeconds ? stats.GpuTempP95 : null,
        FanTempSource.HottestOfCpuGpu => new[]
            {
                stats.CpuTempSeconds >= MinTemperatureHistory.TotalSeconds ? stats.CpuTempP95 : null,
                stats.GpuTempSeconds >= MinTemperatureHistory.TotalSeconds ? stats.GpuTempP95 : null,
            }.Max(),
        _ => null,
    };

    private static bool HasEnoughHistory(UsageStats stats) => stats.Seconds >= MinHistoryForShift.TotalSeconds;

    // ---- Processeur ----

    private static ProfileGroupCpuPart? BuildCpu(string usage, UsageGenerationInput input, List<string> explanation)
    {
        CpuTargetState state = input.Cpu;
        var values = new CpuProfile();
        var parts = new List<string>();

        (uint ac, uint dc) = Epp(usage);
        foreach (string id in EppSettingIds)
        {
            if (!state.Settings.Any(s => s.Setting.Id == id)) continue;
            values.PowerSettings[id] = new CpuProfilePowerValue { Ac = ac, Battery = state.HasBattery ? dc : null };
        }

        if (values.PowerSettings.Count > 0)
        {
            parts.Add(state.HasBattery
                ? $"préférence performance/économie {ac} sur secteur, {dc} sur batterie (0 = performance, 100 = économie)"
                : $"préférence performance/économie {ac} (0 = performance, 100 = économie)");
        }

        string? wattsNote = null;
        if (WattsReason(state) is { } refusal)
        {
            wattsNote = $"watts non touchés : {refusal}";
        }
        else
        {
            CpuWattsReading watts = state.Watts!;
            (float? savedSustained, float? savedBurst) = SavedWatts(input.Saved, watts);
            if (usage == ProfileGroupUsage.Office)
            {
                float sustained = Clamp(MathF.Round(watts.DefaultSustained * OfficeWattsShare), watts);
                if (savedSustained is { } cap) sustained = MathF.Min(sustained, cap);
                float? burst = watts.DefaultBurst is { } defaultBurst && defaultBurst < CpuMaxWattsResolver.UnlimitedWatts
                    ? MathF.Max(sustained, Clamp(MathF.Round(defaultBurst * OfficeWattsShare), watts))
                    : null;
                if (burst is { } b && savedBurst is { } capBurst) burst = MathF.Max(sustained, MathF.Min(b, capBurst));
                values.SustainedWatts = sustained;
                values.BurstWatts = watts.HasBurst ? burst : null;
                parts.Add($"limite de {W(sustained)} ({Percent(OfficeWattsShare)} des {W(watts.DefaultSustained)} d'origine"
                          + (savedSustained is not null ? ", sans dépasser les watts enregistrés dans l'onglet" : "") + ")");
            }
            else if (savedSustained is { } sustained)
            {
                values.SustainedWatts = sustained;
                values.BurstWatts = watts.HasBurst ? savedBurst : null;
                parts.Add($"limite de {W(sustained)}, celle enregistrée dans l'onglet Processeur");
            }
            else
            {
                values.SustainedWatts = Clamp(watts.DefaultSustained, watts);
                values.BurstWatts = watts.HasBurst && watts.DefaultBurst is { } burst && burst < CpuMaxWattsResolver.UnlimitedWatts
                    ? Clamp(burst, watts)
                    : null;
                parts.Add($"limites d'origine ({W(watts.DefaultSustained)})");
            }
        }

        if (values.PowerSettings.Count == 0 && values.SustainedWatts is null)
        {
            explanation.Add($"Processeur : non touché ({wattsNote ?? "aucun réglage pris en charge ici"}).");
            return null;
        }

        string line = $"Processeur : {string.Join(" ; ", parts)}.";
        if (wattsNote is not null) line += $" ({Upper(wattsNote)}.)";
        if (values.PowerSettings.Count > 0) line += " Réglages du plan d'alimentation : permanents, ils restent après la fermeture.";
        explanation.Add(line);
        return ProfileGroupEditor.CpuValues(values, state.Identity);
    }

    /// <summary>Pourquoi les watts ne sont pas touchés, null s'ils peuvent l'être.</summary>
    private static string? WattsReason(CpuTargetState state)
    {
        if (state.Watts is not { } watts) return "limites illisibles sur ce PC";
        if (!state.WattsWritable) return Lower(state.WattsUnavailableReason ?? "ce PC ne permet pas de les écrire");
        if (!state.RiskAccepted) return "l'avertissement de l'onglet Processeur n'a pas été accepté";
        if (watts.DefaultSustained <= 0 || watts.DefaultSustained >= CpuMaxWattsResolver.UnlimitedWatts)
            return "origine « sans limite » réglée par le BIOS, pas de base sûre pour une limite";
        return null;
    }

    /// <summary>Les watts enregistrés dans l'onglet, s'ils sont reposés au démarrage et dans les bornes de ce
    /// processeur ; sinon rien.</summary>
    private static (float? Sustained, float? Burst) SavedWatts(SavedTabTuning saved, CpuWattsReading watts)
    {
        if (!saved.CpuApplyAtStartup || saved.CpuSustainedWatts is not { } sustained) return (null, null);
        if (!float.IsFinite(sustained) || sustained < watts.Min || sustained > watts.Max || sustained >= CpuMaxWattsResolver.UnlimitedWatts)
            return (null, null);

        float? burst = saved.CpuBurstWatts is { } b && float.IsFinite(b) && b >= sustained && b <= watts.Max && b < CpuMaxWattsResolver.UnlimitedWatts
            ? b
            : null;
        return (sustained, burst);
    }

    private static float Clamp(float value, CpuWattsReading watts) => Math.Clamp(value, watts.Min, Math.Max(watts.Min, watts.Max));

    // ---- Carte graphique ----

    private static ProfileGroupGpuPart? BuildGpu(string usage, UsageGenerationInput input, List<string> explanation)
    {
        if (usage == ProfileGroupUsage.Office)
        {
            explanation.Add(input.Gpu.IsAvailable
                ? "Carte graphique : remise d'origine (un overclock posé à la main ou par un groupe de jeu est retiré)."
                : $"Carte graphique : remise d'origine dès qu'elle sera pilotable ({Lower(input.Gpu.UnavailableReason ?? "non pilotable ici")}).");
            return ProfileGroupEditor.GpuOrigin();
        }

        if (input.Overclock.For(usage, input.Gpu) is { Gpu: { } slot } overclock)
        {
            explanation.Add($"Carte graphique : {overclock.Explanation ?? "profil de l'OC automatique"}.");
            return ProfileGroupJson.Clone(slot);
        }

        const string slotNote = "l'emplacement de l'OC automatique (à venir) est vide";
        if (!input.Gpu.IsAvailable)
        {
            explanation.Add($"Carte graphique : non touchée ({Lower(input.Gpu.UnavailableReason ?? "non pilotable ici")} ; {slotNote}).");
            return null;
        }

        SavedTabTuning saved = input.Saved;
        if (saved.GpuApplyAtStartup && saved.GpuOverclock is { } oc && GpuOverclockRaise.IsRaisedProfile(oc))
        {
            if (GpuIdentity.IsSameCard(saved.GpuIdentity, input.Gpu.Identity))
            {
                explanation.Add($"Carte graphique : ton overclock de l'onglet GPU ({DescribeOverclock(oc)}), repris tel quel ; {slotNote}.");
                return ProfileGroupEditor.GpuValues(oc, input.Gpu.Identity);
            }

            explanation.Add($"Carte graphique : non touchée ; l'overclock de l'onglet GPU a été réglé sur une autre carte, il n'est pas repris ; {slotNote}.");
            return null;
        }

        explanation.Add($"Carte graphique : non touchée (aucun overclock enregistré avec « Appliquer au démarrage » dans l'onglet GPU ; {slotNote}). "
                        + "« Modifier » ce groupe permet d'y mettre un profil de l'onglet GPU.");
        return null;
    }

    private static string DescribeOverclock(GpuOverclockProfile profile)
    {
        var parts = new List<string>();
        if (profile.CoreClockOffsetMhz != 0) parts.Add($"cœur {Signed(profile.CoreClockOffsetMhz)} MHz");
        if (profile.MemoryClockOffsetMhz != 0) parts.Add($"mémoire {Signed(profile.MemoryClockOffsetMhz)} MHz");
        if (profile.PowerLimitPercent is { } power) parts.Add($"puissance {power.ToString("0", CultureInfo.InvariantCulture)} %");
        if (profile.TemperatureLimitC is { } temp) parts.Add($"température {temp} °C");
        if (profile.GetVoltage() is { } voltage) parts.Add($"tension {voltage.Value}{(voltage.Unit == GpuVoltageUnit.Percent ? " %" : " mV")}");
        return string.Join(", ", parts);
    }

    // ---- Texte ----

    private static string W(float watts) => $"{watts.ToString("0", CultureInfo.InvariantCulture)} W";

    private static string Percent(float share) => $"{(share * 100).ToString("0", CultureInfo.InvariantCulture)} %";

    private static string Signed(float value) => value > 0
        ? $"+{value.ToString("0", CultureInfo.InvariantCulture)}"
        : value.ToString("0", CultureInfo.InvariantCulture);

    private static string Lower(string text)
    {
        string trimmed = text.Trim().TrimEnd('.');
        return trimmed.Length == 0 ? trimmed : char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
    }

    private static string Upper(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Courbes générées, en logique pure : décalage d'après les températures relevées, bornes de l'éditeur, et
/// 100 % à chaud.</summary>
public static class FanCurveShaper
{
    /// <summary>Décalage maximal vers plus de refroidissement (courbe avancée), et vers moins (courbe reculée).</summary>
    public const float MaxCoolerShift = 5;
    public const float MaxQuieterShift = 3;

    /// <summary>Au-delà de cette température relevée (p95), la courbe n'est jamais reculée.</summary>
    public const float HotP95 = 70;

    /// <summary>Température à laquelle une courbe à 100 % au plus tard.</summary>
    public const float FullSpeedBy = 85;

    /// <summary>
    /// Décalage de la courbe, en °C (négatif : elle monte plus tôt). Repère : la température où le préréglage atteint
    /// 75 %. Un p95 relevé à moins de 5 °C de ce repère avance la courbe (jusqu'à 5 °C) ; un p95 à plus de 15 °C en dessous
    /// la recule (au plus 3 °C), jamais au-dessus de <see cref="HotP95"/>. Sans assez d'historique : aucun décalage.
    /// </summary>
    public static float Shift(IReadOnlyList<FanCurvePoint> preset, int? p95, bool enoughHistory)
    {
        if (!enoughHistory || p95 is not { } temp) return 0;

        float reference = TempAt(preset, 75);
        float margin = reference - temp;
        if (margin < 5) return -MathF.Min(MaxCoolerShift, MathF.Round(5 - margin));
        if (margin > 15 && temp < HotP95) return MathF.Min(MaxQuieterShift, MathF.Round((margin - 15) / 2));
        return 0;
    }

    /// <summary>Les points décalés, ramenés dans [<see cref="FanCurveMath.MinTempC"/>, <see cref="FanCurveMath.MaxTempC"/>]
    /// avec l'écart minimal entre deux points, croissants, et à 100 % au plus tard à <see cref="FullSpeedBy"/> °C.</summary>
    public static List<FanCurvePoint> Shape(IReadOnlyList<FanCurvePoint> preset, float shift)
    {
        var points = preset
            .OrderBy(p => p.TempC)
            .Select(p => new FanCurvePoint { TempC = p.TempC + shift, Percent = Math.Clamp(p.Percent, 0, 100) })
            .ToList();

        if (points.Count == 0 || points[^1].Percent < 100)
        {
            float last = points.Count == 0 ? FanCurveMath.MinTempC : points[^1].TempC;
            points.Add(new FanCurvePoint { TempC = MathF.Min(FullSpeedBy, last + 7), Percent = 100 });
        }

        // Bornes et écart minimal, en partant de la fin pour garder le 100 % sous FullSpeedBy.
        float max = MathF.Min(FanCurveMath.MaxTempC, FullSpeedBy);
        for (int i = points.Count - 1; i >= 0; i--)
        {
            float ceiling = i == points.Count - 1 ? max : points[i + 1].TempC - FanCurveMath.MinTempGap;
            points[i].TempC = MathF.Min(points[i].TempC, ceiling);
        }

        for (int i = 0; i < points.Count; i++)
        {
            float floor = i == 0 ? FanCurveMath.MinTempC : points[i - 1].TempC + FanCurveMath.MinTempGap;
            points[i].TempC = MathF.Max(points[i].TempC, floor);
        }

        // Une courbe ne redescend jamais en montant en température.
        for (int i = 1; i < points.Count; i++)
        {
            points[i].Percent = MathF.Max(points[i].Percent, points[i - 1].Percent);
        }

        return points;
    }

    /// <summary>La température où la courbe atteint <paramref name="percent"/> (interpolée), ou son dernier point.</summary>
    public static float TempAt(IReadOnlyList<FanCurvePoint> points, float percent)
    {
        List<FanCurvePoint> sorted = points.OrderBy(p => p.TempC).ToList();
        if (sorted.Count == 0) return FanCurveMath.MaxTempC;
        if (sorted[0].Percent >= percent) return sorted[0].TempC;

        for (int i = 1; i < sorted.Count; i++)
        {
            FanCurvePoint a = sorted[i - 1], b = sorted[i];
            if (b.Percent < percent) continue;
            float span = b.Percent - a.Percent;
            return span <= 0 ? b.TempC : a.TempC + (percent - a.Percent) / span * (b.TempC - a.TempC);
        }

        return sorted[^1].TempC;
    }
}
