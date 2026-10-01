namespace PCPerfSuite.Core.Installations;

/// <summary>Réglages de la Boîte à outils, dans settings.json.</summary>
public sealed class ToolboxSettings
{
    /// <summary>Outils dont la Boîte à outils a lancé l'installeur avec succès : le registre des modifications
    /// (<see cref="ToolboxChanges"/>) les liste tant qu'ils restent installés, en disant qu'il ne les retire pas lui-même.</summary>
    public List<ToolInstallRecord> InstalledByApp { get; set; } = new();
}

/// <summary>Un outil installé depuis la Boîte à outils.</summary>
public sealed class ToolInstallRecord
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public DateTime InstalledUtc { get; set; }
}
