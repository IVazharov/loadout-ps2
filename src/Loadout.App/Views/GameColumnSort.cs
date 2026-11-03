using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Loadout.App.Controls;

namespace Loadout.App;

public static class GameColumnSort
{
    private static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(GameColumnSort));

    /// <summary>
    /// The property a column sorts by when it draws its cells with a template, which leaves it
    /// without a DisplayMemberBinding to take the property from.
    /// </summary>
    public static readonly DependencyProperty SortPathProperty = DependencyProperty.RegisterAttached(
        "SortPath",
        typeof(string),
        typeof(GameColumnSort));

    public static string? GetSortPath(GridViewColumn column) => (string?)column?.GetValue(SortPathProperty);

    public static void SetSortPath(GridViewColumn column, string? value) => column?.SetValue(SortPathProperty, value);

    /// <summary>The property a column sorts by: its SortPath, or else its DisplayMemberBinding's.</summary>
    public static string? PathOf(GridViewColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var path = GetSortPath(column) ?? (column.DisplayMemberBinding as Binding)?.Path.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public static void Enable(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.GetValue(EnabledProperty) is true)
        {
            return;
        }

        list.SetValue(EnabledProperty, true);
        list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnClick));
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list && e.OriginalSource is GridViewColumnHeader { Column: { } column })
        {
            Sort(list, column);
        }
    }

    /// <summary>
    /// Sorts the rows by the column, ascending, or descending when it is already sorted ascending,
    /// and marks the column's direction for the header's chevron.
    /// </summary>
    public static void Sort(ListView list, GridViewColumn column)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(column);
        var path = PathOf(column);
        if (path is null || list.ItemsSource is not IList source || source.Count == 0)
        {
            return;
        }

        var descending = TableColumns.GetSortDirection(column) == TableSort.Ascending;
        if (list.View is GridView view)
        {
            foreach (var other in view.Columns)
            {
                TableColumns.SetSortDirection(other, TableSort.None);
            }
        }

        TableColumns.SetSortDirection(column, descending ? TableSort.Descending : TableSort.Ascending);
        var rows = new object[source.Count];
        source.CopyTo(rows, 0);
        Array.Sort(rows, (left, right) =>
        {
            var compared = Compare(Read(left, path), Read(right, path));
            return descending ? -compared : compared;
        });
        source.Clear();
        foreach (var row in rows)
        {
            source.Add(row);
        }
    }

    // Values of one comparable type (numbers, dates) compare as themselves, so Size sorts by its
    // sectors and not by its text. Anything else compares as text, ignoring case.
    private static int Compare(object? left, object? right)
    {
        if (left is IComparable comparable && right is not null && left.GetType() == right.GetType())
        {
            return comparable.CompareTo(right);
        }

        return string.Compare(Text(left), Text(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;

    private static object? Read(object? item, string path) =>
        item?.GetType().GetProperty(path, BindingFlags.Instance | BindingFlags.Public)?.GetValue(item);
}
