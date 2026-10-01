using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.Installations;

/// <summary>Rubrique de la Boîte à outils.</summary>
public enum ToolCategory
{
    Diagnostic,
    StressAndBench,
    GpuAndDrivers,
    Misc,
}

/// <summary>Comment un outil arrive sur le PC.</summary>
public enum ToolDelivery
{
    /// <summary>Un exe autonome signé, posé dans le dossier sécurisé (%ProgramData%\PCPerfSuite\Tools) et lancé de là.</summary>
    PortableExe,

    /// <summary>Une archive zip extraite dans le dossier sécurisé ; son exe principal est vérifié à chaque lancement.</summary>
    PortableZip,

    /// <summary>Un installeur signé (exe ou msi), lancé sur clic, après confirmation.</summary>
    Installer,

    /// <summary>Une archive zip qui contient un installeur signé (miroir de Guru3D) : extrait, vérifié, puis lancé.</summary>
    ZipInstaller,

    /// <summary>Téléchargé, vérifié (empreinte, et signature si l'éditeur signe), puis remis dans Téléchargements :
    /// PCPerfSuite ne le lance jamais. C'est le sort de tout outil non signé.</summary>
    DownloadOnly,

    /// <summary>Aucun lien direct utilisable : page officielle seulement, avec la raison (<see cref="ToolDefinition.NoDirectLinkReason"/>).</summary>
    OfficialPageOnly,

    /// <summary>Fourni par Windows : rien à télécharger.</summary>
    BuiltIn,
}

/// <summary>Ce que la version gratuite permet à un technicien qui facture son intervention (décision D5).</summary>
public enum CommercialUse
{
    Allowed,

    /// <summary>La version gratuite l'interdit, ou il faut une licence payante : badge « licence pro requise ».</summary>
    ProLicenceRequired,

    /// <summary>L'éditeur ne publie pas de licence.</summary>
    Unknown,
}

/// <summary>Licence d'un outil, en clair, et ce qu'elle dit de l'usage commercial.</summary>
public sealed record ToolLicence(string Summary, CommercialUse CommercialUse)
{
    public bool RequiresProLicence => CommercialUse == CommercialUse.ProLicenceRequired;
}

/// <summary>Comment savoir si un outil est déjà sur ce PC.</summary>
public enum ToolDetectionKind
{
    None,

    /// <summary>Dossier de l'outil dans %ProgramData%\PCPerfSuite\Tools.</summary>
    PortableFolder,

    /// <summary>Inscription dans « Applications installées » (clés Uninstall du registre).</summary>
    UninstallEntry,

    /// <summary>Détection propre à PawnIO (<see cref="Hardware.Cpu.PawnIoDriver"/>).</summary>
    PawnIo,

    /// <summary>Détection propre à RTSS (<see cref="RtssInstallation"/>).</summary>
    Rtss,
}

