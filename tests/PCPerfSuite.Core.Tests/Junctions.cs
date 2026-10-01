using System.Diagnostics;

namespace PCPerfSuite.Core.Tests;

/// <summary>Jonctions de répertoire pour les tests : mklink /J ne demande aucun droit particulier.</summary>
internal static class Junctions
{
    public static bool TryCreate(string link, string target)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
