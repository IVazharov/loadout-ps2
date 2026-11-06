using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Loadout.Core;
using Loadout.Persistence;

namespace Loadout.App.Controls;

/// <summary>A column's sort, which its header shows as a chevron.</summary>
public enum TableSort
{
    None,
    Ascending,
    Descending,
}

/// <summary>
/// Sizes a GridView's columns to their data, not to the rows on screen: each column fits its widest
/// value between a minimum and an auto maximum, the fill column takes the rest of the viewport, and
/// a column the user sized keeps its width. The order and the user's widths are kept between runs
/// under <c>table.&lt;Key&gt;</c> in <see cref="Store"/>.
/// </summary>
public static class TableColumns
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(TableColumns), new PropertyMetadata(null, OnKeyChanged));

    /// <summary>The id of the column that widens to fill the viewport.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(string), typeof(TableColumns));

    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(TableColumns));

    /// <summary>The least width; the header's own fitted width (at least 48) when that is wider.</summary>
    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.RegisterAttached(
        "MinWidth", typeof(double), typeof(TableColumns), new PropertyMetadata(48.0));

    /// <summary>The widest a fit makes the column; the user can drag it wider.</summary>
    public static readonly DependencyProperty MaxAutoWidthProperty = DependencyProperty.RegisterAttached(
        "MaxAutoWidth", typeof(double), typeof(TableColumns), new PropertyMetadata(400.0));

    /// <summary>The property to measure for a template column, which has no DisplayMemberBinding.</summary>
    public static readonly DependencyProperty MeasurePathProperty = DependencyProperty.RegisterAttached(
        "MeasurePath", typeof(string), typeof(TableColumns));

    /// <summary>
    /// Room after a right-aligned value, which the cell template's right margin takes, so the next
    /// column's left-aligned text doesn't run into it. A fit counts it in the width.
    /// </summary>
    public static readonly DependencyProperty TrailingGapProperty = DependencyProperty.RegisterAttached(
        "TrailingGap", typeof(double), typeof(TableColumns), new PropertyMetadata(0.0));

    public static double GetTrailingGap(GridViewColumn column) => (double)column.GetValue(TrailingGapProperty);

    public static void SetTrailingGap(GridViewColumn column, double value) => column.SetValue(TrailingGapProperty, value);

    /// <summary>A bool property of the row; where it is true the cell also shows <see cref="ExtraTextProperty"/>.</summary>
    public static readonly DependencyProperty ExtraWhenProperty = DependencyProperty.RegisterAttached(
        "ExtraWhen", typeof(string), typeof(TableColumns));

    public static readonly DependencyProperty ExtraTextProperty = DependencyProperty.RegisterAttached(
        "ExtraText", typeof(string), typeof(TableColumns));

    /// <summary>The cells draw their value as a chip: the label style's capitals, plus <see cref="ChipPadding"/>.</summary>
    public static readonly DependencyProperty ChipProperty = DependencyProperty.RegisterAttached(
        "Chip", typeof(bool), typeof(TableColumns));

    /// <summary>A chip's padding and border, both sides.</summary>
    public const double ChipPadding = 16;

    /// <summary>The column keeps the Width set in XAML and can't be resized.</summary>
    public static readonly DependencyProperty FixedProperty = DependencyProperty.RegisterAttached(
        "Fixed", typeof(bool), typeof(TableColumns));

    public static readonly DependencyProperty SortDirectionProperty = DependencyProperty.RegisterAttached(
        "SortDirection", typeof(TableSort), typeof(TableColumns), new PropertyMetadata(TableSort.None));

    private static readonly DependencyProperty TableProperty = DependencyProperty.RegisterAttached(
        "Table", typeof(Table), typeof(TableColumns));

    private static readonly DependencyPropertyDescriptor WidthDescriptor =
        DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));

    /// <summary>Where the layouts are kept; null keeps nothing.</summary>
    public static UiStateStore? Store { get; set; }

    public static string? GetKey(ListView list) => (string?)list.GetValue(KeyProperty);

    public static void SetKey(ListView list, string? value) => list.SetValue(KeyProperty, value);

    public static string? GetFill(ListView list) => (string?)list.GetValue(FillProperty);

    public static void SetFill(ListView list, string? value) => list.SetValue(FillProperty, value);

    public static string? GetId(GridViewColumn column) => (string?)column.GetValue(IdProperty);

    public static void SetId(GridViewColumn column, string? value) => column.SetValue(IdProperty, value);

    public static double GetMinWidth(GridViewColumn column) => (double)column.GetValue(MinWidthProperty);

    public static void SetMinWidth(GridViewColumn column, double value) => column.SetValue(MinWidthProperty, value);

    public static double GetMaxAutoWidth(GridViewColumn column) => (double)column.GetValue(MaxAutoWidthProperty);

    public static void SetMaxAutoWidth(GridViewColumn column, double value) => column.SetValue(MaxAutoWidthProperty, value);

    public static string? GetMeasurePath(GridViewColumn column) => (string?)column.GetValue(MeasurePathProperty);

    public static void SetMeasurePath(GridViewColumn column, string? value) => column.SetValue(MeasurePathProperty, value);

    public static string? GetExtraWhen(GridViewColumn column) => (string?)column.GetValue(ExtraWhenProperty);

    public static void SetExtraWhen(GridViewColumn column, string? value) => column.SetValue(ExtraWhenProperty, value);

    public static string? GetExtraText(GridViewColumn column) => (string?)column.GetValue(ExtraTextProperty);

    public static void SetExtraText(GridViewColumn column, string? value) => column.SetValue(ExtraTextProperty, value);

    public static bool GetChip(GridViewColumn column) => (bool)column.GetValue(ChipProperty);

    public static void SetChip(GridViewColumn column, bool value) => column.SetValue(ChipProperty, value);

    public static bool GetFixed(GridViewColumn column) => (bool)column.GetValue(FixedProperty);

    public static void SetFixed(GridViewColumn column, bool value) => column.SetValue(FixedProperty, value);

    public static TableSort GetSortDirection(GridViewColumn column) => (TableSort)column.GetValue(SortDirectionProperty);

    public static void SetSortDirection(GridViewColumn column, TableSort value) => column.SetValue(SortDirectionProperty, value);

    /// <summary>The header's text as drawn: the label style's capitals.</summary>
    public static string HeaderText(object? header) => (header as string ?? string.Empty).ToUpper(CultureInfo.CurrentCulture);

    private static void OnKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ListView list && list.GetValue(TableProperty) is null && e.NewValue is string)
        {
            list.SetValue(TableProperty, new Table(list));
        }
    }

    private sealed class ColumnState
    {
        public double Fitted { get; set; }

        public double Min { get; set; }

        public bool UserSized { get; set; }
    }

    private sealed class Table
    {
        // The GridViewRowPresenter's 6 + 6 cell margin; the header's 6 + 6 padding, which lines its
        // text up with the cells, and the sort chevron (5 + 8).
        private const double CellPadding = 12;
        private const double HeaderPadding = 12 + 13;
        private const int MeasureAll = 5000;
        private const int MeasureLongest = 200;

        private readonly ListView _list;
        private readonly Dictionary<GridViewColumn, ColumnState> _states = [];
        private readonly Dictionary<(Type, string), PropertyInfo?> _properties = [];
        private readonly DispatcherTimer _fitTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly ContextMenu _menu = new();
        private readonly MenuItem _sortBy = new() { Header = "Sort by" };
        private readonly MenuItem _fitColumn = new() { Header = "Size column to fit" };
        private GridView? _view;
        private List<GridViewColumn>? _defaultOrder;
        private ScrollViewer? _scroller;
        private Window? _window;
        private GridViewColumn? _menuColumn;
        private TextBlock? _cellProbe;
        private TextBlock? _headerProbe;
        private FitMode? _pending;
        private DateTime _lastFit;
        private bool _fitting;
        private bool _restoring;

        public Table(ListView list)
        {
            _list = list;
            _fitTimer = new DispatcherTimer(DispatcherPriority.Background, list.Dispatcher);
            _fitTimer.Tick += (_, _) => RunPendingFit();
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, list.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => Save();
            _fitColumn.Click += (_, _) =>
            {
                if (_menuColumn is not null)
                {
                    FitColumn(_menuColumn);
                }
            };
            var fitAll = new MenuItem { Header = "Size all columns to fit", InputGestureText = "Ctrl+NumPad +" };
            fitAll.Click += (_, _) => FitAll();
            var reset = new MenuItem { Header = "Reset columns" };
            reset.Click += (_, _) => Reset();
            _menu.Items.Add(_sortBy);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(_fitColumn);
            _menu.Items.Add(fitAll);
            _menu.Items.Add(reset);
            list.Loaded += OnLoaded;
            list.Unloaded += OnUnloaded;
        }

        private string StoreKey => "table." + GetKey(_list);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_list.View is not GridView view || _view is not null)
            {
                return;
            }

            _view = view;
            _defaultOrder ??= [.. view.Columns];
            _list.ApplyTemplate();
            _scroller = Descendant<ScrollViewer>(_list);
            _window = Window.GetWindow(_list);
            MakeProbes();
            foreach (var column in view.Columns)
            {
                _states[column] = new ColumnState { Min = MinOf(column) };
                WidthDescriptor.AddValueChanged(column, OnWidthChanged);
            }

            Restore();
            view.Columns.CollectionChanged += OnColumnsChanged;
            ((INotifyCollectionChanged)_list.Items).CollectionChanged += OnItemsChanged;
            _list.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), handledEventsToo: true);
            _list.PreviewKeyDown += OnKeyDown;
            _list.ContextMenuOpening += OnMenuOpening;
            _list.ContextMenu = _menu;
            if (_scroller is not null)
            {
                _scroller.ScrollChanged += OnScrollChanged;
            }

            if (_window is not null)
            {
                _window.DpiChanged += OnDpiChanged;
                _window.Closing += OnClosing;
            }

            Fit(FitMode.Exact);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_view is null)
            {
                return;
            }

            Flush();
            _fitTimer.Stop();
            _pending = null;
            foreach (var column in _view.Columns)
            {
                WidthDescriptor.RemoveValueChanged(column, OnWidthChanged);
            }

            _view.Columns.CollectionChanged -= OnColumnsChanged;
            ((INotifyCollectionChanged)_list.Items).CollectionChanged -= OnItemsChanged;
            _list.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            _list.PreviewKeyDown -= OnKeyDown;
            _list.ContextMenuOpening -= OnMenuOpening;
            if (_scroller is not null)
            {
                _scroller.ScrollChanged -= OnScrollChanged;
            }

            if (_window is not null)
            {
                _window.DpiChanged -= OnDpiChanged;
                _window.Closing -= OnClosing;
            }

            _view = null;
            _scroller = null;
            _window = null;
        }

        private void OnClosing(object? sender, CancelEventArgs e) => Flush();

        private void OnDpiChanged(object sender, DpiChangedEventArgs e)
        {
            MakeProbes();
            RequestFit(FitMode.Exact);
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportWidthChange != 0)
            {
                Fit(FitMode.FillOnly);
            }
        }

        // A Reset (a new list, a sort's clear-and-refill) fits exactly; rows arriving one by one
        // only grow the columns; a removal changes nothing under the pointer.
        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Reset:
                    RequestFit(FitMode.Exact);
                    break;
                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Replace:
                    RequestFit(FitMode.GrowOnly);
                    break;
            }
        }

        private void RequestFit(FitMode mode)
        {
            _pending = _pending == FitMode.Exact || mode == FitMode.Exact ? FitMode.Exact : FitMode.GrowOnly;
            if (_fitTimer.IsEnabled)
            {
                return;
            }

            var wait = _lastFit + TimeSpan.FromMilliseconds(250) - DateTime.UtcNow;
            _fitTimer.Interval = mode == FitMode.Exact || wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            _fitTimer.Start();
        }

        private void RunPendingFit()
        {
            _fitTimer.Stop();
            if (_pending is { } mode)
            {
                _pending = null;
                Fit(mode);
            }
        }

        private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move && !_restoring)
            {
                ScheduleSave();
            }
        }

        // The built-in gripper double-click sets Width to NaN, which sizes to the rows on screen
        // only; the data fit replaces it. A width below the minimum goes back to the minimum.
        private void OnWidthChanged(object? sender, EventArgs e)
        {
            if (_fitting || _restoring || sender is not GridViewColumn column || !_states.TryGetValue(column, out var state))
            {
                return;
            }

            if (double.IsNaN(column.Width))
            {
                state.UserSized = false;
                _list.Dispatcher.InvokeAsync(() => FitColumn(column), DispatcherPriority.Loaded);
            }
            else if (column.Width < state.Min || (GetFixed(column) && column.Width != state.Min))
            {
                SetWidth(column, state.Min);
            }
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (e.HorizontalChange == 0 || Ancestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: { } column }
                || !_states.TryGetValue(column, out var state))
            {
                return;
            }

            state.UserSized = true;
            Fit(FitMode.FillOnly);
            ScheduleSave();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Add && Keyboard.Modifiers == ModifierKeys.Control)
            {
                FitAll();
                e.Handled = true;
            }
        }

        private void OnMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _menuColumn = Ancestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject)?.Column;
            _fitColumn.Visibility = _menuColumn is null || GetFixed(_menuColumn) ? Visibility.Collapsed : Visibility.Visible;
            _sortBy.Items.Clear();
            if (_view is null)
            {
                return;
            }

            foreach (var column in _view.Columns.Where(column => GameColumnSort.PathOf(column) is not null))
            {
                var item = new MenuItem { Header = column.Header as string, IsChecked = GetSortDirection(column) != TableSort.None };
                item.Click += (_, _) => GameColumnSort.Sort(_list, column);
                _sortBy.Items.Add(item);
            }
        }

        private void FitAll()
        {
            foreach (var state in _states.Values)
            {
                state.UserSized = false;
            }

            Fit(FitMode.Exact);
            ScheduleSave();
        }

        private void FitColumn(GridViewColumn column)
        {
            if (!_states.TryGetValue(column, out var state))
            {
                return;
            }

            state.UserSized = false;
            Fit(FitMode.FillOnly, column);
            ScheduleSave();
        }

        private void Reset()
        {
            if (_view is null || _defaultOrder is null)
            {
                return;
            }

            _restoring = true;
            try
            {
                for (var i = 0; i < _defaultOrder.Count; i++)
                {
                    var at = _view.Columns.IndexOf(_defaultOrder[i]);
                    if (at >= 0 && at != i)
                    {
                        _view.Columns.Move(at, i);
                    }
                }
            }
            finally
            {
                _restoring = false;
            }

            foreach (var state in _states.Values)
            {
                state.UserSized = false;
            }

            _saveTimer.Stop();
            Fit(FitMode.Exact);
            try
            {
                Store?.Delete(StoreKey);
            }
            catch (Exception)
            {
                // A layout that stays saved only comes back at the next start.
            }
        }

        // Fits the columns. With `only`, that column fits exactly and the rest keep their fit.
        private void Fit(FitMode mode, GridViewColumn? only = null)
        {
            if (_view is null)
            {
                return;
            }

            var columns = _view.Columns;
            var fill = GetFill(_list);
            var cache = new Dictionary<string, double>(StringComparer.Ordinal);
            var specs = new ColumnSpec[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var state = _states[column];
                var measure = !state.UserSized && !GetFixed(column) && (only is null ? mode != FitMode.FillOnly : column == only);
                var natural = measure ? Natural(column, cache) : 0;
                var current = double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
                var spec = new ColumnSpec(
                    natural, state.Min, GetMaxAutoWidth(column), state.Fitted, current, state.UserSized, GetFixed(column),
                    string.Equals(GetId(column), fill, StringComparison.Ordinal));
                if (column == only)
                {
                    spec = spec with { Fitted = ColumnFit.Compute([spec], 0, FitMode.Exact).Fitted[0] };
                }

                specs[i] = spec;
            }

            var result = ColumnFit.Compute(specs, _scroller?.ViewportWidth ?? 0, mode, 1 / VisualTreeHelper.GetDpi(_list).DpiScaleX);
            _fitting = true;
            try
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    _states[columns[i]].Fitted = result.Fitted[i];
                    if (!GetFixed(columns[i]) && columns[i].Width != result.Widths[i])
                    {
                        columns[i].Width = result.Widths[i];
                    }
                }
            }
            finally
            {
                _fitting = false;
            }

            _lastFit = DateTime.UtcNow;
        }

        private void SetWidth(GridViewColumn column, double width)
        {
            _fitting = true;
            try
            {
                column.Width = width;
            }
            finally
            {
                _fitting = false;
            }
        }

        // The widest of the header and every row's value, padding included: the data, not the
        // realized rows, so a long title below the fold still fits.
        private double Natural(GridViewColumn column, Dictionary<string, double> cache)
        {
            var header = Measure(_headerProbe, HeaderText(column.Header), cache: null) + HeaderPadding;
            var path = GetMeasurePath(column) ?? (column.DisplayMemberBinding as Binding)?.Path.Path;
            var cells = 0.0;
            if (path is not null)
            {
                var extraWhen = GetExtraWhen(column);
                var extra = GetExtraText(column) is { Length: > 0 } text ? 4 + Measure(_cellProbe, text, cache) : 0;
                var rows = _list.Items.Cast<object>().Select(item => (Text: Text(item, path), Extra: extraWhen is not null && Read(item, extraWhen) is true));
                if (_list.Items.Count > MeasureAll)
                {
                    rows = rows.OrderByDescending(row => row.Text.Length).Take(MeasureLongest);
                }

                var chips = GetChip(column) ? new Dictionary<string, double>() : null;
                foreach (var (value, hasExtra) in rows)
                {
                    var width = chips is null
                        ? Measure(_cellProbe, value, cache)
                        : Measure(_headerProbe, value.ToUpper(CultureInfo.CurrentCulture), chips) + ChipPadding;
                    cells = Math.Max(cells, width + (hasExtra ? extra : 0));
                }

                cells += CellPadding + GetTrailingGap(column);
            }

            return Math.Ceiling(Math.Max(header, cells)) + 1;
        }

        private double MinOf(GridViewColumn column)
        {
            if (GetFixed(column))
            {
                return column.Width;
            }

            var header = Math.Ceiling(Measure(_headerProbe, HeaderText(column.Header), cache: null) + HeaderPadding) + 1;
            return Math.Max(Math.Max(GetMinWidth(column), 48), header);
        }

        // Off-tree TextBlocks in the cell and header styles, so the measure matches what renders
        // (tabular digits, Display-mode advances), unlike FormattedText.
        private void MakeProbes()
        {
            _cellProbe = Probe("Text.Cell");
            _headerProbe = Probe("Text.Label");
        }

        private TextBlock Probe(string style)
        {
            var probe = new TextBlock { Style = _list.TryFindResource(style) as Style };
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(_list));
            return probe;
        }

        private static double Measure(TextBlock? probe, string text, Dictionary<string, double>? cache)
        {
            if (probe is null || text.Length == 0)
            {
                return 0;
            }

            if (cache is not null && cache.TryGetValue(text, out var known))
            {
                return known;
            }

            probe.Text = text;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = probe.DesiredSize.Width;
            cache?.Add(text, width);
            return width;
        }

        private string Text(object item, string path) => Convert.ToString(Read(item, path), CultureInfo.CurrentCulture) ?? string.Empty;

        private object? Read(object item, string path)
        {
            var key = (item.GetType(), path);
            if (!_properties.TryGetValue(key, out var property))
            {
                property = key.Item1.GetProperty(path, BindingFlags.Instance | BindingFlags.Public);
                _properties[key] = property;
            }

            return property?.GetValue(item);
        }

        // The saved order is applied and the saved widths become user-sized, before the first fit.
        private void Restore()
        {
            if (_view is null || _defaultOrder is null || Store is null)
            {
                return;
            }

            TableLayout? layout;
            try
            {
                layout = TableLayout.Parse(Store.Get(StoreKey));
            }
            catch (Exception)
            {
                return;
            }

            if (layout is null)
            {
                return;
            }

            var byId = _view.Columns.Where(column => GetId(column) is not null).ToDictionary(column => GetId(column)!, StringComparer.Ordinal);
            var order = layout.Arrange(_defaultOrder.Select(GetId).OfType<string>().ToList());
            _restoring = true;
            try
            {
                for (var i = 0; i < order.Count; i++)
                {
                    var at = _view.Columns.IndexOf(byId[order[i]]);
                    if (at >= 0 && at != i)
                    {
                        _view.Columns.Move(at, i);
                    }
                }

                foreach (var (id, width) in layout.Widths)
                {
                    if (byId.TryGetValue(id, out var column) && !GetFixed(column))
                    {
                        var state = _states[column];
                        state.UserSized = true;
                        column.Width = Math.Max(width, state.Min);
                    }
                }
            }
            finally
            {
                _restoring = false;
            }
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Flush()
        {
            if (_saveTimer.IsEnabled)
            {
                Save();
            }
        }

        private void Save()
        {
            _saveTimer.Stop();
            if (_view is null || Store is null)
            {
                return;
            }

            var layout = new TableLayout(
                _view.Columns.Select(GetId).OfType<string>().ToList(),
                _view.Columns
                    .Where(column => GetId(column) is not null && _states.TryGetValue(column, out var state) && state.UserSized)
                    .ToDictionary(column => GetId(column)!, column => Math.Round(column.ActualWidth, 2), StringComparer.Ordinal));
            try
            {
                Store.Set(StoreKey, layout.ToJson());
            }
            catch (Exception)
            {
                // Losing a layout only costs a re-fit at the next start.
            }
        }

        private static T? Ancestor<T>(DependencyObject? node)
            where T : DependencyObject
        {
            while (node is not null and not T)
            {
                node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }

            return node as T;
        }

        private static T? Descendant<T>(DependencyObject node)
            where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T found)
                {
                    return found;
                }

                if (Descendant<T>(child) is { } deeper)
                {
                    return deeper;
                }
            }

            return null;
        }
    }
}
