using System.Windows;

namespace Loadout.App.Controls;

/// <summary>A placeholder for a TextBox, shown by its template in AppDisabled while the box is empty and unfocused.</summary>
public static class TextHint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(TextHint),
        new FrameworkPropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
