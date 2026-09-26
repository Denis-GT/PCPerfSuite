using System.Windows;

namespace PCPerfSuite.App.Controls;

/// <summary>
/// Demande à un contrôle d'attirer l'œil : son fond passe progressivement à l'orange puis revient, en boucle.
///
/// Cette classe ne porte que l'interrupteur, pour que le ViewModel pilote le clignotement par une simple liaison
/// (<c>controls:Attention.IsBlinking="{Binding ...}"</c>). Le rendu est dans le modèle du contrôle, dans
/// Styles/Theme.xaml : NavButton (le bouton Paramètres) et PillItem (les onglets en pastilles) le prennent en charge.
/// Un contrôle dont le modèle ne le prévoit pas ignore simplement la propriété.
/// </summary>
public static class Attention
{
    public static readonly DependencyProperty IsBlinkingProperty =
        DependencyProperty.RegisterAttached("IsBlinking", typeof(bool), typeof(Attention), new PropertyMetadata(false));

    public static bool GetIsBlinking(DependencyObject element) => (bool)element.GetValue(IsBlinkingProperty);

    public static void SetIsBlinking(DependencyObject element, bool value) => element.SetValue(IsBlinkingProperty, value);
}
