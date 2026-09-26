namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>
/// Repère les connecteurs de la carte mère où aucun ventilateur ne semble branché : 0 tr/min alors que la carte
/// les alimente. La puce Super I/O expose tous ses canaux, branchés ou non — la version 0.9.6 de la bibliothèque
/// de capteurs ne masque plus ceux qui restent à 0 — et rien dans ses registres ne dit « connecteur vide ».
///
/// Un connecteur à 0 tr/min est vide OU porte un ventilateur dont on ne lit pas la vitesse (2 broches, ou port
/// secondaire d'un hub : un seul fil de vitesse pour tous). Les deux se ressemblent : c'est pourquoi le
/// résultat ne sert qu'à regrouper ces canaux à part, jamais à les masquer ni à les rendre non pilotables.
///
/// L'état vit le temps d'une session, sans être enregistré : un ventilateur qu'on branche à chaud remonte
/// aussitôt dans sa catégorie et n'en ressort plus.
/// </summary>
public sealed class EmptyHeaderDetector
{
    /// <summary>Au-delà de cette vitesse, un ventilateur tourne. Sous ce seuil, une lecture parasite d'un
    /// connecteur vide (quelques dizaines de tr/min) ne compte pas.</summary>
    public const float SpinningRpm = 100;

    /// <summary>Commande minimale pour conclure. Avec une commande à 0 % (mode « 0 dB » du BIOS, « arrêt à froid »
    /// de l'app), un ventilateur bien branché est arrêté sans que rien ne cloche : on ne conclut rien.</summary>
    public const float DrivenPercent = 20;

    private readonly HashSet<string> _hasSpun = new(StringComparer.Ordinal);

    /// <summary>Enregistre le relevé d'un ventilateur et dit si son connecteur paraît vide.</summary>
    /// <param name="fanId">Identifiant stable du ventilateur.</param>
    /// <param name="rpm">Vitesse lue, null tant qu'elle n'a pas été lue.</param>
    /// <param name="percent">Commande lue (% de la puce), null si elle ne se lit pas.</param>
    public bool Observe(string fanId, FanCategory category, float? rpm, float? percent)
    {
        // Une carte graphique arrête ses ventilateurs au repos : 0 tr/min est son fonctionnement normal.
        if (category == FanCategory.Gpu) return false;

        if (rpm is > SpinningRpm)
        {
            _hasSpun.Add(fanId);
            return false;
        }

        if (_hasSpun.Contains(fanId)) return false;

        return rpm is not null && percent is > DrivenPercent;
    }
}
