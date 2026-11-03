using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Loadout.App.Controls;

/// <summary>The opposite of BooleanToVisibilityConverter: for what shows instead, such as an empty state.</summary>
public sealed class CollapsedWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
