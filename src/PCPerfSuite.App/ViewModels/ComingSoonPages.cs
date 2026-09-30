namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Ce qu'affiche une page du menu pas encore livrée (ComingSoonView). Sans date ni promesse chiffrée : seulement à quoi
/// servira la page. La conversation qui livre une page retire sa ligne d'ici, puis ajoute son ViewModel au menu et sa
/// vue à MainWindow (docs/navigation.md).
/// </summary>
public static class ComingSoonPages
{
    private sealed record Text(string Subtitle, string[] Bullets);

    private static readonly Dictionary<string, Text> Texts = new()
    {
        [PageKeys.Profiles] = new(
            "Des groupes de réglages processeur, GPU et ventilation, appliqués d'un clic.",
            new[]
            {
                "Un groupe s'enregistre depuis l'état actuel ou depuis les profils des onglets.",
                "Bascule automatique selon l'usage du PC (bureautique, jeu léger, jeu exigeant), désactivable.",
                "Chaque groupe reste modifiable à la main.",
            }),
        [PageKeys.AutoOverclock] = new(
            "Chercher les réglages stables de la carte graphique et du processeur, puis en tirer des profils.",
            new[]
            {
                "Trois profils : sûr, classique et agressif, validés par des tests de charge.",
                "Retour automatique aux réglages d'origine si le PC devient instable.",
                "Onglets GPU et CPU (Curve Optimizer des processeurs AMD).",
            }),
        [PageKeys.Displays] = new(
            "La fréquence de rafraîchissement de chaque écran.",
            new[]
            {
                "Essai à retour automatique : sans confirmation, l'écran reprend son réglage.",
                "La carte graphique qui pilote chaque écran, et pourquoi un réglage n'est pas possible.",
            }),
        [PageKeys.Lighting] = new(
            "L'éclairage RGB des ventilateurs, de la carte mère, de la RAM, du GPU et des périphériques.",
            new[]
            {
                "Appareils détectés, avec leur source et la raison de chaque absence.",
                "Effets simples : couleur fixe, respiration, couleur selon la température.",
            }),
        [PageKeys.LaptopGpu] = new(
            "Laisser dormir le GPU dédié du portable quand il ne sert pas, pour l'autonomie et la chaleur.",
            new[]
            {
                "Les applications qui le tiennent éveillé.",
                "La carte graphique à utiliser, application par application.",
            }),
        [PageKeys.BenchDiagnostic] = new(
            "Mesurer les performances du PC et repérer ce qui ne va pas.",
            new[]
            {
                "Tests processeur, mémoire, disque et GPU, choisis un par un.",
                "Diagnostic : ce qui est normal, anormal ou très bien, et pourquoi.",
                "Rapport à partager, et parcours guidé pour les techniciens.",
            }),
        [PageKeys.Devices] = new(
            "Les périphériques en erreur ou fantômes, et les pilotes.",
            new[]
            {
                "Inventaire par type et par connexion, avec la cause de chaque erreur.",
                "Onglet Pilotes : version, date, éditeur, sauvegarde avant toute suppression.",
            }),
        [PageKeys.Toolbox] = new(
            "Les outils utiles au diagnostic et au réglage, téléchargés depuis leur source officielle.",
            new[]
            {
                "Téléchargement vérifié quand un lien direct existe, page officielle sinon.",
                "La licence de chaque outil : usage personnel ou professionnel.",
            }),
        [PageKeys.Memory] = new(
            "Où part la mémoire vive, et le fichier d'échange.",
            new[]
            {
                "Répartition de la mémoire : utilisée, en attente, modifiée, libre.",
                "Taille et emplacement du fichier d'échange.",
                "Mémoire réservée au GPU intégré (lecture seule).",
            }),
    };

    /// <summary>Contenu de la page d'attente, ou null si cette page n'en a pas (elle est livrée).</summary>
    public static ComingSoonViewModel? Create(NavPage page)
        => Texts.TryGetValue(page.Key, out Text? text) ? new ComingSoonViewModel(page.Title, text.Subtitle, text.Bullets) : null;

    public static bool Has(string key) => Texts.ContainsKey(key);
}
