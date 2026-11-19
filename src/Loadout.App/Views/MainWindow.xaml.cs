using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Loadout.App.Controls;
using Loadout.Artwork;
using Loadout.Core;
using Loadout.Images;
using Loadout.Metadata;
using Loadout.Persistence;
using Loadout.Updates;

namespace Loadout.App;

public partial class MainWindow : LoadoutWindow
{
    // One client for the app's lifetime: the title lookups reuse its connections.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // An update's download is a few MB; on a slow line that takes longer than the lookups' 15 s.
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const string BoundsKey = "window";

    private readonly UiStateStore _uiState;
    private readonly OperationLog _log = new();
    private readonly UpdateInstaller _updates;

    // Why Update now can't run here, or null: a build folder isn't the release's single file.
    private readonly string? _cannotUpdate;

    // Where the window was last time, when that place is on a monitor that is still plugged in.
    private readonly WindowBounds? _lastBounds;

    // The game lists whose empty space already unselects; a list is loaded again each time its page shows.
    private readonly HashSet<ListView> _clearOnEmptyClick = [];

    // Set when Windows ends the session or an update restarts Loadout: no question before closing then.
    private bool _closeWithoutAsking;

    // The pages' lists, by their names: Ctrl+A and Esc pick in the one the page shows, wherever the keyboard is on it.
    private static readonly string[] PageLists = ["Installed games", "Install queue", "Backed-up discs", "Games and their artwork", "Log"];

