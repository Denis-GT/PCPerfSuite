namespace PCPerfSuite.Core.Hardware.Fans;

/// <summary>
/// Ce que refroidit un ventilateur. Déduite du nom que le matériel lui donne, jamais du modèle de la carte
/// ou du fabricant : la même règle sert sur toutes les marques.
/// </summary>
public enum FanCategory
{
    Cpu,
    Gpu,

    /// <summary>Pompe de watercooling (AIO ou boucle custom). Ne doit jamais s'arrêter.</summary>
    Pump,

    Case,

    /// <summary>Nom reconnu comme un vrai nom, mais sans rapport avec le processeur, le GPU ou le boîtier
    /// (chipset, alimentation...).</summary>
    Other,

    /// <summary>Le matériel n'a donné qu'un numéro de canal (« Fan #6 ») : impossible de savoir ce qui y est branché.</summary>
    Unidentified,
}

public static class FanCategoryInfo
{
    /// <summary>Titre de la catégorie, tel qu'affiché dans l'onglet Ventilateurs.</summary>
    public static string Title(FanCategory category) => category switch
    {
        FanCategory.Cpu => "Processeur",
        FanCategory.Gpu => "Carte graphique",
        FanCategory.Pump => "Pompe",
        FanCategory.Case => "Boîtier",
        FanCategory.Other => "Autres",
        _ => "Non identifiés",
    };
}
