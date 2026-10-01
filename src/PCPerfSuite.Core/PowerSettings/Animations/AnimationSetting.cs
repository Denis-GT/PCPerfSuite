namespace PCPerfSuite.Core.PowerSettings.Animations;

/// <summary>Comment Windows range un réglage d'animation, et donc comment le lire et l'écrire.</summary>
public enum AnimationSettingKind
{
    /// <summary>SystemParametersInfo de la plage 0x10xx : GET dans un BOOL pointé par pvParam, SET avec la valeur
    /// dans pvParam lui-même (0 ou 1, pas un pointeur).</summary>
    SpiBool,

    /// <summary>SPI_GET/SETANIMATION : structure ANIMATIONINFO (cbSize 8, iMinAnimate).</summary>
    SpiAnimationInfo,

    /// <summary>SPI_GET/SETDRAGFULLWINDOWS : GET dans un BOOL pointé par pvParam, SET avec la valeur dans uiParam.</summary>
    SpiDragFullWindows,

    /// <summary>DWORD du profil (HKCU), suivi d'un WM_SETTINGCHANGE : Windows n'a pas d'appel SPI pour ce réglage.</summary>
    UserRegistry,
}

/// <summary>
/// Un effet visuel de Windows réglable depuis la carte « Animations et effets ». Tout passe par SystemParametersInfoW
/// (avec SPIF_UPDATEINIFILE|SPIF_SENDCHANGE : Windows écrit lui-même UserPreferencesMask, MinAnimate… et prévient les
/// fenêtres), sauf les deux réglages de l'Explorateur et du thème, qui n'existent que dans le registre.
/// </summary>
public sealed class AnimationSetting
{
    /// <summary>Clé stable, en kebab-case : c'est elle qui est enregistrée avec la valeur d'origine.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AnimationSettingKind Kind { get; init; }

    /// <summary>Codes SPI_GET… et SPI_SET… (types SPI seulement).</summary>
    public uint GetAction { get; init; }
    public uint SetAction { get; init; }

    /// <summary>Sous-clé de HKCU et nom de la valeur (type registre seulement). Une valeur absente vaut « activé » :
    /// c'est le réglage de Windows tant que personne n'y a touché.</summary>
    public string? RegistrySubKey { get; init; }
    public string? RegistryValue { get; init; }

    /// <summary>Zone annoncée dans le WM_SETTINGCHANGE qui suit l'écriture dans le registre (lParam).</summary>
    public string? ChangeArea { get; init; }

    /// <summary>Vrai pour les effets que l'interrupteur général (SPI_SETUIEFFECTS) coupe tous ensemble : ils gardent
    /// leur propre valeur, mais ne jouent plus tant qu'il est coupé.</summary>
    public bool DependsOnUiEffects { get; init; }

    /// <summary>Coupé par le préréglage « Réactif ».</summary>
    public bool InResponsivePreset { get; init; } = true;

    /// <summary>Faux tant que l'effet de l'écriture n'a pas été constaté sur une vraie machine (règle 6 de CLAUDE.md).</summary>
    public bool IsVerified { get; init; }

    /// <summary>Note affichée sous le réglage quand son application à chaud n'est pas garantie.</summary>
    public string? ApplyNote { get; init; }
}

/// <summary>Les réglages de la carte, dans l'ordre d'affichage. Codes vérifiés dans winuser.h et lus sur un PC sous
/// Windows 11 23H2 (lecture seulement : l'effet de chaque écriture reste à vérifier ; que « Effets d'animation » soit
/// bien l'interrupteur de Paramètres › Accessibilité aussi).
///
/// Absents exprès : SPI_SETMENUFADE et SPI_SETTOOLTIPFADE choisissent entre fondu et glissement, ils n'allument ni
/// n'éteignent rien (c'est SPI_SETMENUANIMATION et SPI_SETTOOLTIPANIMATION qui le font) ; le lissage des polices
/// (SPI_SETFONTSMOOTHING), que le préréglage « Réactif » doit laisser.</summary>
public static class WindowsAnimationCatalog
{
    public const string UiEffectsKey = "ui-effects";
    public const string ClientAreaAnimationKey = "client-area-animation";

    private const string ExplorerNote =
        "L'Explorateur ne relit peut-être pas ce réglage à chaud : s'il ne change rien tout de suite, il s'applique "
        + "à la prochaine ouverture de session.";