    public MainWindow()
    {
        InitializeComponent();
        var layout = PortableData.Resolve(AppContext.BaseDirectory);
        var settingsStore = new AppSettingsStore(layout.DatabaseFile);
        var stored = settingsStore.Load();
        _uiState = new UiStateStore(layout.DatabaseFile);
        TableColumns.Store = _uiState;
        var artworkCache = new ArtworkCache(layout.ArtworkCache);
        var viewModel = new MainViewModel(
            settingsStore,
            new InstalledGameListStore(layout.DatabaseFile),
            artworkCache,
            new ArtworkDownloader(new Luden02ArtworkProvider(Http), artworkCache),
            _log,
            Confirm,
            question => QuestionDialog.Choose(this, question),
            new OplCfgTitleCatalog(Http, new DiscTitleCache(layout.DatabaseFile)),
            BundledBay,
            new Loadout.Updates.GitHubReleaseFeed(Http, ProductInfo.Version),
            // Redump's list (about 1.3 MB) is kept in the data folder beside the database.
            new Loadout.Discs.RedumpDatCache(Downloads, Path.GetDirectoryName(layout.DatabaseFile)!, ProductInfo.Version));
        DataContext = viewModel;
        // Back up discs' news while Loadout isn't in front: a Windows notification, and a flashing taskbar button.
        var notifier = new DesktopNotifier(this, () => viewModel.ShowBackupCommand.Execute(null));
        viewModel.Backup.Notice += (_, notice) => notifier.Show(notice.Title, notice.Text, needsUser: notice.Kind != BackupNoticeKind.BackedUp);
        Closed += (_, _) => notifier.Dispose();

        // Windows signing out or shutting down closes Loadout without the question.
        Application.Current.SessionEnding += (_, _) => _closeWithoutAsking = true;
        for (var i = 0; i < viewModel.Pages.Count; i++)
        {
            var page = viewModel.Pages[i];
            var show = new RelayCommand(() => viewModel.SelectedPage = page);
            InputBindings.Add(new KeyBinding(show, Key.D1 + i, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(show, Key.NumPad1 + i, ModifierKeys.Control));
        }

        _lastBounds = ReadBounds();
        if (_lastBounds is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        AppTheme.Apply(stored.Theme);
        Title = ProductInfo.Name + " " + ProductInfo.Version;

        _updates = new UpdateInstaller(
            Downloads,
            ProductInfo.Version,
            AppContext.BaseDirectory,
            Path.Combine(Path.GetDirectoryName(layout.DatabaseFile)!, "update"),
            new SignedExecutableCheck());
        // The release is one file; a build folder has the program's DLL beside the exe, and an update would leave that DLL old.
        _cannotUpdate = File.Exists(Path.Combine(AppContext.BaseDirectory, "LoadoutPS2.dll"))
            ? "This Loadout runs from a build folder, not from a release, so it can't replace itself. Get the new version from its release page."
            : null;
        ContentRendered += OnFirstRender;
    }

    // After the window is up: tidy up after an update, connect again if the old Loadout was connected,
    // then the start check, which shows the update dialog when a newer version is out.
    private async void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        var start = StartOptions.Current;
        if (start.UpdatedFrom is { } from)
        {
            model.About.ShowUpdated(from);
        }

        CleanUpAfterUpdate();
        if (start.Reconnect && model.Installed.ConnectCommand.CanExecute(null))
        {
            model.Installed.ConnectCommand.Execute(null);
        }

        if (model.Settings.CheckForUpdates)
        {
            await model.About.CheckAsync(CancellationToken.None);
            if (model.About.Offer is { } offer)
            {
                ShowUpdate(model, offer);
            }
        }
    }

    private void CleanUpAfterUpdate()
    {
        try
        {
            if (!_updates.CleanUp())
            {
                _log.Add("Some files the last update replaced are still in use; Loadout removes them next time.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add("The last update's leftover files could not be removed: " + ex.Message);
        }

        // A release folder only: 0.10.0's and older zips put licence files beside the program, which updates left behind.
        if (_cannotUpdate is null)
        {
            var removed = OldReleaseFiles.Remove(AppContext.BaseDirectory);
            if (removed.Count > 0)
            {
                _log.Add(string.Create(
                    CultureInfo.CurrentCulture,
                    $"Removed {removed.Count} licence files that Loadout 0.10.0 and older kept beside the program. Every licence text is on the About page."));
            }
        }
    }

    private void ShowUpdate(MainViewModel model, Loadout.Updates.UpdateOffer offer)
    {
        using var update = new UpdateViewModel(
            offer,
            model.Settings,
            _cannotUpdate is null ? _updates : null,
            _cannotUpdate,
            () => model.Install.IsWorking || !model.Installed.CanConnect || model.Artwork.IsBusy,
            _log);
        UpdateDialog.Show(this, update, () => RestartAsNewVersion(model));
    }

    // About's Update now, after a check that found a newer version.
    private void UpdateNow_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { About.Offer: { } offer } model)
        {
            ShowUpdate(model, offer);
        }
    }

    // The new files are in place: start the new Loadout, which waits for this one to close, and close.
    private void RestartAsNewVersion(MainViewModel model)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(_updates.AppFolder, UpdateInstaller.ExecutableName))
        {
            UseShellExecute = false,
            WorkingDirectory = _updates.AppFolder,
        };
        foreach (var argument in StartOptions.AfterUpdate(ProductInfo.Version, model.Installed.IsConnected))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write("Restart after update", ex);
            QuestionDialog.Ask(this, new Question(
                "Start Loadout again",
                "Loadout is updated, but Windows didn't let it start the new version (" + ex.Message + "). Start Loadout again yourself.",
                "_Close",
                "_Close",
                QuestionKind.Warn));
        }

        _closeWithoutAsking = true;
        Application.Current.Shutdown();
    }

    // The Loadout Bay built with this Loadout, embedded when ps2/loadout-bay.elf was there at build time.
    private static byte[]? BundledBay()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("loadout-bay.elf");
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private bool Confirm(Question question) => QuestionDialog.Ask(this, question);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(AppTheme.OnWindowMessage);
        // The first time, this is where WPF centred the window, which is off the top of a screen shorter than the window.
        var bounds = _lastBounds ?? WindowPlacement.Read(this);
        if (bounds is not null)
        {
            WindowPlacement.Restore(this, WindowPlacement.KeepOnScreen(bounds));
        }
    }

    // Failures are ignored, as with the table layouts: the window then opens centred next time.
    private WindowBounds? ReadBounds()
    {
        try
        {
            var bounds = WindowBounds.Parse(_uiState.Get(BoundsKey));
            return bounds is not null && WindowPlacement.IsOnAMonitor(bounds) ? bounds : null;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return null;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A backup or an install under way: closing stops it, so Loadout asks first.
        if (!_closeWithoutAsking && DataContext is MainViewModel model && model.CloseWarning() is { } warning
            && !QuestionDialog.Ask(this, new Question(
                "Close Loadout?",
                warning,
                "_Close anyway",
                "_Keep Loadout open",
                QuestionKind.Warn,
                Destructive: true)))
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
        var bounds = e.Cancel ? null : WindowPlacement.Read(this);
        if (bounds is null)
        {
            return;
        }

        try
        {
            _uiState.Set(BoundsKey, bounds.ToJson());
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
        }
    }

    // The rail's address opens Settings with the keyboard in the address box.
    private void AddressLink_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        model.SelectedPage = model.Pages.First(page => page.Content == model.Settings);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindNamed<AddressEditor>(PageHost, "AddressBox")?.FocusFirst());
    }

    // Find Bay disables itself while it searches and Search again hides, so the keyboard moves to Cancel,
    // where Esc still reaches the row; when Cancel hides, it goes back to Find Bay.
    private void FindBay_OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            KeepKeyboardInRow((FrameworkElement)sender, "FindCancel");
        }
    }

    private void SearchAgain_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            KeepKeyboardInRow((FrameworkElement)sender, "FindCancel");
        }
    }

    private void FindCancel_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            KeepKeyboardInRow((FrameworkElement)sender, "FindBayButton");
        }
    }

    private void KeepKeyboardInRow(FrameworkElement leaving, string next)
    {
        if (leaving.IsKeyboardFocused && leaving.FindName(next) is UIElement target)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => target.Focus());
        }
    }

    // Find Bay's picker: the keyboard goes to the first found Bay.
    private void BayPicker_OnOpened(object sender, EventArgs e)
    {
        if (((Popup)sender).Child is Border { Child: ListBox list } && list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus());
        }
    }

    // Closed with a choice, Esc or a click outside, the keyboard goes back to Find Bay.
    private void BayPicker_OnClosed(object sender, EventArgs e) => (((Popup)sender).PlacementTarget as UIElement)?.Focus();

    // Enter chooses the selected Bay, Esc closes the picker without a change.
    private void BayPicker_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.DataContext is not SettingsViewModel settings)
        {
            return;
        }

        if (e.Key == Key.Enter && list.SelectedItem is FoundBayRow row)
        {
            settings.Choose(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            settings.IsChoosing = false;
            e.Handled = true;
        }
    }

    private void BayPicker_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.DataContext is SettingsViewModel settings
            && ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: FoundBayRow row })
        {
            settings.Choose(row);
        }
    }

    // Segments: the arrow keys choose the next or previous one, as Windows' own radio groups do; no wrap.
    private void Segments_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        var segments = ((Panel)sender).Children.OfType<RadioButton>().ToList();
        var index = segments.FindIndex(segment => segment.IsKeyboardFocused);
        if (step == 0 || index < 0)
        {
            return;
        }

        e.Handled = true;
        var next = index + step;
        if (next >= 0 && next < segments.Count)
        {
            segments[next].IsChecked = true;
            segments[next].Focus();
        }
    }

    private void CopyAllLogs_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && model.Logs.Lines.Count > 0)
        {
            CopyLines(model.Logs.Lines.Select(entry => entry.Text));
        }
    }

    // Ctrl+C in Logs copies the selected lines, in the order they are listed.
    private void LogList_OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItems.Count > 0)
        {
            CopyLines(list.Items.Cast<LogEntry>().Where(list.SelectedItems.Contains).Select(entry => entry.Text));
        }
    }

    private static void CopyLines(IEnumerable<string> lines)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard; the next try usually works.
        }
    }

    private void OpenErrorLogFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(ErrorLog.PathOnDisk)!;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            (DataContext as MainViewModel)?.ReportUnexpected(ex);
        }
    }

    // The About page's links: the button's Tag is the https address, opened in the default browser.
    private void OpenLink_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string address }
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            (DataContext as MainViewModel)?.ReportUnexpected(ex);
        }
    }

    private static T? FindNamed<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            if (FindNamed<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.Dispose();
        }

        base.OnClosed(e);
    }

    private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list || list.DataContext is not InstallViewModel install)
        {
            return;
        }

        var selected = new QueuePreviewItem[list.SelectedItems.Count];
        list.SelectedItems.CopyTo(selected, 0);
        install.SetSelection(selected);
    }

    // Ctrl+C in a list copies its picked rows as text, a line of column names first, the columns in their shown order.
    private void ListRows_OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is ListView list)
        {
            TableColumns.CopyRows(list);
        }
    }

    private void ArtworkList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => PickArtwork(sender);

    // The list is made again each time its page shows, with nothing picked: the buttons then act on every game.
    private void ArtworkList_Loaded(object sender, RoutedEventArgs e)
    {
        GameList_Loaded(sender, e);
        PickArtwork(sender);
    }

    private static void PickArtwork(object sender)
    {
        if (sender is ListView list && list.DataContext is ArtworkViewModel artwork)
        {
            artwork.SetPicked([.. list.SelectedItems.OfType<ArtworkRow>()]);
        }
    }

    // Keys for the page shown that nothing on it took first. Ctrl+A picks every row of its list and Esc none (not while
    // typing); F5 reads the list again; Ctrl+O adds files on Install; on Installed Games, F2, or Enter in the list, goes to
    // the picked game's title, and Ctrl+S saves it.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel model)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var plain = Keyboard.Modifiers == ModifierKeys.None;
        var typing = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox;
        var page = model.SelectedPage.Content;
        var list = PageList();
        switch (e.Key)
        {
            case Key.A when ctrl && list is { SelectionMode: not SelectionMode.Single }:
                list.SelectAll();
                list.Focus();
                e.Handled = true;
                break;
            case Key.Escape when plain && !typing && list is { SelectedItems.Count: > 0 }:
                list.UnselectAll();
                e.Handled = true;
                break;
            case Key.F5 when plain:
                e.Handled = ReadAgain(model, page);
                break;
            case Key.O when ctrl && page is InstallViewModel install:
                install.AddFilesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2 when plain && page is InstalledGamesViewModel:
            case Key.Enter when plain && page is InstalledGamesViewModel && list is { IsKeyboardFocusWithin: true }:
                e.Handled = FocusTitle();
                break;
            case Key.S when ctrl && page is InstalledGamesViewModel { CanSave: true } installed:
                installed.SaveEditCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // F5: Back up discs looks through its folder again; Installed Games and Artwork read the list from Loadout Bay again.
    private static bool ReadAgain(MainViewModel model, object? page)
    {
        switch (page)
        {
            case BackupViewModel backup:
                backup.RefreshList();
                return true;
            case InstalledGamesViewModel or ArtworkViewModel when model.Installed.CanConnect:
                model.Installed.ConnectCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private bool FocusTitle()
    {
        if (FindNamed<TextBox>(PageHost, "TitleBox") is not { IsVisible: true, IsEnabled: true } title)
        {
            return false;
        }

        title.Focus();
        title.SelectAll();
        return true;
    }

    private ListBox? PageList() =>
        Descendants<ListBox>(PageHost).FirstOrDefault(list => list.IsVisible && PageLists.Contains(AutomationProperties.GetName(list)));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var found in Descendants<T>(child))
            {
                yield return found;
            }
        }
    }

    // A list made again, as its page shows, may have its pick back from a binding before it could say so: it says so now.
    private void QueueList_Loaded(object sender, RoutedEventArgs e)
    {
        GameList_Loaded(sender, e);
        QueueList_OnSelectionChanged(sender, null!);
    }

    private void BackupList_Loaded(object sender, RoutedEventArgs e)
    {
        GameList_Loaded(sender, e);
        BackupList_OnSelectionChanged(sender, null!);
    }

    private void BackupList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && list.DataContext is BackupViewModel backup)
        {
            backup.SetPicked([.. list.SelectedItems.OfType<BackupRow>()]);
        }
    }

    private void GameList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list)
        {
            GameColumnSort.Enable(list);
            if (_clearOnEmptyClick.Add(list))
            {
                list.PreviewMouseLeftButtonDown += GameList_PreviewMouseLeftButtonDown;
            }
        }
    }

    // A click on a list's empty space, under its rows, unselects everything, as in Explorer.
    private static void GameList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list)
        {
            return;
        }

        for (var at = e.OriginalSource as DependencyObject; at is not null && at != list;
             at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is ListViewItem or GridViewColumnHeader or ScrollBar)
            {
                return;
            }
        }

        list.UnselectAll();
    }
}
