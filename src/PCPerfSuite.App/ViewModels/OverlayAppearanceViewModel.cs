using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Metrics;
using PCPerfSuite.App.Overlay;
using PCPerfSuite.Core.Overlay;
using PCPerfSuite.Core.PowerSettings;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Une pastille de couleur du sélecteur.</summary>
public sealed partial class OverlayColorChoiceViewModel : ObservableObject
{
    private readonly OverlayColorSlotViewModel _slot;

    public string ColorHex { get; }
    public Brush Brush { get; }

    [ObservableProperty] private bool isSelected;

    public OverlayColorChoiceViewModel(string colorHex, OverlayColorSlotViewModel slot)
    {
        ColorHex = colorHex;
        Brush = OverlayPalette.ToBrush(colorHex);
        _slot = slot;
    }

    [RelayCommand]
    private void Select() => _slot.ColorHex = ColorHex;
}

/// <summary>Une case de couleur du réglage des couleurs (nom et valeurs de chaque catégorie, plus la couleur commune
/// des valeurs), avec sa palette.</summary>
public sealed partial class OverlayColorSlotViewModel : ObservableObject
{
    private readonly Action _onChanged;

    /// <summary>Vrai pendant que la case suit sa couleur par défaut (<see cref="FollowDefault"/>) : ce n'est pas un
    /// choix de l'utilisateur, et celui qui a changé la couleur par défaut se charge déjà du rendu.</summary>
    private bool _following;

    /// <summary>Clé de persistance (clé de catégorie du catalogue, ou "value" pour les valeurs).</summary>
    public string Key { get; }

    public string Name { get; }
    public string DefaultColorHex { get; private set; }
    public IReadOnlyList<OverlayColorChoiceViewModel> Choices { get; }

    [ObservableProperty] private string colorHex;

    public Brush Brush { get; private set; }

    public OverlayColorSlotViewModel(string key, string name, string defaultColorHex, string colorHex, Action onChanged)
    {
        Key = key;
        Name = name;
        DefaultColorHex = defaultColorHex;
        _onChanged = onChanged;
        this.colorHex = colorHex;
        Brush = OverlayPalette.ToBrush(colorHex);
        Choices = OverlayPalette.Colors.Select(c => new OverlayColorChoiceViewModel(c, this)).ToList();
        SyncChoices();
    }

    public void Reset() => ColorHex = DefaultColorHex;

    /// <summary>Vrai tant que l'utilisateur n'a pas choisi une autre couleur que celle par défaut.</summary>
    public bool IsDefault => OverlayColorDefaults.SameColor(ColorHex, DefaultColorHex);

    /// <summary>Change la couleur par défaut. Une case restée sur l'ancienne la suit ; une couleur choisie reste.</summary>
    public void FollowDefault(string colorHex)
    {
        bool wasDefault = IsDefault;
        DefaultColorHex = colorHex;
        if (!wasDefault) return;

        _following = true;
        try
        {
            ColorHex = colorHex;
        }
        finally
        {
            _following = false;
        }
    }

    partial void OnColorHexChanged(string value)
    {
        Brush = OverlayPalette.ToBrush(value);
        OnPropertyChanged(nameof(Brush));
        SyncChoices();
        if (!_following) _onChanged();
    }

