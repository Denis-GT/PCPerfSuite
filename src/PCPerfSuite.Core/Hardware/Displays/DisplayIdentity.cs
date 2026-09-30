namespace PCPerfSuite.Core.Hardware.Displays;

/// <summary>
/// Identité enregistrée d'un écran, pour le retrouver au lancement suivant ou après un débranchement. Jamais le nom GDI
/// « \\.\DISPLAYn », dont le numéro change d'un démarrage à l'autre. Chaque champ est facultatif : un écran sans EDID
/// n'a que son chemin.
///
/// <see cref="SerialHash"/> est une empreinte du numéro de série EDID (voir <see cref="MonitorSerials.HashSerial"/>),
/// jamais le numéro lui-même : settings.json est parfois joint à un rapport de bug.
/// </summary>
public sealed class DisplayIdentity
{
    public string? DevicePath { get; set; }
    public ushort? EdidManufacturerId { get; set; }
    public ushort? EdidProductCodeId { get; set; }

    /// <summary>Nom convivial au moment du choix : seulement pour dire quel écran manque.</summary>
    public string? FriendlyName { get; set; }

    public string? SerialHash { get; set; }

    /// <summary>Identité du premier écran physique de <paramref name="monitor"/> (celui d'écrans dupliqués qui vient
    /// en premier), avec l'empreinte de son numéro de série si elle a été lue.</summary>
    public static DisplayIdentity FromMonitor(DisplayMonitor monitor, IReadOnlyDictionary<string, string> serialHashes, int number)
    {
        DisplayTarget? target = monitor.Targets.FirstOrDefault();
        return new DisplayIdentity
        {
            DevicePath = target?.DevicePath,
            EdidManufacturerId = target?.EdidManufacturerId,
            EdidProductCodeId = target?.EdidProductCodeId,
            FriendlyName = DisplayNames.Label(monitor, number),
            SerialHash = target?.DevicePath is { } path ? serialHashes.GetValueOrDefault(path) : null,
        };
    }
}

/// <summary>Comment l'écran enregistré a été retrouvé.</summary>
public enum DisplayMatch
{
    /// <summary>Même chemin de périphérique : même écran, sur le même connecteur.</summary>
    ExactPath,

    /// <summary>Un seul écran branché a le même fabricant et le même produit EDID.</summary>
    Edid,

    /// <summary>Plusieurs écrans identiques, départagés par le numéro de série.</summary>
    EdidAndSerial,

    /// <summary>Plusieurs écrans identiques, que rien ne départage.</summary>
    Ambiguous,

    /// <summary>Aucun écran branché ne correspond.</summary>
    NotFound,
}

public sealed record DisplayResolution(DisplayMonitor? Monitor, DisplayMatch Match);

/// <summary>Retrouve un écran enregistré parmi ceux branchés, isolé ici pour être testé sans écran.</summary>
public static class DisplayIdentityResolver
{
    /// <summary>
    /// Dans l'ordre : chemin exact ; sinon mêmes identifiants EDID s'il n'y a qu'un candidat ; sinon, entre écrans
    /// identiques, celui dont le numéro de série a la même empreinte. Toujours ambigu, ou rien : pas d'écran, et c'est
    /// l'appelant qui se replie.
    /// </summary>
    /// <param name="serialHashes">Empreinte du numéro de série par chemin de périphérique, lue par
    /// <see cref="MonitorSerials"/> ; vide tant qu'elle n'est pas lue.</param>
    public static DisplayResolution Resolve(DisplayIdentity saved, DisplayTopologySnapshot snapshot, IReadOnlyDictionary<string, string> serialHashes)
    {
        if (!string.IsNullOrEmpty(saved.DevicePath))
        {
            DisplayMonitor? exact = snapshot.Monitors.FirstOrDefault(m => m.Targets.Any(t =>
                string.Equals(t.DevicePath, saved.DevicePath, StringComparison.OrdinalIgnoreCase)));
            if (exact is not null) return new DisplayResolution(exact, DisplayMatch.ExactPath);
        }

        if (saved.EdidManufacturerId is not { } manufacturer || saved.EdidProductCodeId is not { } product)
            return new DisplayResolution(null, DisplayMatch.NotFound);

        List<(DisplayMonitor Monitor, DisplayTarget Target)> candidates = snapshot.Monitors
            .SelectMany(m => m.Targets.Select(t => (Monitor: m, Target: t)))
            .Where(c => c.Target.EdidManufacturerId == manufacturer && c.Target.EdidProductCodeId == product)
            .ToList();

        List<DisplayMonitor> monitors = candidates.Select(c => c.Monitor).Distinct().ToList();
        if (monitors.Count == 0) return new DisplayResolution(null, DisplayMatch.NotFound);
        if (monitors.Count == 1) return new DisplayResolution(monitors[0], DisplayMatch.Edid);

        if (!string.IsNullOrEmpty(saved.SerialHash))
        {
            List<DisplayMonitor> bySerial = candidates
                .Where(c => c.Target.DevicePath is { } path
                            && serialHashes.TryGetValue(path, out string? hash)
                            && string.Equals(hash, saved.SerialHash, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Monitor)
                .Distinct()
                .ToList();
            if (bySerial.Count == 1) return new DisplayResolution(bySerial[0], DisplayMatch.EdidAndSerial);
        }

        return new DisplayResolution(null, DisplayMatch.Ambiguous);
    }
}
