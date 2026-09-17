using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using PCPerfSuite.App.Styles;
using PCPerfSuite.App.Utils;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Mini-graphique d'historique (comme les petites courbes du Gestionnaire des tâches, en plus fin).
/// Dessine un <see cref="SampleHistory"/> tenu par le ViewModel, plus une éventuelle seconde série en
/// simple ligne (lecture/écriture, réception/envoi). Échelle fixe de 0 à <see cref="Maximum"/>, ou calée
/// sur le pic visible avec <see cref="AutoScale"/> pour les valeurs sans borne naturelle (débits, watts, RPM).
/// Dessin manuel (pas de lib de charts externe) pour rester léger et fiable.
///
/// Un clic gauche épingle un repère qui affiche la valeur exacte du relevé visé et son ancienneté, façon
/// OCCT ; on le déplace en glissant, on l'enlève au clic droit ou avec Échap. Le repère mémorise le NUMÉRO
/// du relevé et non sa position : la courbe défile vers la gauche à chaque nouveau point, et un repère gardé
/// en pixels désignerait un autre relevé une seconde plus tard.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private SampleHistory? _attachedSeries;
    private SampleHistory? _attachedSecondarySeries;

    /// <summary>Numéro du relevé épinglé, -1 si aucun.</summary>
    private long _pinnedSequence = -1;
    private bool _isDragging;

    /// <summary>Fait vieillir l'étiquette du repère seconde par seconde, tant qu'un repère est posé.
    /// L'âge ne peut pas dépendre de la seule arrivée du point suivant : chaque groupe de capteurs a sa
    /// propre cadence, réglable jusqu'à 60 s, et le repère afficherait alors « à l'instant » pendant une
    /// minute entière avant de sauter d'un coup à « il y a 60 s ».</summary>
    private DispatcherTimer? _ageTimer;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(SampleHistory), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty SecondarySeriesProperty = DependencyProperty.Register(
        nameof(SecondarySeries), typeof(SampleHistory), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(ThemeColors.FrozenBrush(ThemeColors.Accent), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(ThemeColors.FrozenBrush(ThemeColors.WithAlpha(ThemeColors.Accent, 0x30)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryLineBrushProperty = DependencyProperty.Register(
        nameof(SecondaryLineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(ThemeColors.FrozenBrush(ThemeColors.Accent2), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumScaleProperty = DependencyProperty.Register(
        nameof(MinimumScale), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatterProperty = DependencyProperty.Register(
        nameof(ValueFormatter), typeof(Func<double, string>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SampleHistory? Series
    {
        get => (SampleHistory?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public SampleHistory? SecondarySeries
    {
        get => (SampleHistory?)GetValue(SecondarySeriesProperty);
        set => SetValue(SecondarySeriesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush SecondaryLineBrush
    {
        get => (Brush)GetValue(SecondaryLineBrushProperty);
        set => SetValue(SecondaryLineBrushProperty, value);
    }

    /// <summary>Haut de l'échelle fixe (100 : pourcentages, et températures en °C).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    /// <summary>Plancher de l'échelle automatique, pour qu'une valeur quasi nulle ne remplisse pas tout le graphique.</summary>
    public double MinimumScale
    {
        get => (double)GetValue(MinimumScaleProperty);
        set => SetValue(MinimumScaleProperty, value);
    }

    /// <summary>Met en forme la valeur affichée par le repère (unité comprise). Fournie par le ViewModel, qui
    /// seul connaît la métrique : le contrôle, lui, ne manipule que des double. Null : format numérique neutre.</summary>
    public Func<double, string>? ValueFormatter
    {
        get => (Func<double, string>?)GetValue(ValueFormatterProperty);
        set => SetValue(ValueFormatterProperty, value);
    }

    // Pinceaux du repère figés une fois pour toutes : OnRender est rappelé à chaque relevé, sur toutes les
    // tuiles affichées, et allouer un pinceau par passage coûterait pour rien.
    private static readonly Pen MarkerLinePen = FrozenPen(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen MarkerDotPen = FrozenPen(Color.FromArgb(0xCC, 0x0B, 0x0D, 0x14), 1.5);
    private static readonly Pen MarkerBoxPen = FrozenPen(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF), 1);
    private static readonly SolidColorBrush MarkerBoxBrush = FrozenBrush(Color.FromArgb(0xF2, 0x15, 0x19, 0x25));
    private static readonly SolidColorBrush MarkerTextBrush = FrozenBrush(ThemeColors.TextPrimary);

    private static readonly Typeface MarkerTypeface = new("Segoe UI Variable, Segoe UI");

    private const double MarkerFontSize = 11;
    private const double MarkerPadding = 5;
    private const double MarkerSwatch = 5;
    private const double MarkerSwatchGap = 4;

    static Sparkline()
    {
        // Sans focus clavier, la touche Échap n'arriverait jamais jusqu'ici : seul l'élément focalisé
        // reçoit les touches. La doc de UIElement.Focusable recommande de le faire en surchargeant la
        // métadonnée plutôt qu'en l'affectant dans le constructeur d'instance.
        FocusableProperty.OverrideMetadata(typeof(Sparkline), new UIPropertyMetadata(true));
    }

    public Sparkline()
    {
        // Abonnement limité à la présence dans l'arbre visuel : l'historique vit dans le ViewModel, plus
        // longtemps que le graphique d'une ligne de disque ou de ventilateur, et ne doit pas le retenir.
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();

        ClipToBounds = true;
        Cursor = Cursors.Cross;

        // Les pointillés de focus de WPF se dessineraient par-dessus la courbe : le repère dit déjà quel
        // graphique est actif.
        FocusVisualStyle = null;

        // Focalisable au clic, mais hors de l'ordre de tabulation. Tout FrameworkElement focalisable est un
        // arrêt de tabulation par défaut : les douze graphiques de la page Monitoring le seraient devenus,
        // sans aucun repère visuel puisque FocusVisualStyle est null, et la touche Échap aurait agi sur une
        // tuile que l'utilisateur ne voit pas.
        KeyboardNavigation.SetIsTabStop(this, false);

        // Prendre le focus au clic demande au ScrollViewer parent de ramener la tuile dans la vue, ce qui
        // ferait sauter la page sous le curseur au moment même où on mesure un point. On ne refuse la demande
        // que pendant le geste souris : la refuser toujours empêcherait aussi le ScrollViewer de suivre le
        // focus clavier, et la page ne défilerait plus du tout.
        RequestBringIntoView += (_, e) => e.Handled = Mouse.LeftButton == MouseButtonState.Pressed;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        if (sparkline.IsLoaded) sparkline.Attach();
    }

    private void Attach()
    {
        Detach();
        _attachedSeries = Series;
        _attachedSecondarySeries = SecondarySeries;
        if (_attachedSeries is not null) _attachedSeries.Changed += InvalidateVisual;
        if (_attachedSecondarySeries is not null) _attachedSecondarySeries.Changed += InvalidateVisual;
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_attachedSeries is not null) _attachedSeries.Changed -= InvalidateVisual;
        if (_attachedSecondarySeries is not null) _attachedSecondarySeries.Changed -= InvalidateVisual;
        _attachedSeries = null;
        _attachedSecondarySeries = null;

        // Un numéro de relevé n'a de sens que pour la série qui l'a émis : changer de série (conteneur
        // réutilisé, tuile remplacée) doit repartir sans repère plutôt qu'en désigner un au hasard.
        _pinnedSequence = -1;
        StopAgeTimer();
    }

    // ----- Géométrie, partagée par le dessin et par le clic -----

    /// <summary>Pas horizontal entre deux relevés : la fenêtre glissante occupe toute la largeur.</summary>
    private static double StepOf(SampleHistory series, double width) => width / Math.Max(1, series.Capacity - 1);

    /// <summary>Abscisse du plus ancien relevé gardé (le plus récent est calé sur le bord droit). Le clic
    /// et le tracé passent par la même formule, sinon le repère se poserait à côté de sa courbe.</summary>
    private static double StartOf(SampleHistory series, double width, double stepX) => width - (series.Count - 1) * stepX;

    // ----- Rendu -----

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Rectangle transparent avant tout le reste : WPF teste le clic contre la géométrie réellement
        // dessinée, et une géométrie remplie — même en Transparent — est solide, là où l'absence de fond est
        // creuse. Sans lui, seule la zone remplie sous la courbe serait cliquable, jamais le vide au-dessus.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        SampleHistory? series = Series;
        if (series is null) return;

        SampleHistory? secondary = SecondarySeries;
        // 15 % de marge au-dessus du pic pour que la courbe ne colle pas au bord supérieur.
        double top = AutoScale
            ? Math.Max(MinimumScale, Math.Max(series.Max(), secondary?.Max() ?? 0) * 1.15)
            : Maximum;
        if (top <= 0) return;

        double stepX = StepOf(series, w);
        if (secondary is not null) DrawSeries(dc, secondary, null, new Pen(SecondaryLineBrush, 1.5), w, h, stepX, top);
        DrawSeries(dc, series, FillBrush, new Pen(LineBrush, 1.75), w, h, stepX, top);

        DrawMarker(dc, series, secondary, w, h, stepX, top);
    }

    /// <summary>Trace une série alignée à droite (le relevé le plus récent sur le bord droit), en coupant
    /// la courbe là où la valeur manque plutôt que de la faire plonger à zéro.</summary>
    private static void DrawSeries(DrawingContext dc, SampleHistory series, Brush? fill, Pen pen,
                                   double w, double h, double stepX, double top)
    {
        int n = series.Count;
        if (n < 2) return;

        double startX = StartOf(series, w, stepX);
        Point At(int i) => new(startX + i * stepX, h - Math.Clamp(series[i] / top, 0, 1) * h);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext lineCtx = line.Open())
        using (StreamGeometryContext areaCtx = area.Open())
        {
            bool inSegment = false;
            for (int i = 0; i <= n; i++)
            {
                bool present = i < n && !double.IsNaN(series[i]);
                if (present)
                {
                    Point p = At(i);
                    if (!inSegment)
                    {
                        lineCtx.BeginFigure(p, isFilled: false, isClosed: false);
                        areaCtx.BeginFigure(new Point(p.X, h), isFilled: true, isClosed: true);
                        inSegment = true;
                    }
                    else
                    {
                        lineCtx.LineTo(p, true, false);
                    }
                    areaCtx.LineTo(p, true, false);
                }
                else if (inSegment)
                {
                    areaCtx.LineTo(new Point(At(i - 1).X, h), true, false);
                    inSegment = false;
                }
            }
        }
        line.Freeze();
        area.Freeze();

        if (fill is not null) dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Repère épinglé : trait vertical, point sur la courbe, et étiquette donnant la valeur exacte
    /// du relevé et son ancienneté. Dessiné après les courbes pour rester lisible par-dessus.</summary>
    private void DrawMarker(DrawingContext dc, SampleHistory series, SampleHistory? secondary,
                            double w, double h, double stepX, double top)
    {
        if (_pinnedSequence < 0) return;

        int i = series.IndexOf(_pinnedSequence);
        if (i < 0)
        {
            // Le relevé est sorti de la fenêtre glissante : sa valeur n'existe plus nulle part, le repère
            // s'efface plutôt que d'afficher un chiffre figé qui ne correspondrait plus à rien.
            _pinnedSequence = -1;
            StopAgeTimer();
            return;
        }

        double value = series[i];
        if (double.IsNaN(value))
        {
            _pinnedSequence = -1;
            StopAgeTimer();
            return;
        }

        double x = StartOf(series, w, stepX) + i * stepX;
        double y = h - Math.Clamp(value / top, 0, 1) * h;

        // Trait posé sur le demi-pixel : à coordonnée entière, un trait d'un pixel est réparti sur deux
        // colonnes et ressort gris et flou. Ramené dans la boîte au passage : le relevé le plus récent est
        // calé pile sur le bord droit, donc son trait tombait juste en dehors et ClipToBounds l'effaçait —
        // on voyait l'étiquette et sa valeur, mais aucun trait, alors que deux pixels plus à gauche si.
        double lineX = Math.Clamp(Math.Round(x), 0, Math.Max(0, w - 1)) + 0.5;
        dc.DrawLine(MarkerLinePen, new Point(lineX, 0), new Point(lineX, h));

        // Le point, lui, reste à sa vraie place : il désigne le relevé sur la courbe, le ramener dans la
        // boîte le décrocherait du trait et mentirait sur la position. Au bord, il est donc coupé en deux
        // par ClipToBounds — c'est exact, et le trait suffit à montrer où il est.
        dc.DrawEllipse(LineBrush, MarkerDotPen, new Point(x, y), 3, 3);

        var lines = new List<(Brush Swatch, FormattedText Text)>(2)
        {
            (LineBrush, BuildText($"{Format(value)} · {FormatAge(DateTime.Now - series.TimeAt(i))}", w)),
        };

        if (secondary is not null)
        {
            // La seconde série est retrouvée par NUMÉRO de relevé, pas par indice : si elle n'avance pas à la
            // même cadence, aucun de ses relevés ne porte ce numéro et on n'affiche rien — plutôt que de faire
            // passer la valeur voisine pour celle du point visé.
            int j = secondary.IndexOf(_pinnedSequence);
            if (j >= 0 && !double.IsNaN(secondary[j]))
            {
                lines.Add((SecondaryLineBrush, BuildText(Format(secondary[j]), w)));
            }
        }

        double textWidth = 0;
        double textHeight = 0;
        foreach ((_, FormattedText text) in lines)
        {
            textWidth = Math.Max(textWidth, text.Width);
            textHeight += text.Height;
        }

        double boxWidth = MarkerPadding + MarkerSwatch + MarkerSwatchGap + textWidth + MarkerPadding;
        double boxHeight = MarkerPadding + textHeight + MarkerPadding;

        // Ramenée dans la boîte du graphique, et posée dans la moitié opposée au point pour ne jamais le masquer.
        double boxX = Math.Clamp(x - boxWidth / 2, 2, Math.Max(2, w - boxWidth - 2));
        double boxY = y > h / 2 ? 2 : Math.Max(2, h - boxHeight - 2);

        // Fond opaque plutôt qu'une ombre portée : dans ce projet, tout Effect force un rendu bitmap
        // intermédiaire qui ferait perdre le ClearType du texte (voir Theme.xaml).
        dc.DrawRoundedRectangle(MarkerBoxBrush, MarkerBoxPen, new Rect(boxX, boxY, boxWidth, boxHeight), 5, 5);

        double lineY = boxY + MarkerPadding;
        foreach ((Brush swatch, FormattedText text) in lines)
        {
            dc.DrawRectangle(swatch, null,
                new Rect(boxX + MarkerPadding, lineY + (text.Height - MarkerSwatch) / 2, MarkerSwatch, MarkerSwatch));
            dc.DrawText(text, new Point(boxX + MarkerPadding + MarkerSwatch + MarkerSwatchGap, lineY));
            lineY += text.Height;
        }
    }

    private string Format(double value)
        => ValueFormatter is { } formatter ? formatter(value) : value.ToString("0.##", CultureInfo.CurrentCulture);

    private FormattedText BuildText(string value, double w) => new(
        value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, MarkerTypeface, MarkerFontSize,
        MarkerTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
    {
        MaxTextWidth = Math.Max(20, w - 2 * MarkerPadding - MarkerSwatch - MarkerSwatchGap - 4),
        MaxLineCount = 1,
        Trimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>Ancienneté du relevé épinglé, rafraîchie chaque seconde par <see cref="_ageTimer"/> tant
    /// qu'un repère est posé — et non à l'arrivée du point suivant, qui peut se faire attendre une minute
    /// entière sur un groupe de capteurs réglé à sa cadence la plus lente.</summary>
    private static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 1.5 => "à l'instant",
        < 60 => $"il y a {age.TotalSeconds:0} s",
        < 3600 => $"il y a {age.TotalMinutes:0} min",
        _ => $"à {DateTime.Now.Add(-age):HH:mm}",
    };

    // ----- Interaction -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!TryPin(e.GetPosition(this))) return;

        Focus();
        // CaptureMouse peut échouer si un autre élément tient déjà la capture : on pose quand même le
        // repère, seul le glisser est alors indisponible.
        _isDragging = CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging) TryPin(e.GetPosition(this));
    }

    /// <summary>Le repère reste en place au relâchement : c'est tout l'intérêt d'épingler une valeur.</summary>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging) return;

        _isDragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ClearPin();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;

        ClearPin();
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);

        // Un seul repère à la fois dans l'app : cliquer ailleurs enlève celui-ci. Mais passer dans une autre
        // application (NewFocus null) ne doit rien effacer, sinon on perdrait sa mesure en allant simplement
        // regarder autre chose.
        if (e.NewFocus is not null) ClearPin();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isDragging = false;
    }

    /// <summary>Épingle le relevé le plus proche de l'abscisse cliquée. Retourne false quand il n'y a rien à
    /// épingler (moins de deux points, ou série entièrement absente).</summary>
    private bool TryPin(Point position)
    {
        SampleHistory? series = Series;
        double w = ActualWidth;
        if (series is null || w <= 0 || series.Count < 2) return false;

        double stepX = StepOf(series, w);
        int index = (int)Math.Round((position.X - StartOf(series, w, stepX)) / stepX);

        // Un clic à gauche du premier point se rabat dessus : une zone morte donnerait l'impression d'un clic
        // qui ne marche pas.
        index = Math.Clamp(index, 0, series.Count - 1);
        index = NearestPresent(series, index);
        if (index < 0) return false;

        long sequence = series.SequenceAt(index);
        StartAgeTimer();
        if (sequence == _pinnedSequence) return true;

        _pinnedSequence = sequence;
        InvalidateVisual();
        return true;
    }

    /// <summary>Relevé présent le plus proche de <paramref name="index"/>, en s'écartant des deux côtés (le
    /// plus récent d'abord) : cliquer dans un trou de la courbe épingle un vrai point plutôt que NaN.</summary>
    private static int NearestPresent(SampleHistory series, int index)
    {
        if (!double.IsNaN(series[index])) return index;

        for (int distance = 1; distance < series.Count; distance++)
        {
            if (index + distance < series.Count && !double.IsNaN(series[index + distance])) return index + distance;
            if (index - distance >= 0 && !double.IsNaN(series[index - distance])) return index - distance;
        }

        return -1;
    }

    private void ClearPin()
    {
        if (_pinnedSequence < 0) return;

        _pinnedSequence = -1;
        StopAgeTimer();
        InvalidateVisual();
    }

    /// <summary>Le timer n'existe que le temps d'un repère : aucune tuile ne bat inutilement, et une page
    /// Monitoring sans repère posé ne coûte rien de plus qu'avant.</summary>
    private void StartAgeTimer()
    {
        _ageTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => InvalidateVisual(), Dispatcher);
        _ageTimer.Start();
    }

    private void StopAgeTimer() => _ageTimer?.Stop();
}
