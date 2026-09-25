using System.Text.Json;
using System.Text.Json.Serialization;
using PCPerfSuite.Core.Hardware;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Overlay;

namespace PCPerfSuite.Core.PowerSettings;

public sealed class AppSettings
{
    /// <summary>GUID du plan "Performances ultimes" une fois dupliqué, pour éviter d'en recréer un à chaque lancement
    /// (le nom du plan est localisé par Windows donc on ne peut pas le retrouver de façon fiable par son nom).</summary>
    public string? UltimatePerformanceGuid { get; set; }

    /// <summary>Intervalle de rafraîchissement du monitoring, en millisecondes.</summary>
    public int MonitoringRefreshMs { get; set; } = 1000;

    /// <summary>Cadence imposée par groupe de capteurs, en millisecondes (clé = nom du SensorGroup). Un groupe
    /// absent est en cadence automatique, déduite du coût mesuré de sa lecture.</summary>
    public Dictionary<string, int> SensorGroupIntervalsMs { get; set; } = new();

    /// <summary>Ancienne sélection des tuiles "Mes métriques", d'avant les tuiles graphiques. Lue seulement pour
    /// initialiser <see cref="MonitoringSensorIds"/>.</summary>
    public List<string>? MonitoringMetricIds { get; set; }

    /// <summary>Capteurs affichés en tuiles graphiques dans le Monitoring (catalogue de métriques et capteurs propres
    /// à la machine) — null tant que l'utilisateur n'a rien personnalisé depuis les tuiles graphiques.</summary>
    public List<string>? MonitoringSensorIds { get; set; }

    /// <summary>Courbes de ventilation configurées par l'utilisateur, une par capteur de contrôle piloté.</summary>
    public List<FanCurveConfig> FanCurves { get; set; } = new();

    /// <summary>Réglages de contrôle GPU (limite de puissance, overclocking, ventilateurs NVAPI).</summary>
    public GpuControlSettings Gpu { get; set; } = new();

    /// <summary>Réglages de contrôle du processeur (limites de puissance en watts).</summary>
    public CpuControlSettings Cpu { get; set; } = new();

    /// <summary>Réglages de l'overlay en jeu (RTSS et/ou fenêtre PCPerfSuite).</summary>
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>Réglages de l'onglet Processus.</summary>
    public ProcessesSettings Processes { get; set; } = new();

    /// <summary>Comportement de la fenêtre principale de PCPerfSuite.</summary>
    public AppWindowSettings Window { get; set; } = new();

    /// <summary>Réglages de l'onglet Nettoyage.</summary>
    public CleanupSettings Cleanup { get; set; } = new();

    /// <summary>Valeur d'un réglage d'alimentation Windows telle qu'elle était avant que l'app n'y touche,
    /// clé = "guidSousGroupe/guidRéglage". Permet à « décocher » de rendre exactement ce qui était en place
    /// plutôt qu'une valeur par défaut supposée, qui n'est pas forcément celle de ce PC.</summary>
    public Dictionary<string, uint> OriginalPowerValues { get; set; } = new();
}

/// <summary>Réglages de l'onglet Nettoyage.</summary>
public sealed class CleanupSettings
{
    /// <summary>« Tout nettoyer » vide aussi la corbeille. Désactivé par défaut : contrairement à un cache,
    /// ce qu'on vide de la corbeille ne se régénère pas, et l'utilisateur doit l'avoir choisi.</summary>
    public bool IncludeRecycleBinInCleanAll { get; set; }
}

/// <summary>
/// Comportement de la fenêtre principale — des réglages propres à PCPerfSuite, à ne pas confondre
/// avec les réglages Windows de <see cref="PerformanceTweak"/>.
/// </summary>
public sealed class AppWindowSettings
{
    /// <summary>Le bouton de fermeture range l'app dans la zone de notification au lieu de la quitter ;
    /// on quitte alors par « Quitter » dans le menu de l'icône. Activé par défaut : le monitoring, les
    /// courbes de ventilation et l'overlay en jeu n'ont d'intérêt que s'ils continuent fenêtre fermée.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Le message expliquant que l'app continue en arrière-plan n'est affiché qu'une fois :
    /// passé la première fermeture, l'utilisateur sait où retrouver la fenêtre.</summary>
    public bool TrayHintShown { get; set; }
}

