namespace PCPerfSuite.Core.Profiles;

/// <summary>Une dimension d'un groupe. En mémoire seulement : jamais enregistrée.</summary>
public enum ProfileDimension
{
    Cpu,
    Gpu,
    Fans,
}

/// <summary>Ce qu'il est advenu d'un réglage.</summary>
public enum ReportItemStatus
{
    /// <summary>Posé, et relu tel quel (ou déjà en place).</summary>
    Applied,

    /// <summary>Posé, mais relu différent : le pilote ou Windows a retenu autre chose, ou la valeur a été bornée.</summary>
    Trimmed,

    /// <summary>Le pilote ou Windows a refusé l'écriture, ou une garde de l'app l'a empêchée.</summary>
    Refused,

    /// <summary>Laissé de côté, sans tentative d'écriture : absent de ce PC, accord manquant, autre matériel…</summary>
    Ignored,

    /// <summary>Envoyé, mais impossible à relire.</summary>
    NotReadBack,
}

/// <summary>Un réglage du rapport : son nom, ce qui lui est arrivé, et la phrase qui le dit (« cœur +150 MHz »,
/// « watts ignorés : avertissement non accepté »).</summary>
public sealed record ReportItem(string Label, ReportItemStatus Status, string Text)
{
    public bool Landed => Status is ReportItemStatus.Applied or ReportItemStatus.Trimmed or ReportItemStatus.NotReadBack;

    public static ReportItem Applied(string label, string text) => new(label, ReportItemStatus.Applied, text);

    public static ReportItem Ignored(string label, string text) => new(label, ReportItemStatus.Ignored, text);

    public static ReportItem Refused(string label, string text) => new(label, ReportItemStatus.Refused, text);
}

public enum DimensionOutcome
{
    /// <summary>Tout ce qui était demandé est en place.</summary>
    Applied,

    /// <summary>Une partie seulement.</summary>
    Partial,

    /// <summary>Rien n'a été posé.</summary>
    NotApplied,
}

/// <summary>
/// Ce qu'une dimension a fait du groupe. <paramref name="Notes"/> : ce qu'il faut savoir sans que ce soit un réglage
/// (« portable : seule la ventilation de la carte graphique se règle », « permanent, reste après la fermeture »).
/// <paramref name="IsPermanent"/> : au moins un réglage du plan d'alimentation a été écrit (D7).
/// </summary>
public sealed record DimensionReport(
    ProfileDimension Dimension,
    IReadOnlyList<ReportItem> Items,
    IReadOnlyList<string> Notes,
    bool IsPermanent = false)
{
    public DimensionOutcome Outcome
    {
        get
        {
            int landed = Items.Count(i => i.Landed);
            if (landed == 0) return DimensionOutcome.NotApplied;
            return Items.All(i => i.Status == ReportItemStatus.Applied) ? DimensionOutcome.Applied : DimensionOutcome.Partial;
        }
    }

    /// <summary>La dimension n'a rien posé, pour cette raison (autre carte, aucun ventilateur relevé…).</summary>
    public static DimensionReport Skipped(ProfileDimension dimension, string reason, params string[] notes)
        => new(dimension, [ReportItem.Ignored(Title(dimension), reason)], notes);

    public static string Title(ProfileDimension dimension) => dimension switch
    {
        ProfileDimension.Cpu => "Processeur",
        ProfileDimension.Gpu => "Carte graphique",
        _ => "Ventilation",
    };

    /// <summary>« Processeur : 3 réglages du plan appliqués, watts ignorés : avertissement non accepté. »</summary>
    public string Describe() => $"{Title(Dimension)} : {Body}.";

    /// <summary>Le rapport sans le nom de la dimension, pour le statut d'un onglet.</summary>
    public string Body
    {
        get
        {
            var parts = Items.Select(i => i.Text).Where(t => t.Length > 0).ToList();
            parts.AddRange(Notes.Where(n => n.Length > 0));
            return parts.Count == 0 ? "rien à changer" : string.Join(" ; ", parts);
        }
    }
}

/// <summary>Dans quel ordre les dimensions ont été appliquées (décision de Denis, 01/10/2026).</summary>
public enum ApplyOrder
{
    /// <summary>Montée en performance, indéterminée ou mixte : les ventilateurs d'abord, puis CPU et GPU.</summary>
    FansFirst,

    /// <summary>Descente : CPU et GPU d'abord, puis les ventilateurs.</summary>
    FansLast,
}

/// <summary>Le rapport consolidé d'une application de groupe, dimension par dimension, dans l'ordre d'application.
/// <paramref name="Refusal"/> : le groupe n'a pas été appliqué du tout (bail tenu par un autre…).</summary>
public sealed record ProfileGroupReport(
    string GroupId,
    string GroupName,
    DateTimeOffset TimeUtc,
    ApplyOrder? Order,
    IReadOnlyList<DimensionReport> Dimensions,
    string? Refusal,
    IReadOnlyList<string> Notes)
{
    public bool WasRefused => Refusal is not null;

    public static ProfileGroupReport Refused(ProfileGroup group, DateTimeOffset now, string refusal)
        => new(group.Id, group.Name, now, null, [], refusal, []);

    public DimensionReport? Find(ProfileDimension dimension) => Dimensions.FirstOrDefault(d => d.Dimension == dimension);

    /// <summary>Tout ce qui était demandé est en place.</summary>
    public bool IsComplete => !WasRefused && Dimensions.All(d => d.Outcome == DimensionOutcome.Applied);

    public bool AnyLanded => Dimensions.Any(d => d.Outcome != DimensionOutcome.NotApplied);

    /// <summary>« Groupe « Jeu » appliqué en partie. »</summary>
    public string Title
    {
        get
        {
            if (WasRefused) return $"Groupe « {GroupName} » non appliqué : {Refusal}";
            if (Dimensions.Count == 0) return $"Groupe « {GroupName} » : rien à appliquer.";
            if (IsComplete) return $"Groupe « {GroupName} » appliqué.";
            return AnyLanded ? $"Groupe « {GroupName} » appliqué en partie." : $"Groupe « {GroupName} » non appliqué.";
        }
    }

    /// <summary>Le rapport en une phrase par dimension, puis les remarques : ce qu'affichent la page et le diagnostic.</summary>
    public string Describe()
    {
        var lines = new List<string> { Title };
        if (Order is { } order && Dimensions.Count > 1)
        {
            lines.Add(order == ApplyOrder.FansFirst
                ? "Ordre : ventilation d'abord (montée en performance ou indéterminée), puis processeur et carte graphique."
                : "Ordre : processeur et carte graphique d'abord (descente), puis ventilation.");
        }

        lines.AddRange(Dimensions.Select(d => d.Describe()));
        lines.AddRange(Notes);
        return string.Join(Environment.NewLine, lines);
    }
}
