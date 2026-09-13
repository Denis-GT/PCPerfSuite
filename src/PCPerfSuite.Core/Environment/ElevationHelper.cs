using System.Security.Principal;

namespace PCPerfSuite.Core.SystemInfo;

public static class ElevationHelper
{
    public static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
