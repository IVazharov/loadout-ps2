using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Loadout.Artwork;
using Loadout.Bay;
using Loadout.Core;
using Loadout.Images;
using Loadout.Library;
using Loadout.Metadata;
using Loadout.Vmc;

namespace Loadout.App;

/// <summary>
/// The Install page: scans disc images into a local queue, then installs the Ready ones on the
/// PS2 through Loadout Bay at the address in Settings. Games already on the PS2, by the saved
/// installed-game list, are marked; the install reads the PS2's list again and skips them. With
/// memory cards chosen, each game installed gets a virtual memory card in OPL's VMC folder; a card
/// that is already there is kept unless the user chooses, twice, to replace it.
/// </summary>
public sealed class InstallViewModel : ObservableObject, IDisposable
{
    private readonly DiscScanOptions _options;
    private readonly InstalledGameIndex _installed = new();
    private readonly InstalledGameIndex _incomplete = new();
    private readonly List<QueuePreviewItem> _scanned = new();
    private readonly OperationLog _log;
    private readonly Func<string> _hostFor;
    private readonly Func<BayLibrary, Task> _onLibraryRead;
    private readonly IDiscTitleCatalog _titles;
    private readonly Func<Question, bool> _confirm;
    private readonly ArtworkCache _artworkCache;
    private readonly ArtworkDownloader _artworkDownloader;
    private QueuePreviewItem[] _selection = [];
    private bool _sendArtwork = true;
    private bool _createMemoryCards;
    private int _memoryCardSize = VmcImage.DefaultSize;
    private CancellationTokenSource? _install;
    private string _cacheDirectory;
    private CancellationTokenSource? _scan;
    private int _scansRunning;
    private int _generation;
    private PageStatus _status;
    private BayFreeSpace? _space;
    private int _spaceReading;
    private ulong _sentSinceReading;

