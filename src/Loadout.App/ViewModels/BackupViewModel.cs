using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Loadout.App.Controls;
using Loadout.Core;
using Loadout.Discs;
using Loadout.Metadata;

namespace Loadout.App;

/// <summary>A disc in a drive, as the page offers it.</summary>
public sealed record DriveDisc(DiscInDrive Disc, string Title)
{
    public string Label => $"{Title} ({Disc.Identity?.DiscId?.Canonical}) in {Disc.Drive}";
}

/// <summary>One item of the disc map's legend. Its amount changes in place, so the legend doesn't move as it counts.</summary>
public sealed class DiscMapLegendItem(DiscMapShade shade, System.Windows.Media.Brush brush, string label) : ObservableObject
{
    private string _amount = string.Empty;

    public DiscMapShade Shade { get; } = shade;

    public System.Windows.Media.Brush Brush { get; } = brush;

    public string Label { get; } = label;

    public string Amount
    {
        get => _amount;
        set => SetField(ref _amount, value);
    }
}

/// <summary>One backed-up disc in the page's list.</summary>
public sealed record BackupRow(DiscBackup Backup, bool OnPs2)
{
    public string Name => Backup.Files.Name;

    public string SizeLabel => BackupViewModel.Size(Backup.Bytes);

    public string DateLabel => Backup.ModifiedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public DiscBackupResult Result => Backup.Result;

    public string ResultLabel => Backup.Result switch
    {
        DiscBackupResult.MatchesRedump => "Matches Redump",
        DiscBackupResult.DoesNotMatchRedump => "Doesn't match",
        DiscBackupResult.NotInRedump => "Not in Redump",
        DiscBackupResult.NotCheckedYet => "Not checked yet",
        _ => string.Create(CultureInfo.CurrentCulture, $"Incomplete, {Math.Floor(100.0 * Backup.ReadSectors / Math.Max(1, Backup.Sectors)):0}%"),
    };

    public string OnPs2Label => OnPs2 ? "Yes" : "";

    public override string ToString() => Name + ", " + ResultLabel;
}