/// <summary>
/// Réglages de la liste des processus. Le texte de recherche n'est volontairement pas enregistré : retrouver
/// l'app filtrée sur trois lignes au lancement suivant serait déroutant plus qu'utile.
/// Tout est stocké en chaînes plutôt qu'en enums : faute de JsonStringEnumConverter, les enums partent en
/// nombres dans le fichier (voir <see cref="OverlayAnchor"/>), ce qui se réinterprète silencieusement à la
/// moindre valeur insérée au milieu d'une énumération — et rend settings.json illisible à la main.
/// </summary>
public sealed class ProcessesSettings
{
    /// <summary>Intervalle entre deux relevés, en millisecondes. Un relevé de plusieurs centaines de
    /// processus coûte bien plus cher qu'une lecture de capteurs, d'où un défaut plus lent que le Monitoring.</summary>
    public int RefreshMs { get; set; } = 2000;

    /// <summary>Identifiant de la colonne de tri ("cpu", "memory"…). Une colonne inconnue (retirée dans une
    /// version ultérieure) fait simplement retomber sur le tri par défaut.</summary>
    public string SortColumnId { get; set; } = "cpu";

    public bool SortDescending { get; set; } = true;

    /// <summary>Colonnes affichées — null tant que l'utilisateur n'a pas touché au sélecteur, auquel cas le
    /// jeu par défaut s'applique.</summary>
    public List<string>? VisibleColumnIds { get; set; }

    /// <summary>Famille affichée : "all", "apps", "background" ou "windows".</summary>
    public string KindFilter { get; set; } = "all";

    /// <summary>Affiche le panneau de détail sous la liste pour le processus sélectionné.</summary>
    public bool ShowDetails { get; set; } = true;
}

public sealed class GpuControlSettings
{
    /// <summary>Null tant que l'utilisateur n'a jamais touché le slider — on n'impose alors rien au
    /// démarrage, la carte reste sur son réglage par défaut.</summary>
    public float? PowerLimitPercent { get; set; }

    // Ancienne courbe de ventilation GPU, du temps où elle vivait dans l'onglet GPU. Conservée en
    // lecture seule : au premier lancement, l'onglet Ventilateurs s'en sert pour créer la carte du
    // ventilateur GPU sans perdre les réglages, puis c'est FanCurves qui fait foi.
    public FanControlMode FanMode { get; set; } = FanControlMode.Auto;
    public float FanManualPercent { get; set; } = 60;
    public FanTempSource FanSource { get; set; } = FanTempSource.GpuCore;
    public List<FanCurvePoint> FanPoints { get; set; } = FanCurveMath.EquilibrePoints();

    /// <summary>Décalage d'horloge cœur, en MHz (0 = fréquences d'origine).</summary>
    public int CoreClockOffsetMhz { get; set; }

    /// <summary>Décalage d'horloge mémoire, en MHz (0 = fréquences d'origine).</summary>
    public int MemoryClockOffsetMhz { get; set; }

    /// <summary>Limite de température en °C — null tant que l'utilisateur n'y a pas touché.</summary>
    public int? TemperatureLimitC { get; set; }

    /// <summary>Ancien champ : surtension cœur NVIDIA en %. Relu tant que <see cref="VoltageValue"/> est
    /// vide, pour ne pas perdre les réglages des versions précédentes.</summary>
    public int? VoltageBoostPercent { get; set; }

    /// <summary>Tension choisie, dans l'unité <see cref="VoltageUnit"/> — null si jamais modifiée.</summary>
    public int? VoltageValue { get; set; }

    public GpuVoltageUnit? VoltageUnit { get; set; }