    private void SyncChoices()
    {
        foreach (OverlayColorChoiceViewModel choice in Choices)
        {
            choice.IsSelected = string.Equals(choice.ColorHex, ColorHex, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Une catégorie dans le réglage des couleurs : la couleur de son nom en tête de ligne (CPU, GPU…) et celle
/// de ses valeurs.</summary>
public sealed class OverlayCategoryColorsViewModel
{
    public OverlayCategoryColorsViewModel(OverlayColorSlotViewModel label, OverlayColorSlotViewModel value)
    {
        Label = label;
        Value = value;
    }

    public string Name => Label.Name;
    public OverlayColorSlotViewModel Label { get; }
    public OverlayColorSlotViewModel Value { get; }
}

/// <summary>
/// Apparence de l'overlay : police, taille, couleurs et position. Partagé par l'aperçu de l'onglet
/// Overlay, la fenêtre d'overlay et (pour ce que RTSS sait interpréter) l'OSD de RTSS.
/// </summary>
public sealed partial class OverlayAppearanceViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action _onLayoutChanged;

    /// <summary>Police de repli si celle enregistrée n'existe pas (ou plus) sur la machine.</summary>
    private const string DefaultFontName = "Consolas";

    public IReadOnlyList<FontFamily> FontFamilies { get; }

    [ObservableProperty] private FontFamily selectedFont;
    [ObservableProperty] private double fontSize;
    [ObservableProperty] private int rtssSizePercent;
    [ObservableProperty] private int valueSpacing;
    [ObservableProperty] private int separatorSpacing;
    [ObservableProperty] private bool useCategoryColors;
    [ObservableProperty] private bool sendColorsToRtss;
    [ObservableProperty] private double backgroundOpacity;
    [ObservableProperty] private OverlayAnchor anchor;
    [ObservableProperty] private int marginX;
    [ObservableProperty] private int marginY;

    /// <summary>Couleur commune des valeurs (chiffres) : celle de toutes les valeurs quand les couleurs par catégorie
    /// sont désactivées, et sinon celle que suivent les catégories dont on n'a pas changé la couleur des valeurs.</summary>
    public OverlayColorSlotViewModel ValueColor { get; }

    /// <summary>Une entrée par catégorie du catalogue : c'est ce qui colore "CPU", "RAM", "NET"… en
    /// tête de ligne.</summary>
    public IReadOnlyList<OverlayColorSlotViewModel> CategoryColors { get; }

    /// <summary>Couleur des valeurs de chaque catégorie, dans l'ordre de <see cref="CategoryColors"/>. Par défaut,
    /// la couleur commune (<see cref="ValueColor"/>), qu'elle suit tant qu'on ne l'a pas changée.</summary>
    public IReadOnlyList<OverlayColorSlotViewModel> CategoryValueColors { get; }

    /// <summary>Les deux couleurs de chaque catégorie, une ligne par catégorie dans l'onglet.</summary>
    public IReadOnlyList<OverlayCategoryColorsViewModel> CategoryColorRows { get; }

    public OverlayAppearanceViewModel(OverlayAppearanceSettings settings, Action onChanged, Action onLayoutChanged)
    {
        _onChanged = onChanged;
        _onLayoutChanged = onLayoutChanged;

        FontFamilies = Fonts.SystemFontFamilies
            .OrderBy(f => f.Source, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        selectedFont = ResolveFont(settings.FontFamily);

        fontSize = Math.Clamp(settings.FontSize, 10, 48);
        rtssSizePercent = Math.Clamp(settings.RtssSizePercent, 50, 200);
        valueSpacing = Math.Clamp(settings.ValueSpacing, OverlaySpacing.MinSpaces, OverlaySpacing.MaxSpaces);
        separatorSpacing = Math.Clamp(settings.SeparatorSpacing, OverlaySpacing.MinSpaces, OverlaySpacing.MaxSpaces);
        useCategoryColors = settings.UseCategoryColors;
        sendColorsToRtss = settings.SendColorsToRtss;
        backgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0, 1);
        anchor = settings.Anchor;
        marginX = Math.Clamp(settings.MarginX, 0, 600);
        marginY = Math.Clamp(settings.MarginY, 0, 600);

        Dictionary<string, string> saved = settings.CategoryColors ?? new Dictionary<string, string>();
        Dictionary<string, string> savedValues = settings.CategoryValueColors ?? new Dictionary<string, string>();

        ValueColor = new OverlayColorSlotViewModel("value", "Valeurs", "#FFFFFF", settings.ValueColor, OnValueColorChanged);
        CategoryColors = MetricCatalog.Categories
            .Select(c => new OverlayColorSlotViewModel(
                c.Key,
                c.Name,
                c.OverlayColor,
                OverlayColorDefaults.Resolve(c, saved.GetValueOrDefault(c.Key)),
                onChanged))
            .ToList();
        CategoryValueColors = MetricCatalog.Categories
            .Select(c => new OverlayColorSlotViewModel(
                c.Key,
                c.Name,
                ValueColor.ColorHex,
                savedValues.GetValueOrDefault(c.Key) is { } hex && !string.IsNullOrWhiteSpace(hex) ? hex : ValueColor.ColorHex,
                onChanged))
            .ToList();
        CategoryColorRows = CategoryColors.Zip(CategoryValueColors, (label, value) => new OverlayCategoryColorsViewModel(label, value)).ToList();
    }

    /// <summary>La couleur commune des valeurs a changé : les catégories dont les valeurs la suivaient prennent la
    /// nouvelle, puis un seul rendu pour le tout.</summary>
    private void OnValueColorChanged()
    {
        foreach (OverlayColorSlotViewModel slot in CategoryValueColors) slot.FollowDefault(ValueColor.ColorHex);
        _onChanged();
    }

    /// <summary>Couleurs à appliquer aux lignes : celles de chaque catégorie (nom et valeurs) quand l'option est
    /// activée, sinon la couleur commune des valeurs pour tout le texte.</summary>
    public OverlayColorScheme BuildColorScheme()
    {
        Dictionary<string, string> labels = CategoryColors.ToDictionary(slot => slot.Key, slot => slot.ColorHex);
        Dictionary<string, string> values = CategoryValueColors.ToDictionary(slot => slot.Key, slot => slot.ColorHex);

        string common = ValueColor.ColorHex;
        bool colored = UseCategoryColors;

        return new OverlayColorScheme
        {
            CategoryColor = category => colored && labels.TryGetValue(category.Key, out string? hex) ? hex : common,
            ValueColor = category => colored && values.TryGetValue(category.Key, out string? hex) ? hex : common,
        };
    }

    /// <summary>Espacements à appliquer aux lignes, en nombre d'espaces.</summary>
    public OverlaySpacing BuildSpacing() => new(ValueSpacing, SeparatorSpacing);

    public void WriteTo(OverlayAppearanceSettings settings)
    {
        settings.FontFamily = SelectedFont.Source;
        settings.FontSize = FontSize;
        settings.RtssSizePercent = RtssSizePercent;
        settings.ValueSpacing = ValueSpacing;
        settings.SeparatorSpacing = SeparatorSpacing;
        settings.UseCategoryColors = UseCategoryColors;
        settings.SendColorsToRtss = SendColorsToRtss;
        settings.BackgroundOpacity = BackgroundOpacity;
        settings.Anchor = Anchor;
        settings.MarginX = MarginX;
        settings.MarginY = MarginY;
        settings.ValueColor = ValueColor.ColorHex;

        // Seules les couleurs que l'utilisateur a changées sont enregistrées : les autres suivent leur défaut — le
        // catalogue pour les noms, qui peut évoluer d'une version à l'autre (voir OverlayColorDefaults), la couleur
        // commune pour les valeurs.
        settings.CategoryColors = CategoryColors
            .Where(slot => !slot.IsDefault)
            .ToDictionary(slot => slot.Key, slot => slot.ColorHex);
        settings.CategoryValueColors = CategoryValueColors
            .Where(slot => !slot.IsDefault)
            .ToDictionary(slot => slot.Key, slot => slot.ColorHex);
    }

    [RelayCommand]
    private void ResetColors()
    {
        ValueColor.Reset();
        foreach (OverlayColorSlotViewModel slot in CategoryColors) slot.Reset();
        foreach (OverlayColorSlotViewModel slot in CategoryValueColors) slot.Reset();
    }

    private FontFamily ResolveFont(string? name)
    {
        FontFamily? match = FontFamilies.FirstOrDefault(
            f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase));

        return match
               ?? FontFamilies.FirstOrDefault(f => string.Equals(f.Source, DefaultFontName, StringComparison.OrdinalIgnoreCase))
               ?? FontFamilies.FirstOrDefault()
               ?? new FontFamily(DefaultFontName);
    }

    partial void OnSelectedFontChanged(FontFamily value) => Changed();
    partial void OnFontSizeChanged(double value) => Changed();
    partial void OnRtssSizePercentChanged(int value) => _onChanged();

    // Les espacements élargissent ou resserrent les lignes : la fenêtre ancrée à droite ou en bas doit être replacée.
    partial void OnValueSpacingChanged(int value) => Changed();
    partial void OnSeparatorSpacingChanged(int value) => Changed();
    partial void OnUseCategoryColorsChanged(bool value) => _onChanged();
    partial void OnSendColorsToRtssChanged(bool value) => _onChanged();
    partial void OnBackgroundOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(BackgroundOpacityPercent));
        Changed();
    }

    /// <summary><see cref="BackgroundOpacity"/> (0 à 1) telle qu'on la saisit : de 0 à 100 %. Le fichier de réglages
    /// et la fenêtre d'overlay gardent la fraction ; seul le champ de saisie parle en pourcentage.</summary>
    public double BackgroundOpacityPercent
    {
        get => Math.Round(BackgroundOpacity * 100);
        set => BackgroundOpacity = Math.Clamp(value, 0, 100) / 100;
    }
    partial void OnAnchorChanged(OverlayAnchor value) => Changed();
    partial void OnMarginXChanged(int value) => Changed();
    partial void OnMarginYChanged(int value) => Changed();

    /// <summary>Réglages qui changent aussi la géométrie : la fenêtre d'overlay doit être replacée.</summary>
    private void Changed()
    {
        _onChanged();
        _onLayoutChanged();
    }
}
