namespace PCPerfSuite.Core.Compatibility;

/// <summary>Une ligne du diagnostic « Compatibilité de ce PC » : une fonction ou une source de données, et ce qu'elle
/// donne sur ce PC. <paramref name="IsSupported"/> à false marque un problème (badge orange, « [--] » dans le rapport),
/// pas seulement une absence. <paramref name="IsPersonal"/> marque une donnée personnelle (nom de compte, nom du PC) :
/// affichée à l'écran comme les autres, mais son <paramref name="Status"/> et son <paramref name="Detail"/> sont
/// masqués dans le rapport copié — un rapport de bug est souvent collé tel quel dans un espace public (forum, ticket
/// GitHub).</summary>
public sealed record CompatibilityRow(string Title, string Status, string Detail, bool IsSupported, bool IsPersonal = false);
