using System.Security.Principal;

namespace PCPerfSuite.Core.SystemInfo;

public static class ElevationHelper
{
    /// <summary>Lecture best-effort : renvoie false plutôt que de lever. Plusieurs ViewModels l'appellent
    /// depuis un initialiseur de champ, où une exception ne ferait pas échouer une fonction mais la
    /// construction entière de l'onglet — et donc son ouverture.</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Jeton indisponible : contexte d'emprunt d'identité, stratégie de sécurité d'entreprise, ou
            // app lancée hors de Windows. On suppose alors le moins de droits possible — l'app annoncera
            // les capteurs bas niveau comme inaccessibles, ce qui est le comportement sûr.
            return false;
        }
    }
}
