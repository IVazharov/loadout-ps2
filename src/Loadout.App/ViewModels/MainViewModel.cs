using Loadout.Artwork;
using Loadout.Bay;
using Loadout.Core;
using Loadout.Images;
using Loadout.Metadata;
using Loadout.Persistence;

namespace Loadout.App;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly OperationLog _log;
    private NavPage _selected;

    public MainViewModel(
        AppSettingsStore settingsStore,
        InstalledGameListStore savedList,
        ArtworkCache artworkCache,
        ArtworkDownloader artworkDownloader,
        OperationLog log,
        Func<Question, bool> confirm,
        Func<Question, int?> choose,
        IDiscTitleCatalog titles,
        Func<byte[]?> bundledBay)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(savedList);
        ArgumentNullException.ThrowIfNull(artworkCache);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var stored = settingsStore.Load();
        // After an install, the list read from the PS2 goes to Installed Games and is saved, as after Connect.
        Install = new InstallViewModel(
            log,
            CurrentHost,
            library => AfterInstallAsync(savedList, library),
            titles,
            confirm,
            artworkCache,
            artworkDownloader)
        {
            CheckInstalls = stored.CheckInstalls,
        };
        Settings = new SettingsViewModel(settingsStore, stored, Install, log);
        Installed = new InstalledGamesViewModel(
            artworkCache,
            () => Settings.Host,
            library => KeepList(savedList, library),
            log,
            confirm,
            choose,
            bay => Settings.Use(bay),
            titles,
            bundledBay);
        Artwork = new ArtworkViewModel(artworkCache, artworkDownloader, () => Settings.Host, () => Installed.Games, log);
        Logs = new LogsViewModel(log);
        Pages =
        [
            new NavPage { Title = "Installed Games", Content = Installed, Number = 1 },
            new NavPage { Title = "Install", Content = Install, Number = 2 },
            new NavPage { Title = "Artwork", Content = Artwork, Number = 3 },
            new NavPage { Title = "Settings", Content = Settings, Number = 4 },
            new NavPage { Title = "Logs", Content = Logs, Number = 5 },
        ];
        _selected = Pages[0];
        ShowInstalledCommand = new RelayCommand(() => SelectedPage = Pages[0]);
        ShowInstallCommand = new RelayCommand(() => SelectedPage = Pages[1]);
        LoadSavedList(savedList);
    }

    /// <summary>The empty states' way to the page that fills them.</summary>
    public System.Windows.Input.ICommand ShowInstalledCommand { get; }

    public System.Windows.Input.ICommand ShowInstallCommand { get; }

    public InstalledGamesViewModel Installed { get; }

    // Install calls these only after the constructor, once Settings and Installed exist.
    private string CurrentHost() => Settings.Host;

    private async Task AfterInstallAsync(InstalledGameListStore savedList, BayLibrary library)
    {
        var titled = await Installed.TitleIncompleteAsync(library);
        Installed.ShowRead(titled, Settings.Host.Trim());
        KeepList(savedList, titled);
    }

    public InstallViewModel Install { get; }

    public SettingsViewModel Settings { get; }

    public ArtworkViewModel Artwork { get; }

    public LogsViewModel Logs { get; }

    public IReadOnlyList<NavPage> Pages { get; }

    public NavPage SelectedPage
    {
        get => _selected;
        set
        {
            if (value is not null && SetField(ref _selected, value) && value.Content is ArtworkViewModel artwork)
            {
                // The installed list may have changed since the page was last open.
                artwork.Refresh();
            }
        }
    }

    /// <summary>Disposes the page view models. The window calls this when it closes.</summary>
    public void Dispose()
    {
        Install.Dispose();
        Artwork.Dispose();
    }

    /// <summary>One line in Logs for an exception nothing else handled.</summary>
    public void ReportUnexpected(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _log.Add("Unexpected error: " + exception.Message + " Details are in " + ErrorLog.FileName + ".");
    }

    /// <summary>Saves the list Loadout Bay returned for the next start, and lets Install check new discs against it.</summary>
    private void KeepList(InstalledGameListStore savedList, BayLibrary library)
    {
        Install.ReplaceInstalled(library.Games, library.Space);
        try
        {
            savedList.Replace(library.Games, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            _log.Add("The game list from the PS2 could not be saved for the next start.");
        }
    }

    private void LoadSavedList(InstalledGameListStore savedList)
    {
        try
        {
            var saved = savedList.Load();
            if (saved is not null && saved.Games.Count > 0)
            {
                Installed.ShowSavedList(saved);
                Install.ReplaceInstalled(saved.Games, null);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            _log.Add("The saved game list could not be read.");
        }
    }
}
