using System.Security.AccessControl;
using System.Security.Principal;

namespace PCPerfSuite.Core.Installations;

/// <summary>Un dossier sécurisé prêt à servir (<paramref name="Path"/>), ou pourquoi il est refusé (<paramref name="Error"/>).</summary>
public sealed record SecureFolderResult(string? Path, string? Error)
{
    public bool IsReady => Path is not null;

    /// <summary>Le dossier n'existe pas encore : ce n'est pas un refus, il sera créé au premier besoin.</summary>
    public bool IsAbsent { get; init; }
}

/// <summary>
/// Dossier %ProgramData%\PCPerfSuite, partagé par toutes les sessions : outils portables de la Boîte à outils
/// (<c>Tools\&lt;id&gt;\&lt;version&gt;</c>), et demain fichiers de bench ou outils du mode portable.
///
/// Ce qui s'y trouve est lancé, ou lu, par l'app élevée : seuls les administrateurs et SYSTEM doivent pouvoir y
/// écrire. Or %ProgramData% est inscriptible par tous les utilisateurs (n'importe qui peut y créer un dossier). Donc :
/// - le dossier est créé avec une liste d'accès protégée (SYSTEM et Administrateurs : contrôle total ; Utilisateurs :
///   lecture et exécution, pour qu'un outil lancé sans élévation lise ses propres fichiers), et un niveau d'intégrité
///   « Élevé » ;
/// - un dossier déjà là n'est accepté que s'il appartient aux Administrateurs, à SYSTEM ou à TrustedInstaller et que
///   personne d'autre n'y a de droit d'écriture : un dossier préparé par un compte standard (pour y glisser une DLL)
///   est refusé, jamais « réparé » en douce ;
/// - aucun élément du chemin ne peut être une jonction ni un lien : il mènerait ailleurs.
///
/// Best-effort (règle 2) : ne lève jamais, renvoie la raison du refus prête à afficher.
/// </summary>
public static class ProgramDataFolder
{
    public const string FolderName = "PCPerfSuite";

    /// <summary>Sous-dossier des outils portables de la Boîte à outils.</summary>
    public const string ToolsFolderName = "Tools";

    /// <summary>Sous-dossier des dossiers de travail d'OfficialInstaller (installeurs téléchargés avant leur lancement,
    /// fichiers vérifiés avant d'être remis à l'utilisateur).</summary>
    public const string WorkFolderName = "Installations";

    /// <summary>Plus haut numéro de catalogue d'outils accepté (plancher contre un retour en arrière), ici parce que
    /// seuls les administrateurs peuvent le modifier.</summary>
    public const string CatalogFloorFileName = "catalogue-outils.plancher";

    /// <summary>SYSTEM et Administrateurs : contrôle total ; Utilisateurs : lecture et exécution (0x1200a9). Protégée :
    /// rien n'est hérité de %ProgramData%, qui laisse les utilisateurs créer des fichiers.</summary>
    internal const string SecureSddl = "O:BAG:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Droits qui permettent de changer le contenu ou la sécurité d'un dossier.</summary>
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Droits génériques (bits hauts), que FileSystemRights ne nomme pas : GENERIC_ALL et GENERIC_WRITE.</summary>
    private const int GenericAllOrWrite = 0x10000000 | 0x40000000;

