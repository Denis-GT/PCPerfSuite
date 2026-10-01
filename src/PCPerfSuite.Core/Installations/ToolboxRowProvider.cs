using System.Globalization;
using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Installations;

/// <summary>
/// Diagnostic « Boîte à outils » : d'où vient le catalogue en usage (intégré, en cache, en ligne) et pourquoi le
/// catalogue en ligne a pu être refusé, l'état du dossier sécurisé %ProgramData%\PCPerfSuite, la présence de winget et
/// les outils portables déposés. Rien n'est lu sur le réseau ici : le catalogue en ligne se lit à l'ouverture de la page.
/// </summary>
public sealed class ToolboxRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Boîte à outils";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Lectures disque de <see cref="RefreshAsync"/>, publiées ensemble.</summary>
    private sealed record Reading(SecureFolderResult Folder, bool HasWinget, IReadOnlyList<string> Portables);

    private readonly ToolCatalogStore _catalog;
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private Reading? _reading;

    public ToolboxRowProvider(ToolCatalogStore catalog, IReadOnlyList<ToolDefinition>? tools = null)
    {
        _catalog = catalog;
        _tools = tools ?? ToolCatalog.All;
    }

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        SecureFolderResult folder = ProgramDataFolder.Inspect();
        var portables = new List<string>();
        List<ToolDefinition> portableTools = _tools.Where(t => t.IsPortable).ToList();
        IReadOnlyList<ToolInstallState> states = ToolDetection.DetectAll(portableTools);
        for (int i = 0; i < portableTools.Count; i++)
        {
            if (states[i].IsPresent) portables.Add($"{portableTools[i].Name} {states[i].Version}".TrimEnd());
        }

        Volatile.Write(ref _reading, new Reading(folder, WingetFallback.FindExecutable() is not null, portables));
        return Task.CompletedTask;
    }

    public IReadOnlyList<CompatibilityRow> GetRows()
    {
        Reading? reading = Volatile.Read(ref _reading);
        return new[] { BuildRow(_catalog.Status, _catalog.IsOnlineEnabled, _tools, reading?.Folder, reading?.HasWinget, reading?.Portables) };
    }

    /// <summary>La ligne, isolée ici pour être testée sans disque ni réseau. Les lectures nulles ne sont pas encore faites.</summary>
    public static CompatibilityRow BuildRow(ToolCatalogStatus status, bool onlineEnabled, IReadOnlyList<ToolDefinition> tools,
        SecureFolderResult? folder, bool? hasWinget, IReadOnlyList<string>? portables)
    {
        ToolCatalogDocument document = status.Document;
        string date = document.GeneratedUtc is { } generated ? $" du {generated.ToLocalTime().ToString("d MMMM yyyy", French)}" : "";
        string origin = status.Origin switch
        {
            ToolCatalogOrigin.Online => "en ligne",
            ToolCatalogOrigin.Cache => "en ligne, gardé sur le disque",
            _ => "intégré à l'app",
        };
        string statusText = $"Catalogue n° {document.Sequence}{date} ({origin})";

        int direct = tools.Count(t => t.HasDirectDownload && document.Releases.ContainsKey(t.Id));
        int pageOnly = tools.Count(t => t.Delivery == ToolDelivery.OfficialPageOnly);
        var details = new List<string>
        {
            $"{tools.Count} outils : {direct} en téléchargement direct vérifié, {pageOnly} sur leur page officielle seulement.",
        };

        if (!onlineEnabled) details.Add("Catalogue en ligne pas encore activé (clé de signature à inscrire dans l'app) : la liste intégrée sert.");
        else if (status.OnlineMessage is { } online) details.Add($"Catalogue en ligne : {online}.");
        else if (status.Origin == ToolCatalogOrigin.Embedded) details.Add("Le catalogue en ligne est lu à l'ouverture de la Boîte à outils.");

        if (document.Rejected.Count > 0) details.Add($"Entrées écartées : {string.Join(" ; ", document.Rejected)}.");

        details.Add(folder switch
        {
            null => "Dossier sécurisé : pas encore lu.",
            { IsReady: true } => $"Dossier sécurisé {folder.Path} : prêt (administrateurs seulement en écriture).",
            _ => $"Dossier sécurisé %ProgramData%\\PCPerfSuite : {folder.Error}.",
        });

        if (hasWinget is { } winget)
        {
            details.Add(winget ? "winget : présent (repli si un lien direct ne répond plus)." : "winget : absent sur ce compte (pas de repli winget).");
        }

        if (portables is { Count: > 0 }) details.Add($"Outils portables déposés : {string.Join(", ", portables)}.");
        List<string> unverified = tools.Where(t => t.Delivery != ToolDelivery.OfficialPageOnly && !t.IsVerified).Select(t => t.Name).ToList();
        if (unverified.Count > 0)
        {
            details.Add($"Expérimental (pas encore vérifié sur une vraie machine) : {string.Join(", ", unverified)}.");
        }

        bool folderRefused = folder is { IsReady: false, IsAbsent: false };
        return new CompatibilityRow(RowTitle, statusText, string.Join(" ", details), !folderRefused && !status.OnlineRefusalIsSuspicious);
    }
}
