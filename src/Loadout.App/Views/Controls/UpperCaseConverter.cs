using System.Globalization;
using System.Windows.Data;

namespace Loadout.App.Controls;

/// <summary>Draws a table header in capitals, while its Header (and what Narrator reads) stays in sentence case.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => TableColumns.HeaderText(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
