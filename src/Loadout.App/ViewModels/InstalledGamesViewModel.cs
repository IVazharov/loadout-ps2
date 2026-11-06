using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Loadout.Artwork;
using Loadout.Bay;
using Loadout.Core;
using Loadout.Images;
using Loadout.Library;
using Loadout.Metadata;
using Loadout.Persistence;

namespace Loadout.App;

/// <summary>
/// The Installed Games page. Connect reads the game list from Loadout Bay at the address in Settings;
/// until then it shows the list saved from the last Connect, with the cached cover or icon for each game.
/// Remove takes the selected games off the PS2 after the user confirms, and the editor below the
/// list changes the selected game's title and disc type in its header, and its compatibility modes
/// and transfer mode in its settings file in OPL's CFG folder, where OPL reads them. When Connect finds
/// a Loadout Bay of another version, it offers to send the one built with this Loadout.
/// </summary>
public sealed class InstalledGamesViewModel : ObservableObject
{
    private readonly ArtworkCache _artwork;
    private readonly Func<string> _hostFor;
    private readonly Action<BayLibrary> _onRead;
    private readonly OperationLog _log;
    private readonly Func<Question, bool> _confirm;
    private readonly Func<Question, int?> _choose;
    private readonly Action<FoundBay> _useBay;
    private readonly IDiscTitleCatalog _titles;
    private readonly Func<byte[]?> _bundledBay;
    private InstalledGameRow? _selected;
    private InstalledGameRow[] _selection = [];
    private bool _connecting;
    private bool _connected;
    private bool _finding;
    private string _emptyNote = "";
    private CancellationTokenSource? _searchRun;
    private PageStatus _status;
    private string _summary = "";
    private string _editorTitle = "";
    private byte _editorDiscType;
    private DiscTypeChoice[] _discTypeChoices = DiscTypeChoicesFor(null);
    private string _editStatus = "";
    private bool _editFailed;
    private OplGameSettings? _settings;
    private int _editorDma = OplGameSettings.DefaultDma;
    private string _settingsStatus = "";
    private bool _settingsFailed;
    private bool _readingSettings;
    private int _settingsRead;

    public InstalledGamesViewModel(
        ArtworkCache artwork,
        Func<string> hostFor,
        Action<BayLibrary> onRead,
        OperationLog log,
        Func<Question, bool> confirm,
        Func<Question, int?> choose,
        Action<FoundBay> useBay,
        IDiscTitleCatalog titles,
        Func<byte[]?> bundledBay)
    {
        _choose = choose ?? throw new ArgumentNullException(nameof(choose));
        _useBay = useBay ?? throw new ArgumentNullException(nameof(useBay));
        _titles = titles ?? throw new ArgumentNullException(nameof(titles));
        _bundledBay = bundledBay ?? throw new ArgumentNullException(nameof(bundledBay));
        _artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        _hostFor = hostFor ?? throw new ArgumentNullException(nameof(hostFor));
        _onRead = onRead ?? throw new ArgumentNullException(nameof(onRead));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        FindBayCommand = new AsyncRelayCommand(FindBayAsync);
        CancelSearchCommand = new RelayCommand(() => _searchRun?.Cancel());
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        PutBayOnDiskCommand = new AsyncRelayCommand(PutBayOnDiskAsync);
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync);
        RevertCommand = new RelayCommand(Revert);
        for (var mode = 1; mode <= 8; mode++)
        {
            var choice = new CompatibilityModeChoice(mode);
            choice.PropertyChanged += (_, _) => OnEdited();
            CompatibilityModes.Add(choice);
        }