    public InstallViewModel(
        DiscScanOptions options,
        OperationLog log,
        Func<string> hostFor,
        Func<BayLibrary, Task> onLibraryRead,
        IDiscTitleCatalog titles,
        Func<Question, bool> confirm,
        ArtworkCache artworkCache,
        ArtworkDownloader artworkDownloader)
    {
        _artworkCache = artworkCache ?? throw new ArgumentNullException(nameof(artworkCache));
        _artworkDownloader = artworkDownloader ?? throw new ArgumentNullException(nameof(artworkDownloader));
        _titles = titles ?? throw new ArgumentNullException(nameof(titles));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _hostFor = hostFor ?? throw new ArgumentNullException(nameof(hostFor));
        _onLibraryRead = onLibraryRead ?? throw new ArgumentNullException(nameof(onLibraryRead));
        _cacheDirectory = options.CacheDirectory;
        Items = new ObservableCollection<QueuePreviewItem>();
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, OnScanFailed);
        AddFolderCommand = new AsyncRelayCommand(() => AddFolderAsync(recursive: false), OnScanFailed);
        AddFolderRecursiveCommand = new AsyncRelayCommand(() => AddFolderAsync(recursive: true), OnScanFailed);
        CancelCommand = new RelayCommand(Cancel, () => _install is not null || _scansRunning > 0);
        RemoveCommand = new RelayCommand(RemoveSelected);
        InstallCommand = new AsyncRelayCommand(InstallAsync, OnInstallFailed);
        _status = QueueStatus();
    }

    public ObservableCollection<QueuePreviewItem> Items { get; }

    public AsyncRelayCommand AddFilesCommand { get; }

    public AsyncRelayCommand AddFolderCommand { get; }

    public AsyncRelayCommand AddFolderRecursiveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public ICommand RemoveCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    /// <summary>True while the queue is being installed on the PS2.</summary>
    public bool IsInstalling => _install is not null;

    /// <summary>True when nothing is being installed and the queue holds a game that is ready to install.</summary>
    public bool CanInstall => _install is null && Items.Any(item => item.Status == QueueItemStatus.Ready);

    /// <summary>True while a scan or an install runs, when Cancel shows beside Install.</summary>
    public bool IsWorking => _install is not null || _scansRunning > 0;

    /// <summary>True while nothing is queued, when the page shows its empty state instead of the table.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The page header's summary, such as "5 IN QUEUE · 3 READY · 3.8 GB OF 17.9 GB FREE".</summary>
    public string Summary => InstallQueueSummary.Describe(Items, _space, _sentSinceReading);

    public bool HasSelection => _selection.Length > 0;

    public string CacheDirectory
    {
        get => _cacheDirectory;
        set => SetField(ref _cacheDirectory, value);
    }

    /// <summary>
    /// When set, Loadout Bay reads each game back from its disk and compares it with what was sent
    /// before the game counts as installed. Slower, and can be turned off in Settings.
    /// </summary>
    public bool CheckInstalls { get; set; }

    /// <summary>When set, each game's artwork is downloaded first and sent once the game is installed. A game without any still installs.</summary>
    public bool SendArtwork
    {
        get => _sendArtwork;
        set => SetField(ref _sendArtwork, value);
    }

    /// <summary>When set, each game installed gets a virtual memory card in slot 1.</summary>
    public bool CreateMemoryCards
    {
        get => _createMemoryCards;
        set => SetField(ref _createMemoryCards, value);
    }

    public IReadOnlyList<int> MemoryCardSizes { get; } = VmcImage.Sizes;

    public int MemoryCardSize
    {
        get => _memoryCardSize;
        set => SetField(ref _memoryCardSize, value);
    }

    public PageStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>Cancels a scan that is still running and releases the commands.</summary>
    public void Dispose()
    {
        _scan?.Cancel();
        _scan?.Dispose();
        _scan = null;
        _install?.Cancel();
        AddFilesCommand.Dispose();
        AddFolderCommand.Dispose();
        AddFolderRecursiveCommand.Dispose();
        InstallCommand.Dispose();
    }

    public void SetSelection(IReadOnlyList<QueuePreviewItem> selected)
    {
        _selection = selected.Count == 0 ? [] : selected.ToArray();
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Takes the PS2's list, and its free space when it comes from a Connect rather than the saved list.</summary>
    public void ReplaceInstalled(IReadOnlyList<InstalledGameRow> games, BayFreeSpace? space)
    {
        _space = space;
        _spaceReading++;
        _sentSinceReading = 0;
        _installed.Clear();
        _incomplete.Clear();
        // An incomplete install is not a game on the PS2: its image reads as failed, saying to remove it first.
        foreach (var game in games)
        {
            (game.Incomplete ? _incomplete : _installed).TryAdd(game.DiscId, game.PartitionName);
        }

        Publish();
        if (!IsWorking && Status.Kind == StatusKind.Idle)
        {
            Status = QueueStatus();
        }
    }

    /// <summary>What the queue holds, for when nothing runs. The detail says what just happened, if anything.</summary>
    private PageStatus QueueStatus(string? detail = null)
    {
        var ready = Items.Count(item => item.Status == QueueItemStatus.Ready);
        if (Items.Count == 0)
        {
            return PageStatus.Idle("Nothing to install yet", detail ?? "Add disc images: iso, bin, cue or chd.");
        }

        if (Items.All(item => item.Status == QueueItemStatus.Installed))
        {
            return PageStatus.Idle(Items.Count == 1 ? "The game in the queue is installed" : "Every game in the queue is installed", detail ?? "Add disc images: iso, bin, cue or chd.");
        }

        if (ready == 0)
        {
            return PageStatus.Warn(
                Items.Count == 1 ? "The game in the queue isn't ready to install" : string.Create(CultureInfo.CurrentCulture, $"None of the {Items.Count} games is ready to install"),
                detail ?? "The Status and Detail columns say why.");
        }

        var host = _hostFor().Trim();
        return PageStatus.Idle(
            string.Create(CultureInfo.CurrentCulture, $"{ready} of {Items.Count} ready to install"),
            detail ?? (host.Length == 0 ? "Set the PS2's address in Settings first." : "Install sends them to Loadout Bay at " + host + "."));
    }

    private void SetWorking()
    {
        OnPropertyChanged(nameof(IsWorking));
        CancelCommand.RaiseCanExecuteChanged();
    }

    private async Task AddFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select disc images",
            Filter = "Disc images (*.iso;*.bin;*.cue;*.chd)|*.iso;*.bin;*.cue;*.chd|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ScanAsync(dialog.FileNames.Select(path => new ScanInput(path, ScanDepth.File)).ToArray());
    }

    private async Task AddFolderAsync(bool recursive)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = recursive ? "Select a folder to scan recursively" : "Select a folder of disc images",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var depth = recursive ? ScanDepth.DirectoryRecursive : ScanDepth.Directory;
        await ScanAsync([new ScanInput(dialog.FolderName, depth)]);
    }

    // Cancel is enabled while any scan runs: a new scan cancels the one before it, which still has to finish.
    private async Task ScanAsync(IReadOnlyList<ScanInput> inputs)
    {
        _scansRunning++;
        SetWorking();
        try
        {
            await ScanQueueAsync(inputs);
        }
        finally
        {
            _scansRunning--;
            SetWorking();
        }
    }

    private async Task ScanQueueAsync(IReadOnlyList<ScanInput> inputs)
    {
        var generation = ++_generation;
        _scan?.Cancel();
        _scan?.Dispose();
        _scan = new CancellationTokenSource();
        var token = _scan.Token;
        Status = PageStatus.Busy("Scanning", inputs.Count == 1 ? inputs[0].Path : inputs.Count.ToString(CultureInfo.CurrentCulture) + " files");
        var cacheDirectory = string.IsNullOrWhiteSpace(CacheDirectory) ? _options.CacheDirectory : CacheDirectory.Trim();
        var options = new DiscScanOptions(cacheDirectory)
        {
            Queued =new HashSet<string>(_scanned.Select(item => item.SourceFile), StringComparer.OrdinalIgnoreCase),
        };
        // On a worker thread: without a chd to convert, a scan has nothing to wait for and would hold the window.
        // It gets no list of installed games, which a connect can change meanwhile; WithoutDuplicates and Publish
        // below compare against it here on the UI thread.
        var result = await Task.Run(() => DiscScanner.ScanAsync(inputs, null, options, cancellationToken: token));
        if (generation != _generation)
        {
            return;
        }

        if (token.IsCancellationRequested && result.Items.Count == 0)
        {
            Status = PageStatus.Warn("Scan cancelled", "Nothing was added to the queue.");
            return;
        }

        // The disc gives only its volume label, so each title comes from the OPL CFG database
        // when it knows the disc ID. That title is what an install writes to the PS2.
        var items = result.Items;
        if (!token.IsCancellationRequested)
        {
            Status = PageStatus.Busy("Looking up titles", "In OPL's list of game titles, by disc ID.");
            try
            {
                // Also on a worker thread: titles already cached never wait, so the whole list would hold the window.
                var scanned = items;
                items = await Task.Run(() => QueueTitleLookup.ApplyAsync(scanned, _titles, token));
            }
            catch (OperationCanceledException)
            {
                // Cancelled while looking up: the titles read from the discs stay.
            }

            if (generation != _generation)
            {
                return;
            }
        }

        // A disc already in the queue or on the PS2 would only show as a duplicate that never installs.
        var (kept, leftOut) = DiscScanner.WithoutDuplicates(_scanned, items, _installed);
        _scanned.AddRange(kept);
        Publish();
        var added = new List<string> { kept.Count == 1 ? "Added 1 image" : "Added " + kept.Count.ToString(CultureInfo.CurrentCulture) + " images" };
        var left = result.AlreadyQueued.Count + leftOut.Count;
        if (left > 0)
        {
            added.Add("left out " + left.ToString(CultureInfo.CurrentCulture) + " already in the queue or on the PS2");
        }

        if (result.LeftoverTemporaryFiles.Count > 0)
        {
            added.Add(result.LeftoverTemporaryFiles.Count.ToString(CultureInfo.CurrentCulture) + " temporary files were left over");
        }

        var detail = string.Join(", ", added) + ".";
        Status = result.Cancelled ? PageStatus.Warn("Scan cancelled", detail) : QueueStatus(detail);
        _log.Add(Status.LogLine);
        foreach (var path in result.AlreadyQueued)
        {
            _log.Add("Left out " + path + ": it is already in the queue.");
        }

        foreach (var item in leftOut)
        {
            _log.Add("Left out " + item.SourceFile + ": " + item.DetectedTitle
                + (item.DuplicateStatus == DuplicateMatch.InLibrary ? " is already on the PS2." : " is already in the queue."));
        }

        foreach (var item in Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                _log.Add(item.Detail);
            }
        }
    }

    private void Publish()
    {
        var rows = GameListSort.ByTitle(DiscScanner.ApplyDuplicates(_scanned, _installed, _incomplete), row => row.DetectedTitle);
        if (rows.Select(row => row.SourceFile).SequenceEqual(Items.Select(item => item.SourceFile)))
        {
            // The same games, so only the rows that changed: a batch moves on while the user may have scrolled the list.
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i] != Items[i])
                {
                    Items[i] = rows[i];
                }
            }
        }
        else
        {
            Items.Clear();
            foreach (var row in rows)
            {
                Items.Add(row);
            }
        }

        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    private async Task InstallAsync()
    {
        var host = _hostFor().Trim();
        if (host.Length == 0)
        {
            Status = PageStatus.NoAddress;
            return;
        }

        var queue = Items.Where(item => item.Status == QueueItemStatus.Ready).ToList();
        if (queue.Count == 0 || _install is not null)
        {
            return;
        }

        _install = new CancellationTokenSource();
        var reading = _spaceReading;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(CanInstall));
        SetWorking();
        Status = PageStatus.Busy("Connecting to Loadout Bay", host);
        var clock = new Stopwatch();
        var current = -1;
        var checking = false;
        var progress = new Progress<BayQueueProgress>(step =>
        {
            if (step.Index != current)
            {
                current = step.Index;
                checking = false;
                clock.Restart();
                MarkInstalling(step.Item, true);
            }

            // The last report after a check is not more sending: it would put the check's time in the speed.
            if (checking && !step.Status.Checking)
            {
                return;
            }

            checking = step.Status.Checking;
            ShowProgress(step, clock.Elapsed);
        });
        try
        {
            var cards = CreateMemoryCards ? await PlanMemoryCardsAsync(host, queue, _install.Token) : null;
            var artwork = SendArtwork ? _artworkCache : null;
            if (artwork is not null)
            {
                await DownloadArtworkAsync(queue, _install.Token);
            }

            var run = await BayQueueInstaller.RunAsync(
                host, queue, progress, CheckInstalls, memoryCards: cards, artwork: artwork, finished: new Progress<BayQueueResult>(result => ShowResult(result, reading)), cancellationToken: _install.Token);
            if (run.Library is not null)
            {
                await _onLibraryRead(run.Library);
            }

            Status = Finished(run.Results);
            if (run.Library is null)
            {
                // The install used some of the free space, and the new figure could not be read.
                _space = null;
                OnPropertyChanged(nameof(Summary));
                _log.Add(Status.LogLine);

                // When each game has its own result, only reading the list back failed: the results stay the news.
                Status = run.LostConnection
                    ? PageStatus.Error("Lost the connection to Loadout Bay", "Connect on Installed Games to see the PS2's list.")
                    : run.Results.All(result => result.Outcome == BayInstallOutcome.Installed)
                        ? PageStatus.Warn(Status.Headline, "Loadout Bay stopped answering afterwards. Connect on Installed Games to see the PS2's list.")
                        : PageStatus.Warn(Status.Headline, Status.Detail + " Then Loadout Bay stopped answering: connect on Installed Games to see the PS2's list.");
            }
        }
        finally
        {
            // A game's result clears its row. This is for a batch that ended without one.
            foreach (var item in queue)
            {
                MarkInstalling(item, false);
            }

            _install.Dispose();
            _install = null;
            OnPropertyChanged(nameof(IsInstalling));
            OnPropertyChanged(nameof(CanInstall));
            SetWorking();
        }

        _log.Add(Status.LogLine);
    }

    private void MarkInstalling(QueuePreviewItem item, bool installing)
    {
        var index = _scanned.FindIndex(row => row.SourceFile == item.SourceFile);
        if (index >= 0 && _scanned[index].Installing != installing)
        {
            _scanned[index] = _scanned[index] with { Installing = installing };
            Publish();
        }
    }

    // As each game finishes, so the rows and the Logs page follow the batch.
    /// <param name="reading">The free space reading the batch started from. A result that arrives after a newer reading is already counted in it.</param>
    private void ShowResult(BayQueueResult result, int reading)
    {
        _log.Add(result.Message);
        if (result.Outcome == BayInstallOutcome.Installed && reading == _spaceReading)
        {
            _sentSinceReading += (ulong)result.Item.InstallBytes;
            OnPropertyChanged(nameof(Summary));
        }

        var index = _scanned.FindIndex(row => row.SourceFile == result.Item.SourceFile);
        if (index < 0)
        {
            return;
        }

        var row = _scanned[index] with { Installing = false };
        if (result.Outcome == BayInstallOutcome.Installed)
        {
            row = row with { InstalledFromQueue = true };
            _installed.TryAdd(row.DiscId, result.PartitionName);
        }
        else if (result.Outcome is BayInstallOutcome.CheckFailed or BayInstallOutcome.Cancelled or BayInstallOutcome.IncompleteOnPs2)
        {
            // Loadout Bay left an incomplete install, so the row says to remove it before the list is read again.
            _incomplete.TryAdd(row.DiscId, null);
        }

        _scanned[index] = row;
        Publish();
    }

    private async Task DownloadArtworkAsync(List<QueuePreviewItem> queue, CancellationToken cancellationToken)
    {
        for (var i = 0; i < queue.Count; i++)
        {
            if (DiscId.TryParse(queue[i].DiscId, out var discId))
            {
                Status = PageStatus.Busy(
                    "Downloading artwork",
                    string.Create(CultureInfo.CurrentCulture, $"{queue[i].DetectedTitle} ({i + 1} of {queue.Count})"),
                    i * 100.0 / queue.Count);
                await _artworkDownloader.CollectAsync(discId, cancellationToken);
            }
        }
    }

    // Reads which games already have a card before anything is copied, so the user decides about those
    // once. Keeping them is the answer unless the user chooses Replace and then confirms it.
    private async Task<MemoryCardRequest> PlanMemoryCardsAsync(string host, IReadOnlyList<QueuePreviewItem> queue, CancellationToken cancellationToken)
    {
        Status = PageStatus.Busy("Reading the memory cards on the PS2", "So that a card already there keeps its saves unless you choose otherwise.");
        var plans = await BayMemoryCards.PlanAsync(host, queue, cancellationToken: cancellationToken);
        var there = plans.Where(plan => plan.State == MemoryCardState.CardThere).ToList();
        var replace = there.Count > 0
            && _confirm(KeepCardsQuestion(there, queue))
            && _confirm(ReplaceCardsQuestion(there, queue));
        if (there.Count > 0)
        {
            _log.Add(replace
                ? "The install replaces " + Cards(there.Count) + " already on the PS2 with empty ones."
                : "The install keeps " + Cards(there.Count) + " already on the PS2, with their saves.");
        }

        return new MemoryCardRequest(MemoryCardSize, plans, replace);
    }

    // Replace is not destructive yet (the next question confirms it), but it leads there, so Keep is the default.
    private static Question KeepCardsQuestion(List<MemoryCardPlan> there, IReadOnlyList<QueuePreviewItem> queue) =>
        there.Count == 1
            ? new("A game already has a memory card on the PS2",
                "The install can keep it, with the saves on it, for the game in the queue, or replace it with an empty card.",
                "_Replace with an empty card…",
                "_Keep card and saves",
                Destructive: true,
                Items: CardList(there, queue))
            : new(there.Count.ToString(CultureInfo.CurrentCulture) + " games already have a memory card on the PS2",
                "The install can keep them, with the saves on them, for the games in the queue, or replace them with empty cards.",
                "_Replace with empty cards…",
                "_Keep cards and saves",
                Destructive: true,
                Items: CardList(there, queue));

    private static Question ReplaceCardsQuestion(List<MemoryCardPlan> there, IReadOnlyList<QueuePreviewItem> queue) =>
        new(there.Count == 1
                ? "Replace the memory card with an empty one?"
                : "Replace " + there.Count.ToString(CultureInfo.CurrentCulture) + " memory cards with empty ones?",
            there.Count == 1 ? "The saves on it are lost." : "The saves on them are lost.",
            "_Replace and lose the saves",
            there.Count == 1 ? "_Keep it" : "_Keep them",
            QuestionKind.Warn,
            Destructive: true,
            Items: CardList(there, queue));

    private static List<string> CardList(IReadOnlyList<MemoryCardPlan> plans, IReadOnlyList<QueuePreviewItem> queue) =>
        plans.Select(plan => "VMC/" + plan.FileName + " (" + (queue.FirstOrDefault(item => item.DiscId == plan.DiscId)?.DetectedTitle ?? plan.DiscId) + ")").ToList();

    private static string Cards(int count) => count == 1 ? "1 memory card" : count.ToString(CultureInfo.CurrentCulture) + " memory cards";

    private void ShowProgress(BayQueueProgress step, TimeSpan elapsed)
    {
        const double Megabyte = 1024 * 1024;
        const double Gigabyte = 1024 * Megabyte;
        // The PS2 doesn't report how far a check is, only its own screen shows it.
        if (step.Status.Checking)
        {
            Status = PageStatus.Busy(
                string.Create(CultureInfo.CurrentCulture, $"Checking {step.Index + 1} of {step.Count}: {step.Item.DetectedTitle}"),
                "Loadout Bay reads it back; the PS2 shows how far.");
            return;
        }

        var written = (double)step.Status.WrittenBytes;
        var total = Math.Max((double)step.Status.TotalBytes, 1);
        var rate = InstallBatchMeter.SettledMegabytesPerSecond(written, elapsed);
        var speed = rate > 0
            ? string.Create(CultureInfo.CurrentCulture, $" · {rate:0.0} MB/s · ") + Remaining(TimeSpan.FromSeconds((total - written) / Megabyte / rate.Value)) + " left"
            : "";
        Status = PageStatus.Busy(
            string.Create(CultureInfo.CurrentCulture, $"Installing {step.Index + 1} of {step.Count}: {step.Item.DetectedTitle}"),
            string.Create(CultureInfo.CurrentCulture, $"{written / Gigabyte:0.00} of {total / Gigabyte:0.00} GB{speed}"),
            written * 100 / total);
    }

    private static string Remaining(TimeSpan time) =>
        time.TotalMinutes >= 1
            ? ((int)Math.Ceiling(time.TotalMinutes)).ToString(CultureInfo.CurrentCulture) + " min"
            : ((int)Math.Ceiling(time.TotalSeconds)).ToString(CultureInfo.CurrentCulture) + " s";

    // Ok when every game was installed, otherwise Warn with what became of the others: by name when it is one.
    private static PageStatus Finished(IReadOnlyList<BayQueueResult> results)
    {
        var installed = results.Count(result => result.Outcome == BayInstallOutcome.Installed);
        var others = results.Where(result => result.Outcome != BayInstallOutcome.Installed).ToList();
        if (others.Count == 0)
        {
            var missing = results.Count(result => result.ArtworkMissing);
            return PageStatus.Ok(
                "Installed " + Games(installed),
                missing == 0
                    ? "The Logs page has each game's result."
                    : (installed == 1 ? "It has" : string.Create(CultureInfo.CurrentCulture, $"{missing} of them have")) + " no artwork on the PS2. The Logs page says why.");
        }

        var headline = string.Create(CultureInfo.CurrentCulture, $"Installed {installed} of {Games(results.Count)}");
        return PageStatus.Warn(headline, others.Count == 1 ? others[0].Item.DetectedTitle + " " + Outcome(others[0].Outcome) + "." : NotInstalled(others));
    }

    private static string Games(int count) => count == 1 ? "1 game" : count.ToString(CultureInfo.CurrentCulture) + " games";

    private static string Outcome(BayInstallOutcome outcome) => outcome switch
    {
        BayInstallOutcome.AlreadyOnPs2 => "is already on the PS2",
        BayInstallOutcome.IncompleteOnPs2 => "has an incomplete install to remove first",
        BayInstallOutcome.DoesNotFit => "is too large for the free space",
        BayInstallOutcome.NotReady => "is not ready",
        BayInstallOutcome.CheckFailed => "failed the check and was left as an incomplete install",
        BayInstallOutcome.Cancelled => "was cancelled and left as an incomplete install",
        BayInstallOutcome.NotRun => "was not started",
        _ => "failed",
    };

    private static string NotInstalled(IReadOnlyList<BayQueueResult> results)
    {
        string Part(BayInstallOutcome outcome, string label)
        {
            var count = results.Count(result => result.Outcome == outcome);
            return count == 0 ? "" : count.ToString(CultureInfo.CurrentCulture) + " " + label;
        }

        var parts = new[]
        {
            Part(BayInstallOutcome.AlreadyOnPs2, "already on the PS2"),
            Part(BayInstallOutcome.IncompleteOnPs2, "with an incomplete install to remove first"),
            Part(BayInstallOutcome.DoesNotFit, "too large for the free space"),
            Part(BayInstallOutcome.NotReady, "not ready"),
            Part(BayInstallOutcome.CheckFailed, "failed the check, left as an incomplete install"),
            Part(BayInstallOutcome.Failed, "failed"),
            Part(BayInstallOutcome.Cancelled, "cancelled, left as an incomplete install"),
            Part(BayInstallOutcome.NotRun, "not started"),
        }.Where(part => part.Length > 0);
        return string.Join(", ", parts) + ". Logs has each game's result.";
    }

    // A cancel only gets here before the first game: once games run, the installer reports it as their result.
    private void OnInstallFailed(Exception exception)
    {
        Status = exception is OperationCanceledException
            ? PageStatus.Warn("Install cancelled", "No game was copied.")
            : PageStatus.Error("Install failed", exception is LoadoutBayException bay ? PageStatus.BayReason(bay) : UserFacingText.Describe(exception.Message));
        _log.Add(Status.LogLine);
    }

    private void RemoveSelected()
    {
        if (_selection.Length == 0)
        {
            return;
        }

        var kept = InstallQueueEditor.Remove(_scanned, _selection);
        var removed = _scanned.Count - kept.Count;
        _scanned.Clear();
        _scanned.AddRange(kept);
        SetSelection([]);
        Publish();
        Status = QueueStatus("Removed " + Games(removed) + " from the queue. Nothing was deleted on the PS2.");
        _log.Add(Status.Detail);
    }

    private void Cancel()
    {
        if (_install is not null)
        {
            _install.Cancel();
            Status = PageStatus.Busy("Cancelling", "The game being copied stays on the PS2 as an incomplete install.");
            return;
        }

        _scan?.Cancel();
        Status = PageStatus.Busy("Cancelling", "The images scanned so far stay in the queue.");
    }

    private void OnScanFailed(Exception exception)
    {
        Status = PageStatus.Error("Scan failed", UserFacingText.Describe(exception.Message));
        _log.Add(Status.LogLine);
    }
}
