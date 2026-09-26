using System.Diagnostics;

namespace PCPerfSuite.Core.Installations;

/// <summary>
/// Ouvre une page web dans le navigateur par défaut. Seul le HTTPS est accepté : les adresses des sources
/// officielles sont écrites en dur dans le code, mais une adresse qui viendrait à ne plus l'être ne doit
/// jamais partir vers le shell Windows, qui lancerait n'importe quoi (un fichier, un protocole personnalisé).
/// </summary>
public static class ExternalLink
{
    /// <summary>Ne lève jamais : renvoie false avec une raison lisible, qui contient l'adresse pour que
    /// l'utilisateur puisse l'ouvrir lui-même.</summary>
    public static bool TryOpen(string url, out string? error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Adresse refusée, seul le HTTPS est autorisé : {url}";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Impossible d'ouvrir le navigateur. L'adresse est : {url} ({ex.Message})";
            return false;
        }
    }
}