        Games.CollectionChanged += (_, _) => OnListChanged();
        _status = NotConnected();
    }

    public ObservableCollection<InstalledGameRow> Games { get; } = new();

    public ICommand ConnectCommand { get; }

    /// <summary>The empty state's Find Bay: the Settings search, with its result in the empty state's detail line.</summary>
    public ICommand FindBayCommand { get; }

    /// <summary>Stops the search that runs after Connect got no answer.</summary>
    public ICommand CancelSearchCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand PutBayOnDiskCommand { get; }

    public ICommand SaveEditCommand { get; }

    /// <summary>Puts back the selected game's values as they were read.</summary>
    public ICommand RevertCommand { get; }

    /// <summary>The game the editor shows: the one the user selected last.</summary>
    public InstalledGameRow? SelectedGame
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                LoadEditor(value);
                OnEditorChanged();
            }
        }
    }

    public PageStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>The page header's summary: "42 GAMES · 812.4 GB FREE OF 931.5 GB", or "42 GAMES · SAVED LIST".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    /// <summary>"Connect", or "Connect again" once a list has been read.</summary>
    public string ConnectLabel => _connected ? "_Connect again" : "_Connect";

    /// <summary>False while there is no list to show: the page shows its empty state and no details panel.</summary>
    public bool HasGames => Games.Count > 0;

    /// <summary>True when Connect read a list with no games in it, so the empty state points to Install.</summary>
    public bool NoGamesOnPs2 => _connected && Games.Count == 0;

    public string EmptyHeadline => NoGamesOnPs2 ? "No games on the PS2's hard disk yet" : "No game list yet";

    public string EmptyDetail => NoGamesOnPs2
        ? "Install sends disc images to Loadout Bay, and they show here."
        : _emptyNote.Length > 0 ? _emptyNote : "Start Loadout Bay on the PS2, then Connect.";

    /// <summary>False while a Connect is running, so the button cannot start a second one.</summary>
    public bool CanConnect => !_connecting && !_finding;

    /// <summary>True while the search after a Connect without an answer runs, so Cancel shows.</summary>
    public bool IsSearching => _searchRun is not null;

    public bool IsFinding => _finding;

    public bool CanFind => !_connecting && !_finding;

    public string FindLabel => _finding ? "Searching…" : "_Find Bay";

    public string FindStatus => _finding ? "Searching" : "";

    /// <summary>True when every selected game is one Loadout Bay can remove.</summary>
    public bool CanRemove => !_connecting && _selection.Length > 0 && _selection.All(BayGameChanges.CanRemove);

    /// <summary>True when one whole game is selected, so the editor has a header to change.</summary>
    public bool CanEdit => !_connecting && _selection.Length == 1 && _selected is not null && BayGameChanges.CanEdit(_selected);

    /// <summary>True once the selected game's settings file has been read, so its modes can be changed.</summary>
    public bool CanEditSettings => CanEdit && _settings is not null;

    /// <summary>Why the details panel shows no editor, in the line it shows instead; empty while it can edit.</summary>
    public string EditorHint =>
        _selection.Length == 0 || _selected is null ? "Select one game to change its title, disc type, modes or transfer mode."
        : _selection.Length > 1 ? _selection.Length.ToString(CultureInfo.CurrentCulture) + " games selected. Select one to change it."
        : !BayGameChanges.CanEdit(_selected) ? "An incomplete install can't be changed. Installing the game again replaces it."
        : _connecting ? "Wait for the current task to finish."
        : "";

    /// <summary>True when a value in the editor differs from what was read.</summary>
    public bool IsDirty =>
        _selected is { } game
        && (TitleText.FitFullTitle(EditorTitle.Trim()) != game.Title
            || EditorDiscType != game.DiscType
            || (_settings is not null && (EditorModes() != _settings.CompatibilityModes || EditorDma != _settings.Dma)));

    public bool TitleIsBlank => EditorTitle.Trim().Length == 0;

    /// <summary>Save changes is enabled only when there is a change to save and the game keeps a title.</summary>
    public bool CanSave => CanEdit && IsDirty && !TitleIsBlank;

    public string EditorTitle
    {
        get => _editorTitle;
        set
        {
            if (SetField(ref _editorTitle, value ?? ""))
            {
                OnPropertyChanged(nameof(TitleIsBlank));
                OnEdited();
            }
        }
    }

    /// <summary>OPL's compatibility modes 1 to 8, one bit each of <c>$Compatibility</c>.</summary>
    public ObservableCollection<CompatibilityModeChoice> CompatibilityModes { get; } = new();

    /// <summary>OPL's transfer modes, in the order of its game settings: the index is <c>$DMA</c>.</summary>
    public IReadOnlyList<string> DmaChoices { get; } = OplGameSettings.DmaLabels;

    public int EditorDma
    {
        get => _editorDma;
        set
        {
            if (SetField(ref _editorDma, value))
            {
                OnEdited();
            }
        }
    }

    public DiscTypeChoice[] DiscTypeChoices
    {
        get => _discTypeChoices;
        private set => SetField(ref _discTypeChoices, value);
    }

    public byte EditorDiscType
    {
        get => _editorDiscType;
        set
        {
            if (SetField(ref _editorDiscType, value))
            {
                OnEdited();
            }
        }
    }

    /// <summary>True while the selected game's OPL settings are being read.</summary>
    public bool IsReadingSettings
    {
        get => _readingSettings;
        private set => SetField(ref _readingSettings, value);
    }

    /// <summary>The details panel's help line: the last save's result, or else whether the OPL settings were read.</summary>
    public string DetailsHelp => _editStatus.Length > 0 ? _editStatus : _settingsStatus;

    public bool DetailsHelpIsError => _editStatus.Length > 0 ? _editFailed : _settingsFailed;

    /// <summary>Every game selected in the list, for Remove.</summary>
    public void SetSelection(IReadOnlyList<InstalledGameRow> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        _selection = selected.ToArray();
        OnPropertyChanged(nameof(CanRemove));
        OnEditorChanged();
    }

    public void ShowSavedList(SavedInstalledGames saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Games.Count == 0)
        {
            return;
        }

        ShowGames(saved.Games);
        var time = saved.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        Status = PageStatus.Idle("Not connected", "Showing the list from the last Connect, " + time + ".");
        Summary = Count(saved.Games.Count).ToUpper(CultureInfo.CurrentCulture) + " · SAVED LIST";
    }

    private PageStatus NotConnected() =>
        PageStatus.Idle(
            "Not connected",
            _hostFor().Trim().Length == 0 ? "Set the PS2's address in Settings, or Find Bay." : "Start Loadout Bay on the PS2, then Connect.");

    private void ShowGames(IReadOnlyList<InstalledGameRow> games)
    {
        Games.Clear();
        foreach (var game in GameListSort.ByTitle(games, game => game.Title))
        {
            Games.Add(game with { ArtPath = _artwork.CoverOrIconPath(game.DiscId) });
        }
    }

    // The list is on screen by now, and the covers fill in as they arrive. It is whole without them,
    // so a failure here is only logged.
    private async Task ReadCoversAsync(string host)
    {
        var discs = Games
            .Where(game => game.ArtPath is null && BayGameChanges.CanEdit(game))
            .Select(game => DiscId.TryParse(game.DiscId, out var disc) ? disc : (DiscId?)null)
            .OfType<DiscId>()
            .ToList();
        if (discs.Count == 0)
        {
            return;
        }

        var shown = Status;
        var progress = new Progress<CoverReadProgress>(step =>
        {
            ShowCover(step.ReadDiscId);
            Status = PageStatus.Busy(
                "Reading covers from the PS2",
                string.Create(CultureInfo.CurrentCulture, $"{step.Done} of {step.Total}"),
                step.Done * 100.0 / step.Total);
        });
        try
        {
            var read = await BayArtwork.ReadCoversAsync(host, discs, _artwork, progress);
            _log.Add(string.Create(CultureInfo.CurrentCulture, $"Read {read} of {discs.Count} covers from the PS2."));
        }
        catch (Exception ex) when (ex is LoadoutBayException or IOException or UnauthorizedAccessException)
        {
            _log.Add("Covers weren't read from the PS2: " + ex.Message);
        }

        Status = shown;
    }

    // Replacing a row would drop it from the selection, so a selected row gets its cover from the next list.
    private void ShowCover(string? discId)
    {
        for (var i = 0; discId is not null && i < Games.Count; i++)
        {
            var game = Games[i];
            if (game.DiscId == discId && game.ArtPath is null && game != _selected && !_selection.Contains(game))
            {
                Games[i] = game with { ArtPath = _artwork.CoverOrIconPath(discId) };
            }
        }
    }

    private Task ConnectAsync() => ConnectAsync(searchIfUnreachable: true);

    // Without an answer at the address, Find Bay's search runs once for this click.
    private async Task ConnectAsync(bool searchIfUnreachable)
    {
        var host = _hostFor().Trim();
        if (host.Length == 0)
        {
            Status = PageStatus.NoAddress;
            return;
        }

        SetConnecting(true);
        SetEmptyNote("");
        Status = PageStatus.Busy("Connecting to Loadout Bay", host);
        var otherVersion = false;
        var unreachable = false;
        try
        {
            var reading = new Progress<BayHello>(hello =>
                Status = PageStatus.Busy("Reading the game list", "Connected to Loadout Bay " + hello.Version + " at " + host + "."));
            var library = await TitleIncompleteAsync(await BayLibrary.ReadAsync(host, connected: reading));
            ShowRead(library, host);
            _onRead(library);
            otherVersion = library.Hello.Version != ProductInfo.Version;
            await ReadCoversAsync(host);
        }
        catch (LoadoutBayException ex)
        {
            SetConnected(false);
            Status = PageStatus.Error(
                ex.Status == 0 ? "Couldn't reach Loadout Bay at " + host
                    : ex.Status == BayWire.StatusProtocolMismatch ? "Loadout Bay at " + host + " is another version"
                    : "Couldn't read the game list",
                Games.Count > 0 ? ex.Message + " The list is from before." : ex.Message);
            otherVersion = ex.Status == BayWire.StatusProtocolMismatch;
            unreachable = ex.Unreachable;
        }
        finally
        {
            SetConnecting(false);
        }

        _log.Add(Status.LogLine);
        if (unreachable && searchIfUnreachable)
        {
            await SearchAfterNoAnswerAsync(host);
            return;
        }

        if (otherVersion && _bundledBay() is { } elf && _confirm(UpdateQuestion()))
        {
            await UpdateBayAsync(host, elf);
        }
    }

    private async Task SearchAfterNoAnswerAsync(string host)
    {
        var failed = Status;
        const string Looking = "Looking for Loadout Bay on the network";
        var noAnswer = "No answer at " + host + ".";
        var run = new CancellationTokenSource();
        _searchRun = run;
        SetConnecting(true);
        Status = PageStatus.Busy(Looking, noAnswer);
        BaySearchResult? result = null;
        try
        {
            result = await BaySearch.RunAsync(
                note =>
                {
                    if (!run.IsCancellationRequested)
                    {
                        Status = PageStatus.Busy(Looking, noAnswer + " " + note);
                    }
                },
                run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
        }
        finally
        {
            _searchRun = null;
            run.Dispose();
            SetConnecting(false);
        }

        Status = failed;
        if (result is null)
        {
            _log.Add("The search for Loadout Bay was cancelled.");
            return;
        }

        if (result.Found.Any(bay => BaySearch.IsAt(bay, host)))
        {
            // It was still starting: it answers the search at the same address, so Connect tries once more.
            _log.Add("Loadout Bay answered the search at " + host + ". Connecting again.");
            await ConnectAsync(searchIfUnreachable: false);
            return;
        }

        if (result.Found.Count == 0)
        {
            _log.Add(Join("The search found no Loadout Bay on the network.", result.CutNote));
            return;
        }

        _log.Add("The search found " + string.Join(", ", result.Found.Select(BaySearch.Line)) + ".");
        var chosen = result.Found.Count == 1
            ? _confirm(MovedQuestion(host, result.Found[0])) ? result.Found[0] : null
            : _choose(ChooseBayQuestion(result.Found, "None answered at " + host + ". Choose one:", "_Keep " + host)) is { } index
                ? result.Found[index]
                : null;
        if (chosen is not null)
        {
            _useBay(chosen);
            await ConnectAsync(searchIfUnreachable: false);
        }
    }

    // Asked when the search finds Loadout Bay at another address than the one in Settings.
    private static Question MovedQuestion(string host, FoundBay bay) =>
        new("Loadout Bay is at " + bay.Address,
            "It didn't answer at " + host + ", the address in Settings. Loadout Bay " + bay.Version + " answered at " + bay.Address + ".",
            "_Use " + bay.Address,
            "_Keep " + host);

    private static Question ChooseBayQuestion(IReadOnlyList<FoundBay> found, string body, string cancelLabel) =>
        new(string.Create(CultureInfo.CurrentCulture, $"Found {found.Count} Loadout Bays"),
            body,
            "_Use this one",
            cancelLabel,
            Items: found.Select(BaySearch.Line).ToList(),
            ChooseItem: true);

    // The empty state's Find Bay: as in Settings, but what it finds goes in the empty state's detail line.
    private async Task FindBayAsync()
    {
        if (!CanFind)
        {
            return;
        }

        SetFinding(true);
        SetEmptyNote(BaySearch.Asking);
        BaySearchResult result;
        try
        {
            result = await BaySearch.RunAsync(SetEmptyNote, CancellationToken.None);
        }
        finally
        {
            SetFinding(false);
        }

        var chosen = result.Found.Count switch
        {
            0 => null,
            1 => result.Found[0],
            _ => _choose(ChooseBayQuestion(result.Found, "Choose the one Loadout connects to:", "_Cancel")) is { } index ? result.Found[index] : null,
        };
        if (chosen is not null)
        {
            _useBay(chosen);
        }

        SetEmptyNote(
            result.NoNetwork ? BaySearch.NoNetwork
            : chosen is not null ? "Found Loadout Bay " + chosen.Version + " at " + chosen.Address + ". Connect reads its games."
            : result.Found.Count > 0 ? "Start Loadout Bay on the PS2, then Connect."
            : Join(
                "No Loadout Bay answered. Check that it runs on the PS2, that the PS2 and this PC are on the same network, and that Windows Firewall lets Loadout through.",
                result.CutNote));
        _log.Add("Find Bay: " + EmptyDetail);
    }

    private void SetEmptyNote(string note)
    {
        _emptyNote = note;
        OnPropertyChanged(nameof(EmptyDetail));
    }

    private void SetFinding(bool value)
    {
        _finding = value;
        OnPropertyChanged(nameof(IsFinding));
        OnPropertyChanged(nameof(CanFind));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(FindLabel));
        OnPropertyChanged(nameof(FindStatus));
    }

    private static Question UpdateQuestion() =>
        new("Update Loadout Bay on the PS2?",
            "The Loadout Bay on the PS2 is not the one that comes with this Loadout (version " + ProductInfo.Version + "). "
            + "The new one starts on the PS2 at once and replaces loadout-bay.elf on the USB drive or hard disk it was started from, "
            + "keeping the old file as loadout-bay.elf.bak.",
            "_Send version " + ProductInfo.Version,
            "_Not now");

    private async Task UpdateBayAsync(string host, byte[] elf)
    {
        SetConnecting(true);
        const string Updating = "Updating Loadout Bay";
        var stages = new Progress<BayUpgradeStage>(stage => Status = PageStatus.Busy(Updating, stage switch
        {
            BayUpgradeStage.Sending => "Sending " + ProductInfo.Version + " to the PS2…",
            BayUpgradeStage.Starting => "Starting it on the PS2…",
            BayUpgradeStage.WaitingForRestart => "Waiting for it to start…",
            _ => "Saving it where the PS2 started the old one from…",
        }));
        Status = PageStatus.Busy(Updating, "Sending " + ProductInfo.Version + " to the PS2…");
        PageStatus? notSaved = null;
        try
        {
            var result = await BayUpgrade.RunAsync(host, elf, save: true, stages);
            Status = result.Saved
                ? PageStatus.Ok("Loadout Bay " + result.Version + " runs on the PS2", "It is saved where it was started from; the old file is loadout-bay.elf.bak.")
                : PageStatus.Warn(
                    "Loadout Bay " + result.Version + " runs, but wasn't saved",
                    "It wasn't started from a USB drive or the hard disk, so the PS2 starts the old one next time.");
            notSaved = result.Saved ? null : Status;
        }
        catch (LoadoutBayException ex)
        {
            Status = PageStatus.Error("Loadout Bay wasn't updated", ex.Message);
        }
        finally
        {
            SetConnecting(false);
        }

        _log.Add(Status.LogLine);
        if (Status.Kind != StatusKind.Error)
        {
            await ConnectAsync();
            // The list is read again, but that the new one wasn't saved matters more.
            if (notSaved is not null && Status.Kind == StatusKind.Ok)
            {
                Status = notSaved;
            }
        }
    }

    private static Question PutBayOnDiskQuestion() =>
        new("Put Loadout Bay on the hard disk?",
            "This Loadout's Loadout Bay (version " + ProductInfo.Version + ") goes in APPS/Loadout-Bay in OPL's partition, replacing a copy already there. "
            + "OPL lists it under Apps once its Applications start mode is on, so it starts without the USB drive. "
            + "Without a loadout-bay.cfg beside it, it takes its address over DHCP.",
            "_Put it on the hard disk",
            "_Cancel");

    private async Task PutBayOnDiskAsync()
    {
        var host = _hostFor().Trim();
        if (host.Length == 0)
        {
            Status = PageStatus.NoAddress;
            return;
        }

        if (_connecting)
        {
            return;
        }

        if (_bundledBay() is not { } elf)
        {
            Status = PageStatus.Warn("This build doesn't include Loadout Bay", "Build ps2/ first and then this app, or use a release download.");
            return;
        }

        if (!_confirm(PutBayOnDiskQuestion()))
        {
            return;
        }

        SetConnecting(true);
        Status = PageStatus.Busy("Putting Loadout Bay on the hard disk", "Version " + ProductInfo.Version + " goes in APPS/Loadout-Bay.");
        try
        {
            await BayUpgrade.PutOnHardDiskAsync(host, elf);
            Status = PageStatus.Ok("Loadout Bay " + ProductInfo.Version + " is on the hard disk", "Start it from OPL's Apps menu. It is in APPS/Loadout-Bay.");
        }
        catch (LoadoutBayException ex)
        {
            Status = PageStatus.Error("Loadout Bay wasn't put on the hard disk", ex.Message);
        }
        finally
        {
            SetConnecting(false);
        }

        _log.Add(Status.LogLine);
    }

    private async Task RemoveAsync()
    {
        var games = _selection;
        var host = _hostFor().Trim();
        if (!CanRemove || host.Length == 0 || !_confirm(RemoveQuestion(games)))
        {
            return;
        }

        SetConnecting(true);
        Status = PageStatus.Busy(games.Length == 1 ? "Removing " + games[0].Title : "Removing " + Count(games.Length));
        var removed = 0;
        var wholeGames = new List<InstalledGameRow>();
        try
        {
            var removal = await BayGameChanges.RemoveAsync(host, games);
            removed = removal.Removed.Count;
            wholeGames.AddRange(removal.Removed.Where(game => !game.Incomplete));
            foreach (var game in removal.Removed)
            {
                _log.Add("Removed " + Describe(game) + " (" + game.PartitionName + ") from the PS2's hard disk.");
            }

            foreach (var failure in removal.Failed)
            {
                _log.Add(failure.Game.Title + " was not removed. " + failure.Reason);
            }

            Status = removal.Failed.Count == 0
                ? PageStatus.Ok("Removed " + Count(removed))
                : PageStatus.Warn(
                    "Removed " + removed.ToString(CultureInfo.CurrentCulture) + " of " + Count(games.Length),
                    "Not removed: " + string.Join(", ", removal.Failed.Select(failure => failure.Game.Title)) + ". Logs has the reasons.");
        }
        catch (LoadoutBayException ex)
        {
            Status = PageStatus.Error("Nothing was removed", ex.Message);
            _log.Add(Status.LogLine);
        }
        finally
        {
            SetConnecting(false);
        }

        if (wholeGames.Count > 0)
        {
            if (_confirm(CleanUpQuestion(wholeGames)))
            {
                await CleanUpAsync(host, wholeGames);
            }
            else
            {
                Status = Status with { Detail = Join(Status.Detail, (wholeGames.Count == 1 ? "Its" : "Their") + " artwork and OPL settings stay on the PS2.") };
            }
        }

        // The list is read again, so it shows what is on the disk now. The removal stays the news unless that read fails.
        if (removed > 0)
        {
            var status = Status;
            await ConnectAsync();
            if (Status.Kind == StatusKind.Ok)
            {
                Status = status;
            }
        }
    }

    private static string Join(string detail, string more) => detail.Length == 0 ? more : detail + " " + more;

    private static Question CleanUpQuestion(List<InstalledGameRow> games) =>
        new(games.Count == 1 ? "Also delete the artwork and OPL settings of " + games[0].Title + "?" : "Also delete their artwork and OPL settings?",
            "They stay in OPL's ART and CFG folders after a game is removed, and are used again if the game is installed again.",
            "_Delete them",
            "_Leave them on the PS2",
            Destructive: true,
            Items: games.Count == 1 ? null : games.Select(game => game.Title).ToList());

    // Deletes the removed games' files in OPL's partition. A failure is reported, and the games stay removed.
    private async Task CleanUpAsync(string host, List<InstalledGameRow> games)
    {
        SetConnecting(true);
        var status = Status;
        Status = PageStatus.Busy("Deleting their artwork and OPL settings", Count(games.Count));
        try
        {
            var discs = games.Select(game => DiscId.TryParse(game.DiscId, out var discId) ? discId : (DiscId?)null).OfType<DiscId>().ToList();
            var removal = await BayGameChanges.RemoveOplFilesAsync(host, discs);
            foreach (var name in removal.Deleted)
            {
                _log.Add("Deleted " + name + " from OPL's partition.");
            }

            foreach (var (name, reason) in removal.Failed)
            {
                _log.Add(name + " was not deleted. " + reason);
            }

            var deleted = string.Create(CultureInfo.CurrentCulture, $"Deleted {removal.Deleted.Count} artwork and settings files.");
            Status = removal.Failed.Count == 0
                ? status with { Detail = Join(status.Detail, deleted) }
                : new PageStatus(StatusKind.Warn, status.Headline, Join(status.Detail, deleted + " Some could not be deleted: see Logs."));
        }
        catch (LoadoutBayException ex)
        {
            Status = new PageStatus(StatusKind.Warn, status.Headline, Join(status.Detail, "Their artwork and OPL settings were not deleted. " + ex.Message));
            _log.Add(Status.LogLine);
        }
        finally
        {
            SetConnecting(false);
        }
    }

    private static Question RemoveQuestion(InstalledGameRow[] games)
    {
        var one = games.Length == 1;
        return new(
            one ? "Remove " + Describe(games[0]) + " from the PS2?" : "Remove " + Count(games.Length) + " from the PS2?",
            (one ? "Its" : "Their") + " partitions are deleted and the space is freed. This can't be undone: to play a game again, install it again."
            + (games.Any(game => !game.Incomplete) ? " Loadout then asks whether to delete " + (one ? "its" : "their") + " artwork and OPL settings too." : ""),
            one ? "_Remove the game" : "_Remove " + Count(games.Length),
            one ? "_Keep it" : "_Keep them",
            QuestionKind.Warn,
            Destructive: true,
            Items: one ? null : games.Select(Describe).ToList());
    }

    private static string Describe(InstalledGameRow game) => game.Incomplete ? "the incomplete install of " + game.Title : game.Title;

    private async Task SaveEditAsync()
    {
        var game = _selected;
        var host = _hostFor().Trim();
        if (game is null || !CanSave || host.Length == 0)
        {
            return;
        }

        var title = TitleText.FitFullTitle(EditorTitle.Trim());
        var discType = EditorDiscType;
        var settings = _settings;
        var modes = EditorModes();
        var dma = EditorDma;
        var headerChanged = title != game.Title || discType != game.DiscType;
        var settingsChanged = settings is not null && (modes != settings.CompatibilityModes || dma != settings.Dma);

        // Only what the user changed is written over the header as the PS2 holds it now.
        BayGameEdit Change(BayGameEdit edit)
        {
            edit = title == game.Title ? edit : edit with { Title = title };
            return discType == game.DiscType ? edit : edit with { DiscType = discType };
        }

        SetConnecting(true);
        Status = PageStatus.Busy("Saving the changes to " + game.Title);
        var saved = false;
        try
        {
            if (headerChanged)
            {
                var row = await BayGameChanges.EditAsync(host, game.PartitionName, Change);
                saved = true;
                _log.Add("Saved the details of " + row.Title + " (" + row.PartitionName + "): disc type " + row.DiscTypeLabel + ".");
            }

            if (settingsChanged && DiscId.TryParse(game.DiscId, out var discId))
            {
                var written = await BayGameChanges.SaveSettingsAsync(host, discId, modes, dma);
                _settings = written;
                saved = true;
                _log.Add("Saved the OPL settings of " + game.Title + " (" + OplGameSettings.FileName(discId) + "): modes "
                    + ModesLabel(written.CompatibilityModes) + ", transfer " + OplGameSettings.DmaLabels[written.Dma] + ".");
            }

            SetEditStatus("Saved " + title + ".", failed: false);
        }
        catch (LoadoutBayException ex)
        {
            SetEditStatus("Not saved. " + ex.Message, failed: true);
            Status = PageStatus.Error("Couldn't save the changes to " + game.Title, ex.Message);
            _log.Add(game.Title + " was not saved. " + ex.Message);
        }
        finally
        {
            SetConnecting(false);
        }

        if (saved)
        {
            var (status, failed) = (_editStatus, _editFailed);
            await ConnectAsync();
            SelectedGame = Games.FirstOrDefault(row => row.PartitionName == game.PartitionName);
            SetEditStatus(status, failed);
        }
    }

    private void Revert()
    {
        if (_selected is not { } game)
        {
            return;
        }

        EditorTitle = game.Title;
        EditorDiscType = game.DiscType;
        ShowSettings(_settings);
        SetEditStatus("", failed: false);
    }

    private void LoadEditor(InstalledGameRow? game)
    {
        EditorTitle = game?.Title ?? "";
        DiscTypeChoices = DiscTypeChoicesFor(game?.DiscType);
        EditorDiscType = game?.DiscType ?? HdlDiscTypes.Ps2Dvd;
        SetEditStatus("", failed: false);
        ShowSettings(null);
        SetSettingsStatus("", failed: false);
        IsReadingSettings = false;
        if (game is not null && BayGameChanges.CanEdit(game) && DiscId.TryParse(game.DiscId, out var discId) && _hostFor().Trim().Length > 0)
        {
            _ = ReadSettingsAsync(discId);
        }
    }

    // Reads the selected game's settings file. A read that a newer selection overtook is dropped.
    private async Task ReadSettingsAsync(DiscId discId)
    {
        var read = ++_settingsRead;
        IsReadingSettings = true;
        SetSettingsStatus("Reading OPL's settings…", failed: false);
        try
        {
            var settings = await BayGameChanges.ReadSettingsAsync(_hostFor().Trim(), discId);
            if (read == _settingsRead)
            {
                ShowSettings(settings);
                SetSettingsStatus(
                    ReferenceEquals(settings, OplGameSettings.Empty)
                        ? "OPL has no settings for this game yet: no modes, UDMA 4."
                        : "OPL's settings for this game, from " + OplGameSettings.FileName(discId) + ".",
                    failed: false);
            }
        }
        catch (LoadoutBayException ex)
        {
            if (read == _settingsRead)
            {
                SetSettingsStatus("OPL's settings were not read, so modes and transfer mode can't be changed. " + ex.Message, failed: true);
            }
        }
        finally
        {
            if (read == _settingsRead)
            {
                IsReadingSettings = false;
            }
        }
    }

    private void ShowSettings(OplGameSettings? settings)
    {
        _settings = settings;
        for (var i = 0; i < CompatibilityModes.Count; i++)
        {
            CompatibilityModes[i].IsSet = settings is not null && (settings.CompatibilityModes & (1 << i)) != 0;
        }

        EditorDma = settings?.Dma ?? OplGameSettings.DefaultDma;
        OnPropertyChanged(nameof(CanEditSettings));
        OnEdited();
    }

    private void SetEditStatus(string text, bool failed)
    {
        _editStatus = text;
        _editFailed = failed;
        OnPropertyChanged(nameof(DetailsHelp));
        OnPropertyChanged(nameof(DetailsHelpIsError));
    }

    private void SetSettingsStatus(string text, bool failed)
    {
        _settingsStatus = text;
        _settingsFailed = failed;
        OnPropertyChanged(nameof(DetailsHelp));
        OnPropertyChanged(nameof(DetailsHelpIsError));
    }

    private byte EditorModes()
    {
        var modes = 0;
        for (var i = 0; i < CompatibilityModes.Count; i++)
        {
            modes |= CompatibilityModes[i].IsSet ? 1 << i : 0;
        }

        return (byte)modes;
    }

    private static string ModesLabel(byte modes) => modes == 0 ? "none" : InstalledGameRow.Modes(modes);

    // CD, CDDA, and DVD, plus the game's own disc type when it is none of those, so saving can keep it.
    private static DiscTypeChoice[] DiscTypeChoicesFor(byte? current)
    {
        DiscTypeChoice[] choices =
        [
            new(HdlDiscTypes.Ps2Cd, "CD"),
            new(HdlDiscTypes.Ps2CdDa, "CDDA"),
            new(HdlDiscTypes.Ps2Dvd, "DVD"),
        ];
        return current is byte value && choices.All(choice => choice.Value != value)
            ? [new DiscTypeChoice(value, "Current (0x" + value.ToString("X2", CultureInfo.InvariantCulture) + ")"), .. choices]
            : choices;
    }

    /// <summary>
    /// The library with a title for each incomplete install, which has no header to give one:
    /// the title database's, or what its partition name keeps of it.
    /// </summary>
    public async Task<BayLibrary> TitleIncompleteAsync(BayLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.Games.Any(game => game.Incomplete)
            ? library with { Games = await IncompleteTitleLookup.ApplyAsync(library.Games, _titles) }
            : library;
    }

    /// <summary>Shows a list just read from Loadout Bay, by Connect or after an install.</summary>
    public void ShowRead(BayLibrary library, string host)
    {
        ArgumentNullException.ThrowIfNull(library);
        SetConnected(true);
        ShowGames(library.Games);
        var free = Gb(library.Space.FreeBytes);
        var total = Gb(library.Space.TotalBytes);
        Status = PageStatus.Ok(
            Count(library.Games.Count) + " on the PS2",
            "Loadout Bay " + library.Hello.Version + " at " + host + " · " + free + " of " + total + " GB free");
        Summary = Count(library.Games.Count).ToUpper(CultureInfo.CurrentCulture) + " · " + free + " GB FREE OF " + total + " GB";
    }

    private void SetConnecting(bool value)
    {
        _connecting = value;
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanFind));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(CanRemove));
        OnEditorChanged();
    }

    private void SetConnected(bool value)
    {
        _connected = value;
        OnPropertyChanged(nameof(ConnectLabel));
        OnListChanged();
    }

    private void OnListChanged()
    {
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(NoGamesOnPs2));
        OnPropertyChanged(nameof(EmptyHeadline));
        OnPropertyChanged(nameof(EmptyDetail));
    }

    private void OnEditorChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(EditorHint));
        OnEdited();
    }

    private void OnEdited()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
    }

    private static string Count(int games) => games == 1 ? "1 game" : games.ToString(CultureInfo.CurrentCulture) + " games";

    private static string Gb(ulong bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture);
}

/// <summary>One of OPL's compatibility modes, a check box in the editor labelled with its number.</summary>
public sealed class CompatibilityModeChoice : ObservableObject
{
    private bool _isSet;

    public CompatibilityModeChoice(int number)
    {
        Label = number.ToString(CultureInfo.CurrentCulture);
        Name = "Mode " + Label;
    }

    public string Label { get; }

    /// <summary>"Mode 3", for its tooltip and for UI Automation.</summary>
    public string Name { get; }

    public bool IsSet
    {
        get => _isSet;
        set => SetField(ref _isSet, value);
    }
}

public sealed record DiscTypeChoice(byte Value, string Label);