    public static IReadOnlyList<AnimationSetting> All { get; } = new AnimationSetting[]
    {
        new()
        {
            Key = UiEffectsKey,
            Name = "Tous les effets d'interface",
            Description = "Interrupteur général de Windows : coupé, il fait taire d'un coup les fondus des menus et des "
                + "info-bulles, les listes qui glissent, le défilement fluide et l'ombre du pointeur, sans changer leurs "
                + "réglages ci-dessous. Il commande aussi le suivi du survol et les dégradés des titres.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x103E,
            SetAction = 0x103F,
            // Le couper retirerait aussi des effets que la carte ne montre pas : « Réactif » coupe les effets un par un.
            InResponsivePreset = false,
        },
        new()
        {
            Key = ClientAreaAnimationKey,
            Name = "Effets d'animation",
            Description = "Animations à l'intérieur des fenêtres et des applis modernes : c'est l'interrupteur de "
                + "Paramètres › Accessibilité › Effets visuels. PCPerfSuite le suit aussi : ses volets "
                + "s'ouvriront sans animation.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1042,
            SetAction = 0x1043,
        },
        new()
        {
            Key = "window-minimize-animation",
            Name = "Animer la réduction et l'agrandissement des fenêtres",
            Description = "La fenêtre qui se réduit vers la barre des tâches ou qui s'agrandit.",
            Kind = AnimationSettingKind.SpiAnimationInfo,
            GetAction = 0x0048,
            SetAction = 0x0049,
        },
        new()
        {
            Key = "taskbar-animations",
            Name = "Animations de la barre des tâches",
            Description = "Boutons qui glissent et aperçus qui s'ouvrent en fondu dans la barre des tâches.",
            Kind = AnimationSettingKind.UserRegistry,
            RegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            RegistryValue = "TaskbarAnimations",
            ChangeArea = "TraySettings",
            ApplyNote = ExplorerNote,
        },
        new()
        {
            Key = "menu-animation",
            Name = "Fondu ou glissement des menus",
            Description = "Les menus apparaissent en fondu ou en glissant au lieu de s'afficher d'un coup.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1002,
            SetAction = 0x1003,
            DependsOnUiEffects = true,
        },
        new()
        {
            Key = "selection-fade",
            Name = "Fondu de l'élément de menu choisi",
            Description = "L'élément cliqué dans un menu s'efface en fondu après le clic.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1014,
            SetAction = 0x1015,
            DependsOnUiEffects = true,
        },
        new()
        {
            Key = "tooltip-animation",
            Name = "Fondu ou glissement des info-bulles",
            Description = "Les info-bulles apparaissent en fondu ou en glissant.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1016,
            SetAction = 0x1017,
            DependsOnUiEffects = true,
        },
        new()
        {
            Key = "combobox-animation",
            Name = "Ouverture glissante des listes déroulantes",
            Description = "Les listes déroulantes s'ouvrent en glissant.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1004,
            SetAction = 0x1005,
            DependsOnUiEffects = true,
        },
        new()
        {
            Key = "listbox-smooth-scrolling",
            Name = "Défilement fluide des listes",
            Description = "Les zones de liste défilent en douceur au lieu de sauter d'une ligne à l'autre.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1006,
            SetAction = 0x1007,
            DependsOnUiEffects = true,
        },
        new()
        {
            Key = "drag-full-windows",
            Name = "Contenu des fenêtres pendant le déplacement",
            Description = "Une fenêtre déplacée montre son contenu ; coupé, seul son cadre suit la souris.",
            Kind = AnimationSettingKind.SpiDragFullWindows,
            GetAction = 0x0026,
            SetAction = 0x0025,
        },
        new()
        {
            Key = "drop-shadow",
            Name = "Ombres sous les fenêtres",
            Description = "Ombre portée autour des fenêtres et des menus.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x1024,
            SetAction = 0x1025,
        },
        new()
        {
            Key = "cursor-shadow",
            Name = "Ombre sous le pointeur de la souris",
            Description = "Petite ombre portée sous le pointeur.",
            Kind = AnimationSettingKind.SpiBool,
            GetAction = 0x101A,
            SetAction = 0x101B,
            DependsOnUiEffects = true,
        },
        new()
        {
            Key = "transparency",
            Name = "Effets de transparence",
            Description = "Menu Démarrer, barre des tâches et fenêtres modernes translucides (Paramètres › "
                + "Personnalisation › Couleurs).",
            Kind = AnimationSettingKind.UserRegistry,
            RegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            RegistryValue = "EnableTransparency",
            ChangeArea = "ImmersiveColorSet",
            ApplyNote = ExplorerNote,
        },
    };

    public static AnimationSetting? Find(string key)
        => All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