/// <summary>
/// Back up discs: copies an original PS2 disc from a PC drive to an image checked against Redump, so the games are kept on
/// the PC. A copy goes on until it is complete or stopped, and can be resumed after cleaning the disc or in another drive.
/// </summary>
public sealed class BackupViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private readonly OperationLog _log;
    private readonly SettingsViewModel _settings;
    private readonly IDiscTitleCatalog _titles;
    private readonly RedumpDatCache _redump;
    private readonly Func<Question, bool> _confirm;
    private readonly Func<IReadOnlyCollection<string>> _installedDiscIds;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reportGate = new();
    private readonly List<string> _report = [];
    private bool _looking;
    private CancellationTokenSource? _run;
    private Task? _copyRun;
    private (DiscBackupFiles Files, DriveDisc Disc, DateTime Started)? _current;
    private bool _closed;
    private DriveDisc? _selected;
    private PageStatus _status = PageStatus.Idle("Insert a PS2 disc", "Into a disc drive on this PC. Back up this disc copies it to a file here.");
    private string _statusLine = string.Empty;
    private string _summary = string.Empty;
    private bool _working;
    private bool _keepGoing;
    private DiscMapView _map = DiscMapView.Empty;
    private DiscCopyStatus? _copy;
    private DateTime _restUntil;
    private DiscBackup? _sameDisc;
    private BackupRow? _selectedBackup;
    private IsoFileList? _files;
    private string? _filesFrom;
    private DateTime _filesTried;

    public BackupViewModel(
        OperationLog log,
        SettingsViewModel settings,
        IDiscTitleCatalog titles,
        RedumpDatCache redump,
        Func<Question, bool> confirm,
        Func<IReadOnlyCollection<string>> installedDiscIds)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _titles = titles ?? throw new ArgumentNullException(nameof(titles));
        _redump = redump ?? throw new ArgumentNullException(nameof(redump));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _installedDiscIds = installedDiscIds ?? throw new ArgumentNullException(nameof(installedDiscIds));
        BackUpCommand = new AsyncRelayCommand(BackUpAsync, Fail);
        BackUpAgainCommand = new AsyncRelayCommand(() => BackUpAsync(again: true), Fail);
        StopCommand = new RelayCommand(Stop);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ShowReportCommand = new RelayCommand(ShowReport);
        Describe = DescribeSectors;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollEvery };
        _poll.Tick += (_, _) => _ = PollAsync();
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => UpdateStatusLine();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.BackupFolder))
            {
                RefreshList();
            }
        };
        RefreshList();
        _poll.Start();
        _ = PollAsync();
    }

    public ICommand BackUpCommand { get; }

    public ICommand BackUpAgainCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand ShowReportCommand { get; }

    /// <summary>The PS2 discs in the PC's drives; the page offers a choice when there are several.</summary>
    public ObservableCollection<DriveDisc> Discs { get; } = [];

    public bool HasSeveralDiscs => Discs.Count > 1;

    public DriveDisc? SelectedDisc
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                if (!_working)
                {
                    Map = DiscMapView.Empty;
                    Legend.Clear();
                    OnPropertyChanged(nameof(HasMap));
                }

                UpdateSameDisc();
                ShowReady();
                OnPropertyChanged(nameof(CanBackUp));
            }
        }
    }

    public ObservableCollection<BackupRow> Backups { get; } = [];

    public BackupRow? SelectedBackup
    {
        get => _selectedBackup;
        set => SetField(ref _selectedBackup, value);
    }

    public bool HasBackups => Backups.Count > 0;

    public PageStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>One plain line under the disc map about what the copy does now.</summary>
    public string StatusLine
    {
        get => _statusLine;
        private set => SetField(ref _statusLine, value);
    }

    /// <summary>The collection's totals in the page header.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public bool IsWorking
    {
        get => _working;
        private set
        {
            if (SetField(ref _working, value))
            {
                OnPropertyChanged(nameof(CanBackUp));
                OnPropertyChanged(nameof(CanBackUpAgain));
                OnPropertyChanged(nameof(CanChooseDisc));
            }
        }
    }

    /// <summary>The disc can be chosen while no copy runs.</summary>
    public bool CanChooseDisc => !_working;

    public bool CanBackUp => !_working && _selected is not null && _sameDisc is not { Result: not DiscBackupResult.Incomplete };

    /// <summary>The disc in the drive is backed up already: offered instead of Back up this disc.</summary>
    public bool CanBackUpAgain => !_working && _selected is not null && _sameDisc is { Result: not DiscBackupResult.Incomplete };

    public string BackUpLabel => _sameDisc is { Result: DiscBackupResult.Incomplete } ? "_Resume this disc" : "_Back up this disc";

    /// <summary>When on, a finished disc comes out of the drive and the next PS2 disc put in starts by itself.</summary>
    public bool KeepGoing
    {
        get => _keepGoing;
        set
        {
            if (SetField(ref _keepGoing, value))
            {
                _log.Add(value ? "Back up discs keeps going: each PS2 disc put in is backed up." : "Back up discs no longer starts by itself.");
            }
        }
    }

    public DiscMapView Map
    {
        get => _map;
        private set => SetField(ref _map, value);
    }

    public bool HasMap => _map.Sectors > 0;

    public ObservableCollection<DiscMapLegendItem> Legend { get; } = [];

    /// <summary>The disc map's hover text for a stretch of sectors.</summary>
    public Func<long, long, string> Describe { get; }

    /// <summary>A legend amount in a steady form: whole MB under 1 GB, two decimals of GB above.</summary>
    private static string LegendSize(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000_000.0:0.00} GB")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000.0:0} MB");

    public static string Size(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000_000.0:0.00} GB")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_000_000.0:0.#} MB");

    /// <summary>
    /// Stops a copy under way and waits a little for it to save its map, then adds the run to the copy's report, so
    /// closing Loadout loses nothing: the copy goes on from there next time.
    /// </summary>
    public void Dispose()
    {
        _closed = true;
        _poll.Stop();
        _tick.Stop();
        if (_run is { } run && _copyRun is { } copy && _current is { } current)
        {
            run.Cancel();
            try
            {
                copy.Wait(TimeSpan.FromSeconds(15));
            }
            catch (AggregateException)
            {
                // Whatever stopped it, the map was saved after its last change.
            }

            WriteReport(current.Files, current.Disc, current.Started, "Stopped: Loadout was closed.");
        }
    }

    /// <summary>Reads the backup folder again: after a copy, or when the folder changed.</summary>
    public void RefreshList()
    {
        var folder = _settings.BackupFolder;
        var backups = folder.Length == 0 ? [] : DiscBackupLibrary.List(folder);
        var installed = new HashSet<string>(_installedDiscIds(), StringComparer.OrdinalIgnoreCase);
        var selected = _selectedBackup?.Name;
        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(new BackupRow(backup, backup.Identity?.DiscId is { } id && installed.Contains(id.Canonical)));
        }

        SelectedBackup = Backups.FirstOrDefault(b => b.Name == selected);
        var verified = backups.Count(b => b.Result == DiscBackupResult.MatchesRedump);
        var incomplete = backups.Count(b => b.Result == DiscBackupResult.Incomplete);
        Summary = backups.Count == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"{backups.Count} DISC{(backups.Count == 1 ? "" : "S")} · {Size(backups.Sum(b => b.Bytes)).ToUpperInvariant()}{(verified > 0 ? $" · {verified} VERIFIED" : "")}{(incomplete > 0 ? $" · {incomplete} INCOMPLETE" : "")}");
        OnPropertyChanged(nameof(HasBackups));
        UpdateSameDisc();
    }

    private void Fail(Exception ex)
    {
        IsWorking = false;
        Status = PageStatus.Error("The backup stopped", ex.Message);
        _log.Add("Back up discs: " + ex.Message);
    }

    // Looks at each drive cheaply, and reads a disc only when what Windows sees in the drive changed.
    private async Task PollAsync()
    {
        if (_looking)
        {
            return;
        }

        _looking = true;
        try
        {
            var busyDrive = _working ? _selected?.Disc.Drive : null;
            var changes = await Task.Run(() =>
            {
                var found = new List<(string Drive, string Print, DiscInDrive? Disc)>();
                foreach (var drive in DiscDrives.List())
                {
                    if (string.Equals(drive, busyDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var print = DiscDrives.Fingerprint(drive);
                    if (_fingerprints.TryGetValue(drive, out var was) && was == print)
                    {
                        continue;
                    }

                    found.Add((drive, print, print.Length == 0 ? null : DiscDrives.Look(drive)));
                }

                return found;
            }).ConfigureAwait(true);

            foreach (var (drive, print, disc) in changes)
            {
                _fingerprints[drive] = print;
                await ShowDiscAsync(drive, disc).ConfigureAwait(true);
            }
        }
        finally
        {
            _looking = false;
        }
    }

    private async Task ShowDiscAsync(string drive, DiscInDrive? disc)
    {
        var old = Discs.FirstOrDefault(d => string.Equals(d.Disc.Drive, drive, StringComparison.OrdinalIgnoreCase));
        if (old is not null && !(_working && old == _selected))
        {
            Discs.Remove(old);
        }

        if (disc is { IsPs2Game: true, Identity.DiscId: { } id })
        {
            var lookup = await _titles.LookupAsync(id, CancellationToken.None).ConfigureAwait(true);
            var entry = new DriveDisc(disc, string.IsNullOrWhiteSpace(lookup.Title) ? disc.VolumeLabel ?? id.Canonical : lookup.Title!);
            Discs.Add(entry);
            if (!_working)
            {
                SelectedDisc = entry;
            }

            if (_keepGoing && !_working && CanBackUp)
            {
                _log.Add($"Keep going: {entry.Title} was put in, so its backup starts.");
                await BackUpAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        else if (!_working && (_selected is null || old == _selected))
        {
            SelectedDisc = Discs.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasSeveralDiscs));
        if (!_working && _selected is null)
        {
            Status = disc is not null && !disc.IsPs2Game
                ? PageStatus.Idle("This is not a PS2 game disc", $"The disc in {drive} has no PS2 game on it.")
                : PageStatus.Idle("Insert a PS2 disc", "Into a disc drive on this PC. Back up this disc copies it to a file here.");
        }
    }

    private void UpdateSameDisc()
    {
        var identity = _selected?.Disc.Identity;
        _sameDisc = identity is null ? null : DiscBackupLibrary.FindSameDisc(Backups.Select(b => b.Backup), identity);
        OnPropertyChanged(nameof(CanBackUp));
        OnPropertyChanged(nameof(CanBackUpAgain));
        OnPropertyChanged(nameof(BackUpLabel));
    }

    private void ShowReady()
    {
        if (_working || _selected is not { } disc)
        {
            return;
        }

        var what = $"{disc.Title} ({disc.Disc.Identity?.DiscId?.Canonical}), a {Size(disc.Disc.Bytes)} {(disc.Disc.Media == DiscMedia.Cd ? "CD" : disc.Disc.Layers > 1 ? "two-layer DVD" : "DVD")} in drive {disc.Disc.Drive.TrimEnd(':')}";
        Status = _sameDisc switch
        {
            { Result: DiscBackupResult.Incomplete } same => PageStatus.Idle(
                "This disc's backup is not complete",
                $"{what}. {Percent(same)} of it is copied; Resume this disc goes on from there."),
            { } same => PageStatus.Ok("This disc is backed up", $"{what}, as {same.Files.Name}. Back up again makes a second copy."),
            _ => PageStatus.Idle("Ready to back up", what + "."),
        };
        if (_sameDisc is { } shown && (Map.Sectors == 0 || !_working))
        {
            ShowSavedMap(shown, disc.Disc);
        }
        else if (_sameDisc is null)
        {
            // The disc's map, all of it not read yet, so the page keeps its layout when the copy starts.
            SetMap([new DiscMapRange(0, disc.Disc.Sectors, DiscSectorState.Untried)], disc.Disc.Sectors, disc.Disc.Media, disc.Disc.Layer0Sectors, -1);
        }
    }

    private void ShowSavedMap(DiscBackup backup, DiscInDrive? disc)
    {
        try
        {
            var map = DiscCopyMap.Load(backup.Files.Map);
            SetMap(map.Ranges.ToList(), map.Sectors, disc?.Media ?? DiscMedia.Dvd, disc?.Layer0Sectors ?? 0, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Map = DiscMapView.Empty;
        }
    }

    private static string Percent(DiscBackup backup) =>
        string.Create(CultureInfo.CurrentCulture, $"{100.0 * backup.ReadSectors / Math.Max(1, backup.Sectors):0.0}%");

    private Task BackUpAsync(CancellationToken _) => BackUpAsync(again: false);

    /// <summary>Backs up the selected disc, or goes on with its incomplete copy, until it is complete or stopped.</summary>
    private async Task BackUpAsync(bool again)
    {
        if (_working || _selected is not { } chosen || chosen.Disc.Identity is not { } identity)
        {
            return;
        }

        var folder = _settings.BackupFolder;
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            folder = _settings.ChooseBackupFolder() ?? string.Empty;
            if (folder.Length == 0)
            {
                return;
            }

            RefreshList();
        }

        var resume = !again && _sameDisc is { Result: DiscBackupResult.Incomplete } incomplete ? incomplete : null;
        if (!again && resume is null && _sameDisc is not null)
        {
            return;
        }

        if (again && _sameDisc is { } first && !_confirm(new Question(
            "Back up this disc again?",
            $"{chosen.Title} is backed up as {first.Files.Name}. A second copy is made beside it; the first one stays.",
            "_Back up again",
            "Cancel")))
        {
            return;
        }

        var files = resume?.Files ?? DiscBackupFiles.Unused(folder, DiscBackupFiles.NameFor(chosen.Title, identity.DiscId), isCd: chosen.Disc.Media == DiscMedia.Cd);
        IsWorking = true;
        var run = new CancellationTokenSource();
        _run = run;
        var progress = new Progress<DiscCopyStatus>(Show);
        lock (_reportGate)
        {
            _report.Clear();
        }

        _files = null;
        _filesFrom = files.Image;
        Status = PageStatus.Busy(resume is null ? "Backing up " + chosen.Title : "Going on with " + chosen.Title, "Opening the disc…");
        StatusLine = string.Empty;
        _tick.Start();
        DiscCopyOutcome outcome;
        DiscCopyMap map;
        var started = DateTime.Now;
        _current = (files, chosen, started);
        try
        {
            var copy = Task.Run(() => CopyAsync(chosen.Disc, identity, files, resume is not null, progress, run.Token));
            _copyRun = copy;
            (outcome, map) = await copy.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _tick.Stop();
            IsWorking = false;
            Status = PageStatus.Error("The backup stopped", ex.Message);
            _log.Add($"Backing up {chosen.Title} stopped: {ex.Message}");
            WriteReport(files, chosen, started, "Stopped: " + ex.Message);
            RefreshList();
            return;
        }
        finally
        {
            _run = null;
            _copyRun = null;
            _current = null;
            if (!_closed)
            {
                run.Dispose();
            }
        }

        if (_closed)
        {
            return;
        }

        _tick.Stop();
        if (outcome == DiscCopyOutcome.Stopped)
        {
            IsWorking = false;
            var done = 100.0 * map.ReadSectors / map.Sectors;
            Status = PageStatus.Idle("Stopped", string.Create(CultureInfo.CurrentCulture, $"{done:0.0}% of {chosen.Title} is copied. Resume this disc goes on from there, after cleaning it or in another drive too."));
            StatusLine = string.Empty;
            _log.Add(string.Create(CultureInfo.CurrentCulture, $"Stopped backing up {chosen.Title} at {done:0.0}%."));
            WriteReport(files, chosen, started, string.Create(CultureInfo.InvariantCulture, $"Stopped at {done:0.0}%."));
            RefreshList();
            return;
        }

        await FinishAsync(chosen, files, started).ConfigureAwait(true);
    }

    private async Task<(DiscCopyOutcome, DiscCopyMap)> CopyAsync(DiscInDrive disc, DiscIdentity identity, DiscBackupFiles files, bool resume, IProgress<DiscCopyStatus> progress, CancellationToken cancellationToken)
    {
        using var drive = WindowsDiscDrive.Open(disc.Drive);
        var now = DiscIdentity.Read(drive);
        if (now is null || !now.IsSameDisc(identity))
        {
            throw new IOException($"The disc in {disc.Drive} changed. Back up this disc reads it again.");
        }

        DiscCopyMap map;
        if (resume)
        {
            map = DiscCopyMap.Load(files.Map);
            if (!DiscIdentity.TryParse(map.Identity, out var was) || !was.IsSameDisc(now))
            {
                throw new InvalidDataException($"{files.Name} is the copy of another disc.");
            }

            // Another drive, or the same one after a while: the passes start over on what is missing, in big pieces.
            map.StartOver(cleaned: false);
        }
        else
        {
            map = DiscCopyMap.New(drive.Sectors, now.Format());
        }

        Directory.CreateDirectory(files.Folder);
        map.Save(files.Map);
        Note($"Backup of {files.Name} ({now.Format()}) from {disc.Drive}, {(resume ? "going on" : "new")}: {map.ReadSectors} of {map.Sectors} sectors copied.");
        await using var image = new FileStream(files.Image, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        var copier = new DiscCopier(drive, image, map, m => m.Save(files.Map))
        {
            Note = note => Note(note.Message),
            Progress = progress,
        };
        var outcome = await copier.RunAsync(cancellationToken).ConfigureAwait(false);
        Note(string.Create(CultureInfo.InvariantCulture, $"{copier.Failures} failed reads; the drive rested {copier.Rested.TotalMinutes:0.0} min."));
        return (outcome, map);
    }

    private async Task FinishAsync(DriveDisc chosen, DiscBackupFiles files, DateTime started)
    {
        Status = PageStatus.Busy("Checking the copy", "Reading it back for its hashes, and getting Redump's list…", 0);
        StatusLine = string.Empty;
        var list = await _redump.GetAsync(refresh: false, CancellationToken.None).ConfigureAwait(true);
        var bytes = (double)chosen.Disc.Sectors * (files.IsCd ? 2352 : DiscSectors.Size);
        var hashing = new Progress<long>(read => Status = PageStatus.Busy("Checking the copy", "Reading it back for its hashes…", read / bytes * 100));
        var backup = await DiscBackupLibrary.FinishAsync(files, list.Dat, hashing, CancellationToken.None).ConfigureAwait(true);
        IsWorking = false;
        var name = backup.Files.Name;
        (Status, var line) = backup.Result switch
        {
            DiscBackupResult.MatchesRedump => (
                PageStatus.Ok("Backed up " + name, "It matches Redump: the copy is exact. Saved in " + backup.Files.Folder + "."),
                "matches Redump"),
            DiscBackupResult.DoesNotMatchRedump => (
                PageStatus.Warn("Backed up " + chosen.Title + ", but it doesn't match Redump", "Another printing of the game, or a read error the drive did not notice. Back up again reads it once more: two equal copies point to another printing."),
                "doesn't match Redump"),
            DiscBackupResult.NotInRedump => (
                PageStatus.Ok("Backed up " + chosen.Title, "Redump doesn't list this disc, so the copy can't be checked against it."),
                "not in Redump's list"),
            _ => (
                PageStatus.Warn("Backed up " + chosen.Title + "; not checked yet", (list.Failure ?? "Redump's list could not be had.") + " The copy is checked when Loadout next gets the list."),
                "not checked against Redump yet"),
        };
        _log.Add($"Backed up {name}: {line}.");
        WriteReport(backup.Files, chosen, started, "Complete: " + line + ".");
        RefreshList();
        if (_keepGoing)
        {
            Eject(chosen.Disc.Drive);
        }
    }

    private void Eject(string drive)
    {
        if (WindowsDiscDrive.TryEject(drive))
        {
            Status = Status with { Detail = Status.Detail + " Insert the next disc." };
        }
    }

    private void Stop()
    {
        if (_run is { } run)
        {
            Status = PageStatus.Busy("Stopping", "After the read under way, which can take a few seconds on a weak area.");
            run.Cancel();
        }
    }

    private void Show(DiscCopyStatus status)
    {
        if (!_working)
        {
            return;
        }

        _copy = status;
        if (status.Phase == DiscCopyPhase.Resting)
        {
            _restUntil = DateTime.UtcNow + status.RestLeft;
        }

        SetMap(status.Ranges, status.TotalSectors, _selected?.Disc.Media ?? DiscMedia.Dvd, _selected?.Disc.Layer0Sectors ?? 0, status.Sector);
        var percent = 100.0 * status.ReadSectors / Math.Max(1, status.TotalSectors);
        var title = _selected?.Title ?? "the disc";
        Status = status.Stalled
            ? PageStatus.Warn("Little is being read", "Cleaning the disc helps most here. Stop, clean it, put it back and Resume this disc: the copy goes on where it was.")
            : status.Phase is DiscCopyPhase.Sweep or DiscCopyPhase.Resting && status.Sweep > 0
                ? PageStatus.Busy("Backing up " + title, $"Weak areas can take minutes or hours. You can stop and resume any time. {Size(status.MissingSectors * DiscSectors.Size)} left.", percent)
                : PageStatus.Busy("Backing up " + title, status.Failures == 0 ? "Reading the disc." : "The first pass jumps past weak areas; later passes go back to them.", percent);
        UpdateStatusLine();
    }

    private void UpdateStatusLine()
    {
        if (_copy is not { } status || !_working)
        {
            return;
        }

        var at = Size(status.Sector * DiscSectors.Size);
        var speed = string.Create(CultureInfo.CurrentCulture, $"{status.BytesPerSecond / 1_000_000:0.0} MB/s");
        StatusLine = status.Phase switch
        {
            DiscCopyPhase.WaitingForDrive => "The drive refuses to read for now; waiting for it…",
            DiscCopyPhase.Resting => (_restUntil - DateTime.UtcNow).TotalSeconds is var left and > 0.5
                ? string.Create(CultureInfo.CurrentCulture, $"The drive needs a short rest after a failed read ({left:0} s)…")
                : "The drive needs a short rest after a failed read; trying it again…",
            DiscCopyPhase.FirstPass => status.BytesPerSecond > 0
                ? $"Reading normally · {speed} · about {Left(status.UntriedSectors * (_selected?.Disc.Media == DiscMedia.Cd ? 2352 : DiscSectors.Size) / status.BytesPerSecond)} left"
                : "Reading normally",
            DiscCopyPhase.Sweep when status.PieceSectors < 128 => $"Weak area at {at}: reading it in small pieces",
            DiscCopyPhase.Sweep when status.Backwards => $"Pass {status.Sweep + 1}: going back over {Size(status.MissingSectors * DiscSectors.Size)} from the end",
            DiscCopyPhase.Sweep => $"Pass {status.Sweep + 1}: going over {Size(status.MissingSectors * DiscSectors.Size)} again",
            _ => string.Empty,
        };
    }

    private static string Left(double seconds) => seconds switch
    {
        < 90 => "1 min",
        < 3600 => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(seconds / 60)} min"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{seconds / 3600:0.0} h"),
    };

    private void SetMap(IReadOnlyList<DiscMapRange> ranges, long sectors, DiscMedia media, long layer0, long current)
    {
        Map = new DiscMapView(ranges, sectors, media, layer0, current);
        OnPropertyChanged(nameof(HasMap));
        var totals = DiscMapShades.Totals(ranges);
        var notRead = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AppTrack");

        // A shade joins the legend when it first shows and stays, in the shades' order; amounts change in place.
        foreach (var shade in Enum.GetValues<DiscMapShade>())
        {
            var item = Legend.FirstOrDefault(i => i.Shade == shade);
            if (item is null && (totals[shade] > 0 || shade is DiscMapShade.ReadNormally or DiscMapShade.NotRead))
            {
                item = new DiscMapLegendItem(shade, DiscMapBrushes.For(shade, notRead), DiscMapBrushes.Label(shade));
                Legend.Insert(Legend.Count(i => i.Shade < shade), item);
            }

            if (item is not null)
            {
                item.Amount = LegendSize(totals[shade] * DiscSectors.Size);
            }
        }
    }

    private string DescribeSectors(long from, long to)
    {
        var map = _map;
        var text = new StringBuilder();
        text.Append(Size(from * DiscSectors.Size))
            .Append(string.Create(CultureInfo.CurrentCulture, $" · {DiscGeometry.RadiusMm(from, map.Media, map.Layer0Sectors) / 10:0.0} cm from the centre"));
        if (DiscMapShades.Worst(map.Ranges, from, to) is { } shade)
        {
            text.Append('\n').Append(DiscMapBrushes.Label(shade));
        }

        var files = FilesForHover();
        if (files is not null)
        {
            var (shares, _) = IsoFileTree.FilesIn(files, [(from, Math.Max(1, to - from))]);
            foreach (var share in shares.Take(2))
            {
                text.Append('\n').Append(share.File.Path);
            }
        }

        return text.ToString();
    }

    // The disc's files, read from the copy's sectors the map says it has, at most every 30 s while it is incomplete.
    private IsoFileList? FilesForHover()
    {
        if ((_files is { Complete: true }) || _filesFrom is null || DateTime.UtcNow - _filesTried < TimeSpan.FromSeconds(30))
        {
            return _files;
        }

        _filesTried = DateTime.UtcNow;
        var ranges = _map.Ranges;
        var sectors = _map.Sectors;
        var path = _filesFrom;
        var cd = path.EndsWith(DiscBackupFiles.CdImageExtension, StringComparison.OrdinalIgnoreCase);
        var raw = new byte[2352];
        try
        {
            using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _files = IsoFileTree.Read(
                (sector, destination) =>
                {
                    if (DiscMapShades.Worst(ranges, sector, sector + 1) is not { } shade || shade is DiscMapShade.NotRead or DiscMapShade.Failed)
                    {
                        return false;
                    }

                    if (!cd)
                    {
                        image.Position = sector * DiscSectors.Size;
                        image.ReadExactly(destination);
                        return true;
                    }

                    // A CD copy is raw: the data starts after the sync and header (mode 1) or the subheader too (mode 2).
                    image.Position = sector * 2352;
                    image.ReadExactly(raw);
                    raw.AsSpan(raw[15] == 1 ? 16 : 24, DiscSectors.Size).CopyTo(destination);
                    return true;
                },
                sectors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The hover text goes without file names.
        }

        return _files;
    }

    private void Note(string line)
    {
        lock (_reportGate)
        {
            _report.Add(string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}"));
        }
    }

    // Each run adds its lines to the copy's report, so a disc copied in several goes keeps one report.
    private void WriteReport(DiscBackupFiles files, DriveDisc disc, DateTime started, string result)
    {
        string[] lines;
        lock (_reportGate)
        {
            lines = [.. _report];
            _report.Clear();
        }

        try
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"== {disc.Title} ({disc.Disc.Identity?.DiscId?.Canonical}), from {disc.Disc.Drive}, started {started:yyyy-MM-dd HH:mm:ss}\n");
            foreach (var line in lines)
            {
                text.Append(line).Append('\n');
            }

            text.Append(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {result}\n\n");
            File.AppendAllText(files.Report, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add($"The report of {files.Name} could not be written: {ex.Message}");
        }
    }

    private void OpenFolder()
    {
        var folder = _settings.BackupFolder;
        if (_selectedBackup is { } row && File.Exists(row.Backup.Files.Image))
        {
            Process.Start("explorer.exe", "/select,\"" + row.Backup.Files.Image + "\"");
        }
        else if (folder.Length > 0 && Directory.Exists(folder))
        {
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }
    }

    private void ShowReport()
    {
        if (_selectedBackup is { } row && File.Exists(row.Backup.Files.Report))
        {
            Process.Start(new ProcessStartInfo(row.Backup.Files.Report) { UseShellExecute = true });
        }
        else if (_selectedBackup is { } other)
        {
            Status = PageStatus.Idle("No report for " + other.Name, "It was made before reports were kept, or its report was removed.");
        }
    }
}