    /// <summary>%ProgramData%\PCPerfSuite.</summary>
    public static string RootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);

    /// <summary>Crée (ou vérifie) la racine, puis chaque sous-dossier de <paramref name="segments"/>, qui héritent de sa
    /// liste d'accès. Chaque segment est un nom simple (« Tools », « cpu-z », « 3.01 »).</summary>
    public static SecureFolderResult TryEnsure(params string[] segments) => TryEnsure(RootPath, segments);

    /// <summary>État du dossier sans rien créer, pour le diagnostic.</summary>
    public static SecureFolderResult Inspect()
    {
        try
        {
            string root = RootPath;
            if (!Directory.Exists(root))
            {
                return new SecureFolderResult(null, "pas encore créé (il le sera au premier outil portable)") { IsAbsent = true };
            }
            return CheckExisting(root) is { } refusal ? new SecureFolderResult(null, refusal) : new SecureFolderResult(root, null);
        }
        catch (Exception ex)
        {
            return new SecureFolderResult(null, $"lecture impossible ({ex.Message})");
        }
    }

    /// <summary>Chemin d'un sous-dossier existant, vérifié, sans rien créer ; null s'il n'existe pas ou s'il est refusé.</summary>
    public static string? TryGetExisting(params string[] segments)
    {
        try
        {
            string root = RootPath;
            if (!Directory.Exists(root) || CheckExisting(root) is not null) return null;

            string current = root;
            foreach (string segment in segments)
            {
                if (!IsPlainName(segment)) return null;
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) || IsLink(current)) return null;
            }

            return current;
        }
        catch
        {
            return null;
        }
    }

    internal static SecureFolderResult TryEnsure(string root, IReadOnlyList<string> segments)
    {
        try
        {
            foreach (string segment in segments)
            {
                if (!IsPlainName(segment)) return new SecureFolderResult(null, $"nom de dossier refusé (« {segment} »)");
            }

            string? parent = Path.GetDirectoryName(root);
            if (parent is null || !Directory.Exists(parent)) return new SecureFolderResult(null, $"dossier {parent} introuvable");
            if (IsLink(parent)) return new SecureFolderResult(null, $"{parent} est un lien vers un autre emplacement");

            if (!Directory.Exists(root))
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm(SecureSddl);

                // Créé d'emblée avec sa liste d'accès : aucun instant où il serait ouvert à tous. S'il est apparu entre-temps,
                // Create ne fait rien et la vérification qui suit le jugera comme un dossier existant.
                new DirectoryInfo(root).Create(security);
                OfficialInstaller.RestrictToElevatedProcesses(root);
            }

            if (CheckExisting(root) is { } refusal) return new SecureFolderResult(null, refusal);

            string current = root;
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current))
                {
                    if (IsLink(current)) return new SecureFolderResult(null, $"{current} est un lien vers un autre emplacement");
                }
                else
                {
                    Directory.CreateDirectory(current);
                }
            }

            return new SecureFolderResult(current, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new SecureFolderResult(null, "droits insuffisants : il faut lancer PCPerfSuite en administrateur");
        }
        catch (Exception ex)
        {
            return new SecureFolderResult(null, $"création impossible ({ex.Message})");
        }
    }

    /// <summary>Raison de refuser un dossier existant, null s'il est sûr.</summary>
    private static string? CheckExisting(string root)
    {
        if (IsLink(root)) return $"{root} est un lien vers un autre emplacement : PCPerfSuite ne s'en sert pas";

        DirectorySecurity security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        return DescribeUntrusted(security) is { } why
            ? $"{root} {why} : PCPerfSuite ne s'en sert pas. Supprime ce dossier (il ne contient que des outils re-téléchargeables), puis réessaie"
            : null;
    }

    /// <summary>Pourquoi cette sécurité n'est pas sûre (« appartient à … », « laisse … le modifier »), null si elle l'est.
    /// Pure, pour être testée sur une description SDDL.</summary>
    internal static string? DescribeUntrusted(DirectorySecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return "n'a pas de propriétaire lisible";
        if (!IsTrusted(owner)) return $"appartient à un autre compte que les administrateurs ({PublicName(owner)})";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || IsTrusted(sid)) continue;

            bool canWrite = (rule.FileSystemRights & WriteRights) != 0 || ((int)rule.FileSystemRights & GenericAllOrWrite) != 0;
            if (canWrite) return $"laisse {PublicName(sid)} le modifier";
        }

        return null;
    }

    /// <summary>Vrai pour un nom de dossier simple : ni séparateur, ni « . » ou « .. », ni caractère interdit.</summary>
    public static bool IsPlainName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name.Length <= 64
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>Jonction, lien symbolique ou tout autre point d'analyse : il mènerait ailleurs.</summary>
    internal static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsTrusted(SecurityIdentifier sid) => sid == LocalSystem || sid == Administrators || sid == TrustedInstaller;

    /// <summary>Nom d'un groupe intégré (« BUILTIN\Utilisateurs », « Tout le monde ») ; un compte local ou de domaine reste
    /// anonyme : ce texte part dans le rapport de compatibilité, souvent collé dans un espace public.</summary>
    private static string PublicName(SecurityIdentifier sid)
    {
        if (sid.AccountDomainSid is not null) return "un compte utilisateur";

        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }
    }
}