/// <summary>
/// Ce que PCPerfSuite sait d'un outil sans jamais le lire sur le réseau : identifiant, hôtes autorisés, éditeur
/// attendu (relevé sur le vrai fichier) ou « non signé », type de fichier, manière de le détecter. Le catalogue en
/// ligne ne donne que la version, l'adresse versionnée, l'empreinte et la taille, et chacune de ses adresses doit
/// tomber sur un hôte d'ici : un catalogue compromis ne peut ni changer d'éditeur, ni pointer ailleurs.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>Identifiant stable en kebab-case, nom du dossier de l'outil portable.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Éditeur de l'outil, tel que l'utilisateur le connaît.</summary>
    public required string Editor { get; init; }

    public required ToolCategory Category { get; init; }

    /// <summary>À quoi il sert, en une ou deux phrases.</summary>
    public required string Purpose { get; init; }

    public required ToolLicence Licence { get; init; }

    /// <summary>Page officielle, en HTTPS ; null pour un outil fourni par Windows.</summary>
    public string? OfficialPage { get; init; }

    public required ToolDelivery Delivery { get; init; }

    /// <summary>Seuls hôtes acceptés pour le téléchargement et chacune de ses redirections.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();

    /// <summary>Organisation du certificat Authenticode (O=), relevée sur le vrai fichier ; null : l'éditeur ne signe
    /// pas, et le fichier n'est jamais lancé. Pour une archive, c'est l'éditeur de l'exe qu'elle contient.</summary>
    public string? ExpectedPublisher { get; init; }

    /// <summary>Type du fichier téléchargé.</summary>
    public InstallerFileKind FileKind { get; init; } = InstallerFileKind.Exe;

    /// <summary>Nom du fichier quand l'adresse n'en donne pas d'utilisable (« …/os:Windows »).</summary>
    public string? FallbackFileName { get; init; }

    /// <summary>Outil portable : chemin relatif de l'exe à lancer dans son dossier. Archive d'installeur : motif du
    /// nom de l'installeur qu'elle contient (« RTSSSetup*.exe »). Outil de Windows : nom de l'exe dans System32.</summary>
    public string? LaunchFile { get; init; }

    /// <summary>Arguments de l'installeur : vide pour qu'il s'affiche, l'utilisateur voit ce qu'il installe.</summary>
    public string InstallerArguments { get; init; } = "";

    public long MaxBytes { get; init; } = OfficialInstaller.DefaultMaxBytes;

    /// <summary>Taille maximale une fois l'archive extraite.</summary>
    public long MaxExtractedBytes { get; init; }

    public TimeSpan DownloadTimeout { get; init; } = OfficialInstaller.DefaultDownloadTimeout;

    public ToolDetectionKind Detection { get; init; }

    /// <summary>Début du nom affiché dans « Applications installées » (« Display Driver Uninstaller »).</summary>
    public string? UninstallDisplayName { get; init; }

    /// <summary>Nom de la clé Uninstall, quand il est plus sûr que le nom affiché (« Afterburner »).</summary>
    public string? UninstallKeyName { get; init; }

    /// <summary>Exe à lancer, relatif au dossier d'installation (null : rien à lancer, comme un pilote).</summary>
    public string? InstalledExe { get; init; }

    /// <summary>Identifiant winget, pour le repli « winget install --id … --exact ».</summary>
    public string? WingetId { get; init; }

    /// <summary>Pourquoi il n'y a pas de téléchargement direct (<see cref="ToolDelivery.OfficialPageOnly"/>).</summary>
    public string? NoDirectLinkReason { get; init; }

    /// <summary>Précision affichée sous l'outil.</summary>
    public string? Note { get; init; }

    /// <summary>Téléchargement, vérification et lancement essayés sur une vraie machine. Faux : « expérimental » (règle 6).</summary>
    public bool IsVerified { get; init; }

    public bool IsSigned => ExpectedPublisher is not null;

    public bool HasDirectDownload => Delivery is not (ToolDelivery.OfficialPageOnly or ToolDelivery.BuiltIn);

    public bool IsPortable => Delivery is ToolDelivery.PortableExe or ToolDelivery.PortableZip;

    public bool IsInstaller => Delivery is ToolDelivery.Installer or ToolDelivery.ZipInstaller;
}

/// <summary>
/// Les outils de la Boîte à outils, figés dans l'app. Liste choisie par Denis le 01/10/2026 ; éditeurs relevés le même
/// jour sur les fichiers officiels (signature Authenticode, champ O= du certificat), licences relevées sur les pages
/// des éditeurs. Seuls PawnIO et OCCT ont un lien « dernière version » stable : les autres passent par l'adresse
/// versionnée du catalogue, régénéré chaque jour (tools/catalogue).
/// </summary>
public static class ToolCatalog
{
    private const long Mo = 1024L * 1024;

    private static readonly IReadOnlyList<string> SourceForgeHosts = new[] { "downloads.sourceforge.net", "*.dl.sourceforge.net" };
    private static readonly IReadOnlyList<string> WagnardsoftHosts = new[] { "www.wagnardsoft.com", "download.wagnardsoft.com" };
    private static readonly IReadOnlyList<string> Guru3DMirrorHosts = new[] { "ftp.nluug.nl" };

    private const string TechPowerUpReason =
        "TechPowerUp ne sert ses fichiers que par des liens qui expirent au bout de 24 heures : PCPerfSuite ouvre sa page officielle.";