    /// <summary>Marque de la carte sur laquelle ces réglages ont été faits (null = NVIDIA, seule marque
    /// gérée avant). Après un changement de carte, on ne réapplique pas au démarrage un overclock pensé
    /// pour une autre.</summary>
    public GpuVendor? OverclockVendor { get; set; }

    /// <summary>Intel exige que l'utilisateur accepte explicitement la renonciation de garantie avant
    /// tout overclock ; l'accord est mémorisé ici, comme le prévoit la documentation IGCL.</summary>
    public bool IntelOverclockWaiverAccepted { get; set; }

    /// <summary>Profils d'overclocking enregistrés par l'utilisateur.</summary>
    public List<GpuOverclockProfile> OverclockProfiles { get; set; } = new();

    /// <summary>Réapplique l'overclock au lancement de l'app — et, dans ce cas seulement, le laisse en
    /// place en quittant. Décoché (défaut), la carte repart toujours d'origine.</summary>
    public bool ApplyOverclockAtStartup { get; set; }

    public (int Value, GpuVoltageUnit Unit)? GetVoltage()
    {
        if (VoltageValue is { } value) return (value, VoltageUnit ?? GpuVoltageUnit.Percent);
        if (VoltageBoostPercent is { } legacy) return (legacy, GpuVoltageUnit.Percent);
        return null;
    }
}

/// <summary>
/// Réglages du processeur. Les limites de puissance ne survivent pas à un redémarrage (le firmware les
/// repose à chaque démarrage) : ce qui est enregistré ici sert à les réappliquer, jamais à supposer
/// qu'elles sont encore en place.
/// </summary>
public sealed class CpuControlSettings
{
    /// <summary>Limite soutenue en watts (PL1 chez Intel, PPT/STAPM chez AMD) — null tant que
    /// l'utilisateur n'y a pas touché, auquel cas on laisse le processeur tel que le firmware l'a réglé.</summary>
    public float? SustainedWatts { get; set; }

    /// <summary>Limite courte durée en watts (PL2 chez Intel, limite rapide chez AMD).</summary>
    public float? BurstWatts { get; set; }

    /// <summary>Réapplique les limites au lancement — et, dans ce cas seulement, les laisse en place en
    /// quittant. Décoché (défaut), le processeur repart toujours de ses limites d'origine.</summary>
    public bool ApplyAtStartup { get; set; }

    /// <summary>L'utilisateur a lu et accepté l'avertissement avant la première écriture. Mémorisé pour ne
    /// pas le reposer à chaque réglage.</summary>
    public bool RiskAccepted { get; set; }

    /// <summary>Profils enregistrés par l'utilisateur : tout l'onglet Processeur sous un nom.</summary>
    public List<CpuProfile> Profiles { get; set; } = new();
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }

    /// <summary>Identifiants (catalogue de métriques) affichés dans l'OSD — null dans un fichier d'avant la
    /// sélection libre, dérivé alors de ShowCpu/ShowGpu/ShowRam.</summary>
    public List<string>? MetricIds { get; set; }

    /// <summary>Une ligne par métrique au lieu d'une ligne par catégorie.</summary>
    public bool OneLinePerMetric { get; set; }

    /// <summary>Cadence de rafraîchissement de l'overlay, en millisecondes. Plancher : la cadence du
    /// monitoring, qui est la source des valeurs.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Pousse le texte dans l'overlay de RTSS (fonctionne en plein écran exclusif).</summary>
    public bool UseRtss { get; set; } = true;

    /// <summary>Affiche l'overlay fenêtre de PCPerfSuite (jeux fenêtrés/sans bordure, police et
    /// couleurs entièrement personnalisables).</summary>
    public bool UseWindow { get; set; }

    public OverlayAppearanceSettings Appearance { get; set; } = new();

    // Anciens interrupteurs, lus uniquement pour la migration : remis à null (donc retirés du JSON) dès
    // que MetricIds est enregistré.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowCpu { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowGpu { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowRam { get; set; }
}

/// <summary>Apparence de l'overlay : police, taille et couleurs. Les couleurs sont stockées en
/// "#RRGGBB" pour rester lisibles/modifiables à la main dans settings.json.</summary>
public sealed class OverlayAppearanceSettings
{
    public string FontFamily { get; set; } = "Consolas";