    public static IReadOnlyList<ToolDefinition> All { get; } = new[]
    {
        // Diagnostic
        new ToolDefinition
        {
            Id = "cpu-z", Name = "CPU-Z", Editor = "CPUID", Category = ToolCategory.Diagnostic,
            Purpose = "Identifie le processeur, la carte mère et la mémoire (modèle, fréquences, timings), avec un petit bench processeur.",
            Licence = new("Gratuit, usage personnel et professionnel.", CommercialUse.Allowed),
            OfficialPage = "https://www.cpuid.com/softwares/cpu-z.html",
            Delivery = ToolDelivery.PortableZip, AllowedHosts = new[] { "download.cpuid.com" }, ExpectedPublisher = "CPUID",
            FileKind = InstallerFileKind.Zip, LaunchFile = "cpuz_x64.exe", MaxBytes = 40 * Mo, MaxExtractedBytes = 100 * Mo,
            Detection = ToolDetectionKind.PortableFolder, WingetId = "CPUID.CPU-Z",
        },
        new ToolDefinition
        {
            Id = "gpu-z", Name = "GPU-Z", Editor = "TechPowerUp", Category = ToolCategory.Diagnostic,
            Purpose = "Fiche détaillée de la carte graphique : puce, mémoire, BIOS, capteurs et lien PCIe.",
            Licence = new("Gratuit, y compris en usage professionnel ; interdit de l'intégrer à un produit commercial.", CommercialUse.Allowed),
            OfficialPage = "https://www.techpowerup.com/gpuz/",
            Delivery = ToolDelivery.OfficialPageOnly, NoDirectLinkReason = TechPowerUpReason,
        },
        new ToolDefinition
        {
            Id = "hwinfo", Name = "HWiNFO", Editor = "REALiX", Category = ToolCategory.Diagnostic,
            Purpose = "Inventaire complet du matériel et relevé de tous les capteurs, avec journal.",
            Licence = new("Gratuit en usage non commercial ; un technicien a besoin de la licence Pro (Engineer ou Corporate).",
                CommercialUse.ProLicenceRequired),
            OfficialPage = "https://www.hwinfo.com/download/",
            Delivery = ToolDelivery.OfficialPageOnly,
            NoDirectLinkReason = "HWiNFO refuse les téléchargements qui ne viennent pas d'un navigateur, et ses miroirs retirent " +
                                 "chaque version à la sortie de la suivante : PCPerfSuite ouvre sa page officielle.",
        },
        new ToolDefinition
        {
            Id = "crystaldiskinfo", Name = "CrystalDiskInfo", Editor = "Crystal Dew World", Category = ToolCategory.Diagnostic,
            Purpose = "État de santé des disques (SMART) : usure, secteurs réalloués, température, heures de fonctionnement.",
            Licence = new("Libre (licence MIT), usage professionnel compris.", CommercialUse.Allowed),
            OfficialPage = "https://crystalmark.info/en/software/crystaldiskinfo/",
            Delivery = ToolDelivery.PortableZip, AllowedHosts = SourceForgeHosts, ExpectedPublisher = "CrystalMark Inc.",
            FileKind = InstallerFileKind.Zip, LaunchFile = "DiskInfo64.exe", MaxBytes = 50 * Mo, MaxExtractedBytes = 150 * Mo,
            Detection = ToolDetectionKind.PortableFolder, WingetId = "CrystalDewWorld.CrystalDiskInfo",
        },

        // Stress et bench
        new ToolDefinition
        {
            Id = "occt", Name = "OCCT", Editor = "OCBase", Category = ToolCategory.StressAndBench,
            Purpose = "Tests de stabilité du processeur, de la mémoire, du GPU et de l'alimentation, avec relevé des capteurs et détection d'erreurs.",
            Licence = new("Édition Personal gratuite pour un usage personnel seulement ; un technicien a besoin d'OCCT Pro.",
                CommercialUse.ProLicenceRequired),
            OfficialPage = "https://www.ocbase.com/download",
            Delivery = ToolDelivery.PortableExe, AllowedHosts = new[] { "www.ocbase.com" }, ExpectedPublisher = "OCBASE",
            FallbackFileName = "OCCT.exe", LaunchFile = "OCCT.exe", MaxBytes = 400 * Mo, DownloadTimeout = TimeSpan.FromMinutes(30),
            Detection = ToolDetectionKind.PortableFolder, WingetId = "OCBase.OCCT.Personal",
        },
        new ToolDefinition
        {
            Id = "prime95", Name = "Prime95", Editor = "GIMPS (Mersenne Research)", Category = ToolCategory.StressAndBench,
            Purpose = "Test de stabilité de référence du processeur et de la mémoire (« Torture Test »).",
            Licence = new("Gratuit, usage professionnel compris, sur un PC dont le propriétaire a donné son accord.", CommercialUse.Allowed),
            OfficialPage = "https://www.mersenne.org/download/",
            Delivery = ToolDelivery.DownloadOnly, AllowedHosts = new[] { "www.mersenne.org" },
            FileKind = InstallerFileKind.Zip, MaxBytes = 50 * Mo,
        },
        new ToolDefinition
        {
            Id = "furmark", Name = "FurMark 2", Editor = "Geeks3D", Category = ToolCategory.StressAndBench,
            Purpose = "Test de charge et de chauffe de la carte graphique.",
            Licence = new("Gratuit en usage personnel ; en usage commercial, le Geeks3D PRO Pack (payant) est requis.",
                CommercialUse.ProLicenceRequired),
            OfficialPage = "https://www.geeks3d.com/furmark/",
            Delivery = ToolDelivery.DownloadOnly, AllowedHosts = new[] { "gpumagick.com", "geeks3d.com" },
            FileKind = InstallerFileKind.Zip, MaxBytes = 100 * Mo,
        },
        new ToolDefinition
        {
            Id = "cinebench", Name = "Cinebench R23", Editor = "Maxon", Category = ToolCategory.StressAndBench,
            Purpose = "Bench du processeur par rendu 3D, multi-cœur et mono-cœur, comparable d'un PC à l'autre.",
            Licence = new("R23 : licence personnelle, sans usage commercial prévu. Cinebench 2026, gratuit en entreprise " +
                          "(moins d'un milliard de dollars de chiffre d'affaires), est sur la page officielle.", CommercialUse.ProLicenceRequired),
            OfficialPage = "https://www.maxon.net/en/downloads/cinebench-downloads",
            Delivery = ToolDelivery.PortableZip, AllowedHosts = new[] { "installer.maxon.net" }, ExpectedPublisher = "MAXON Computer GmbH",
            FileKind = InstallerFileKind.Zip, LaunchFile = "Cinebench.exe", MaxBytes = 400 * Mo, MaxExtractedBytes = 1024 * Mo,
            DownloadTimeout = TimeSpan.FromMinutes(30), Detection = ToolDetectionKind.PortableFolder, WingetId = "Maxon.CinebenchR23",
            Note = "Version R23 (2021). La version actuelle, Cinebench 2026, pèse 2,7 Go et n'a pas d'adresse versionnée : " +
                   "elle se télécharge depuis la page officielle.",
        },
        new ToolDefinition
        {
            Id = "y-cruncher", Name = "y-cruncher", Editor = "Alexander Yee", Category = ToolCategory.StressAndBench,
            Purpose = "Calcul de décimales (pi, e…) très exigeant pour le processeur et la mémoire : révèle des instabilités qui échappent aux autres tests.",
            Licence = new("Gratuit en usage non commercial ; usage commercial sur accord de l'auteur.", CommercialUse.ProLicenceRequired),
            OfficialPage = "https://www.numberworld.org/y-cruncher/",
            Delivery = ToolDelivery.DownloadOnly, AllowedHosts = OfficialInstaller.GitHubHosts,
            FileKind = InstallerFileKind.Zip, MaxBytes = 150 * Mo,
        },
        new ToolDefinition
        {
            Id = "crystaldiskmark", Name = "CrystalDiskMark", Editor = "Crystal Dew World", Category = ToolCategory.StressAndBench,
            Purpose = "Débits et temps d'accès des disques, en lecture et en écriture.",
            Licence = new("Libre (licence MIT), usage professionnel compris.", CommercialUse.Allowed),
            OfficialPage = "https://crystalmark.info/en/software/crystaldiskmark/",
            Delivery = ToolDelivery.PortableZip, AllowedHosts = SourceForgeHosts, ExpectedPublisher = "CrystalMark Inc.",
            FileKind = InstallerFileKind.Zip, LaunchFile = "DiskMark64.exe", MaxBytes = 50 * Mo, MaxExtractedBytes = 100 * Mo,
            Detection = ToolDetectionKind.PortableFolder, WingetId = "CrystalDewWorld.CrystalDiskMark",
        },

        // GPU et pilotes
        new ToolDefinition
        {
            Id = "ddu", Name = "DDU (Display Driver Uninstaller)", Editor = "Wagnardsoft", Category = ToolCategory.GpuAndDrivers,
            Purpose = "Retire complètement un pilote graphique (NVIDIA, AMD, Intel), de préférence en mode sans échec, avant d'en installer un autre.",
            Licence = new("Libre (licence MIT), usage professionnel compris ; ne pas le rehéberger.", CommercialUse.Allowed),
            OfficialPage = "https://www.wagnardsoft.com/display-driver-uninstaller-ddu",
            Delivery = ToolDelivery.Installer, AllowedHosts = WagnardsoftHosts, ExpectedPublisher = "Wagnardsoft",
            MaxBytes = 30 * Mo, Detection = ToolDetectionKind.UninstallEntry, UninstallDisplayName = "Display Driver Uninstaller",
            InstalledExe = "Display Driver Uninstaller.exe", WingetId = "Wagnardsoft.DisplayDriverUninstaller",
        },
        new ToolDefinition
        {
            Id = "nvcleanstall", Name = "NVCleanstall", Editor = "TechPowerUp", Category = ToolCategory.GpuAndDrivers,
            Purpose = "Prépare un pilote NVIDIA allégé : on choisit les composants à installer.",
            Licence = new("Gratuit ; aucune licence publiée.", CommercialUse.Unknown),
            OfficialPage = "https://www.techpowerup.com/nvcleanstall/",
            Delivery = ToolDelivery.OfficialPageOnly, NoDirectLinkReason = TechPowerUpReason,
        },
        new ToolDefinition
        {
            Id = "rtss", Name = "RTSS (RivaTuner Statistics Server)", Editor = "Guru3D (Unwinder)", Category = ToolCategory.GpuAndDrivers,
            Purpose = "Lit les FPS des jeux et affiche l'overlay de PCPerfSuite par-dessus, y compris en plein écran exclusif.",
            Licence = new("Gratuit ; distribué seulement par Guru3D, dont ce miroir dépend.", CommercialUse.Allowed),
            OfficialPage = RtssInstallation.DownloadPageUrl,
            Delivery = ToolDelivery.ZipInstaller, AllowedHosts = Guru3DMirrorHosts, ExpectedPublisher = "MICRO-STAR INTERNATIONAL CO., LTD.",
            FileKind = InstallerFileKind.Zip, LaunchFile = "RTSSSetup*.exe", MaxBytes = 80 * Mo,
            Detection = ToolDetectionKind.Rtss, WingetId = "Guru3D.RTSS",
            Note = "Téléchargé depuis le miroir officiel de Guru3D (ftp.nluug.nl), qui sert un zip par version.",
        },
        new ToolDefinition
        {
            Id = "afterburner", Name = "MSI Afterburner", Editor = "MSI", Category = ToolCategory.GpuAndDrivers,
            Purpose = "Overclocking et ventilation des cartes graphiques, avec son propre overlay (s'appuie sur RTSS).",
            Licence = new("Gratuit ; distribué seulement par MSI et Guru3D, dont ce miroir dépend.", CommercialUse.Allowed),
            OfficialPage = "https://www.msi.com/Landing/afterburner/graphics-cards",
            Delivery = ToolDelivery.ZipInstaller, AllowedHosts = Guru3DMirrorHosts, ExpectedPublisher = "MICRO-STAR INTERNATIONAL CO., LTD.",
            FileKind = InstallerFileKind.Zip, LaunchFile = "MSIAfterburnerSetup*.exe", MaxBytes = 150 * Mo,
            Detection = ToolDetectionKind.UninstallEntry, UninstallKeyName = "Afterburner", UninstallDisplayName = "MSI Afterburner",
            InstalledExe = "MSIAfterburner.exe", WingetId = "Guru3D.Afterburner",
            Note = "Ne le laisse pas piloter la carte en même temps que l'onglet GPU de PCPerfSuite : les deux se contrediraient.",
        },

        // Divers
        new ToolDefinition
        {
            Id = "pawnio", Name = "PawnIO", Editor = "namazso", Category = ToolCategory.Misc,
            Purpose = "Pilote signé qui donne accès aux sondes bas niveau (températures et limites du processeur) ; OpenRGB s'en sert aussi pour la RAM et la carte mère.",
            Licence = new("Libre (GPL-2.0, avec exception pour les modules).", CommercialUse.Allowed),
            OfficialPage = "https://pawnio.eu/",
            Delivery = ToolDelivery.Installer, AllowedHosts = OfficialInstaller.GitHubHosts, ExpectedPublisher = "namazso",
            InstallerArguments = "-install", Detection = ToolDetectionKind.PawnIo, WingetId = "namazso.PawnIO",
            Note = "Aussi dans Paramètres › Installations, qui suit sa dernière version.",
        },
        new ToolDefinition
        {
            Id = "7-zip", Name = "7-Zip", Editor = "Igor Pavlov", Category = ToolCategory.Misc,
            Purpose = "Ouvre et crée les archives (7z, zip, rar…).",
            Licence = new("Libre (LGPL), usage professionnel compris.", CommercialUse.Allowed),
            OfficialPage = "https://www.7-zip.org/download.html",
            Delivery = ToolDelivery.DownloadOnly, AllowedHosts = OfficialInstaller.GitHubHosts, MaxBytes = 20 * Mo,
            Detection = ToolDetectionKind.UninstallEntry, UninstallDisplayName = "7-Zip",
        },
        new ToolDefinition
        {
            Id = "openrgb", Name = "OpenRGB", Editor = "OpenRGB (Adam Honse)", Category = ToolCategory.Misc,
            Purpose = "Pilote l'éclairage RGB de la plupart des marques : carte mère, RAM, ventilateurs, périphériques.",
            Licence = new("Libre (GPL-2.0).", CommercialUse.Allowed),
            OfficialPage = "https://openrgb.org/",
            Delivery = ToolDelivery.DownloadOnly, AllowedHosts = OfficialInstaller.GitHubHosts,
            FileKind = InstallerFileKind.Msi, MaxBytes = 80 * Mo,
            Detection = ToolDetectionKind.UninstallEntry, UninstallDisplayName = "OpenRGB",
            Note = "Il a besoin de PawnIO pour la RAM et la carte mère.",
        },
        new ToolDefinition
        {
            Id = "islc", Name = "ISLC (Intelligent Standby List Cleaner)", Editor = "Wagnardsoft", Category = ToolCategory.Misc,
            Purpose = "Vide la liste d'attente de la mémoire de Windows quand elle sature, ce qui peut réduire des saccades en jeu.",
            Licence = new("Gratuit ; licence fournie avec le programme, pas publiée en ligne.", CommercialUse.Unknown),
            OfficialPage = "https://www.wagnardsoft.com/forums/viewtopic.php?t=1256",
            Delivery = ToolDelivery.DownloadOnly, AllowedHosts = WagnardsoftHosts, ExpectedPublisher = "Wagnardsoft",
            MaxBytes = 20 * Mo,
            Note = "Archive auto-extractible signée par Wagnardsoft : PCPerfSuite vérifie sa signature, la dépose dans " +
                   "Téléchargements, et c'est toi qui choisis où l'extraire.",
        },
        new ToolDefinition
        {
            Id = "memtest86", Name = "MemTest86", Editor = "PassMark", Category = ToolCategory.Misc,
            Purpose = "Test complet de la mémoire vive, hors de Windows, depuis une clé USB de démarrage.",
            Licence = new("Édition Free gratuite, usage professionnel compris.", CommercialUse.Allowed),
            OfficialPage = "https://www.memtest86.com/download.htm",
            Delivery = ToolDelivery.OfficialPageOnly,
            NoDirectLinkReason = "MemTest86 démarre depuis une clé USB, hors de Windows : il se prépare avec l'outil de création de clé " +
                                 "de sa page officielle. Sans clé, le Diagnostic de mémoire Windows ci-dessous fait un premier test.",
        },
        new ToolDefinition
        {
            Id = "mdsched", Name = "Diagnostic de mémoire Windows", Editor = "Microsoft", Category = ToolCategory.Misc,
            Purpose = "Test de la mémoire intégré à Windows : il redémarre le PC et vérifie la RAM avant le démarrage de Windows.",
            Licence = new("Fourni avec Windows.", CommercialUse.Allowed),
            Delivery = ToolDelivery.BuiltIn, LaunchFile = "MdSched.exe",
            Note = "Il propose de redémarrer tout de suite ou au prochain démarrage : enregistre ton travail avant.",
        },
    };

    public static ToolDefinition? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    public static string CategoryTitle(ToolCategory category) => category switch
    {
        ToolCategory.Diagnostic => "Diagnostic",
        ToolCategory.StressAndBench => "Stress et bench",
        ToolCategory.GpuAndDrivers => "GPU et pilotes",
        _ => "Divers",
    };
}