    /// <summary>Taille de police de l'overlay fenêtre, en pixels.</summary>
    public double FontSize { get; set; } = 18;

    /// <summary>Taille du texte envoyé à RTSS, en % de la police configurée dans RTSS (balise
    /// &lt;S=...&gt;). 100 = taille RTSS d'origine.</summary>
    public int RtssSizePercent { get; set; } = 100;

    /// <summary>Colore le nom de chaque ligne (CPU, RAM, NET...) avec la couleur de sa catégorie.</summary>
    public bool UseCategoryColors { get; set; } = true;

    /// <summary>Envoie aussi les couleurs à RTSS via ses balises de mise en forme. À décocher si une
    /// version de RTSS trop ancienne affiche les balises en clair au lieu de les interpréter.</summary>
    public bool SendColorsToRtss { get; set; } = true;

    /// <summary>Couleur des valeurs (les libellés, eux, prennent la couleur de leur catégorie).</summary>
    public string ValueColor { get; set; } = "#FFFFFF";

    /// <summary>Couleur par catégorie, clé = MetricCategory.Key. Une catégorie absente garde la
    /// couleur par défaut du catalogue.</summary>
    public Dictionary<string, string> CategoryColors { get; set; } = new();

    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;

    /// <summary>Marge depuis le bord de l'écran, en pixels (overlay fenêtre).</summary>
    public int MarginX { get; set; } = 24;

    public int MarginY { get; set; } = 24;

    /// <summary>Opacité du fond noir derrière le texte de l'overlay fenêtre (0 = aucun fond).</summary>
    public double BackgroundOpacity { get; set; } = 0.45;
}

/// <summary>
/// Petit stockage JSON local pour l'état de l'app (pas besoin d'une DB pour si peu).
///
/// Deux précautions, parce que ce fichier est la seule trace des réglages de l'utilisateur :
/// - toutes les lectures et écritures passent par un verrou. Les ViewModels enregistrent depuis
///   le thread UI pendant que <see cref="PowerPlanService"/> lit et écrit depuis le pool de
///   threads ; sans verrou, deux accès simultanés se heurtent sur un partage de fichier refusé et
///   l'enregistrement se perd en silence ;
/// - l'écriture est atomique (fichier temporaire puis remplacement). Une écriture directe tronque
///   le fichier avant de le réécrire : une coupure au milieu laisse un JSON incomplet, que la
///   lecture suivante ne peut que jeter — tous les réglages avec.
/// </summary>
public static class AppSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCPerfSuite", "settings.json");

    private static readonly object Gate = new();

    /// <summary>Raison du dernier échec de lecture ou d'enregistrement, null tant que tout va bien.
    /// Affichée dans le diagnostic « Compatibilité de ce PC » : un réglage qui ne s'enregistre pas
    /// doit se voir, pas disparaître sans un mot.</summary>
    public static string? LastError { get; private set; }

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings is not null) return settings;
                }

                return new AppSettings();
            }
            catch (Exception ex)
            {
                // Le fichier illisible est mis de côté plutôt qu'écrasé au prochain enregistrement :
                // c'est la seule trace de ce que l'utilisateur avait réglé, et de quoi comprendre
                // ce qui l'a abîmé.
                TryBackupUnreadableFile();
                LastError = $"Réglages illisibles, remis à zéro ({ex.Message}).";
                return new AppSettings();
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            string temp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

                // Écriture atomique : on écrit un fichier temporaire complet, puis on le met à la place
                // de l'ancien par un remplacement que le système de fichiers garantit indivisible.
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"Enregistrement des réglages impossible ({ex.Message}).";
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Renomme le fichier illisible en .corrupt pour qu'il survive à l'enregistrement suivant.</summary>
    private static void TryBackupUnreadableFile()
    {
        try
        {
            if (File.Exists(FilePath)) File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch { /* best-effort : si on n'y arrive pas, le fichier sera écrasé, tant pis */ }
    }
}
