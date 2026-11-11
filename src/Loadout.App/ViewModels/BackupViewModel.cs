using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Loadout.App.Controls;
using Loadout.Bay;
using Loadout.Core;
using Loadout.Discs;
using Loadout.Images;
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

    // A drive that refuses even the disc's first sectors, as one does for a while after a failed read, is left idle this
    // long before it is tried again: reading it sooner keeps it from coming back.
    private static readonly TimeSpan LookAgainAfter = TimeSpan.FromSeconds(30);

    private readonly IDiscDrives _drives;
    private readonly OperationLog _log;
    private readonly SettingsViewModel _settings;
    private readonly IDiscTitleCatalog _titles;
    private readonly RedumpDatCache _redump;
    private readonly InstallViewModel _installer;
    private readonly List<Ps2Install> _ps2 = [];
    private readonly Func<Question, bool> _confirm;
    private readonly Func<IReadOnlyCollection<string>> _installedDiscIds;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reportGate = new();
    private readonly List<string> _report = [];
    private bool _looking;
    private readonly Dictionary<string, DateTime> _lookAgain = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _run;
    private Task? _copyRun;
    private (DiscBackupFiles Files, DriveDisc Disc, DateTime Started)? _current;
    private bool _closed;
    private bool _cleanAfterStop;
    private bool _resumeCleaned;
    private IReadOnlyList<double> _resumeRings = [];
    private (DiscIdentity Identity, string Drive, string Title)? _cleaning;
    private IReadOnlyList<double> _troubleRings = [];
    private AutoPlayBlocker? _autoPlay;
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
    private bool _alsoInstall;
    private string _installLine = string.Empty;
    private Ps2Install? _following;

    public BackupViewModel(
        IDiscDrives drives,
        OperationLog log,
        SettingsViewModel settings,
        IDiscTitleCatalog titles,
        RedumpDatCache redump,
        InstallViewModel installer,
        Func<Question, bool> confirm,
        Func<IReadOnlyCollection<string>> installedDiscIds)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _titles = titles ?? throw new ArgumentNullException(nameof(titles));
        _redump = redump ?? throw new ArgumentNullException(nameof(redump));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _installedDiscIds = installedDiscIds ?? throw new ArgumentNullException(nameof(installedDiscIds));
        BackUpCommand = new AsyncRelayCommand(BackUpAsync, Fail);
        BackUpAgainCommand = new AsyncRelayCommand(() => BackUpAsync(again: true), Fail);
        StopCommand = new RelayCommand(Stop);
        PauseToCleanCommand = new RelayCommand(PauseToClean);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ShowReportCommand = new RelayCommand(ShowReport);
        InstallBackupCommand = new RelayCommand(InstallSelected);
        CancelInstallCommand = new RelayCommand(CancelInstalls);
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

    /// <summary>Stops the copy, opens the tray and waits for the same disc, cleaned; the copy then goes on in big pieces.</summary>
    public ICommand PauseToCleanCommand { get; }

    /// <summary>While a copy reads: it can pause for cleaning.</summary>
    public bool CanPauseToClean => _copyRun is not null && _cleaning is null;

    /// <summary>During the cleaning pause: where the missing sectors are, in mm from the centre, circled on the disc picture.</summary>
    public IReadOnlyList<double> TroubleRings
    {
        get => _troubleRings;
        private set => SetField(ref _troubleRings, value);
    }

    public ICommand OpenFolderCommand { get; }

    public ICommand ShowReportCommand { get; }

    /// <summary>Installs the backup picked in the list on the PS2 now, with the Install page's options.</summary>
    public ICommand InstallBackupCommand { get; }

    /// <summary>Cancels the installs on the PS2 this page started; Loadout Bay removes what it had of them.</summary>
    public ICommand CancelInstallCommand { get; }

    /// <summary>The backup picked in the list is complete, so it can go to the PS2.</summary>
    public bool CanInstallBackup => _selectedBackup is { Result: not DiscBackupResult.Incomplete };

    /// <summary>An install on the PS2 that this page started is under way, or waits for its turn.</summary>
    public bool IsInstallingOnPs2 => _ps2.Count > 0;

    /// <summary>
    /// When on, each disc is installed on the PS2 as it is copied, with the Install page's options; when the copy meets a
    /// weak area, that install is cancelled and the finished copy is installed instead. Off at each start of Loadout, and
    /// kept from disc to disc.
    /// </summary>
    public bool AlsoInstall
    {
        get => _alsoInstall;
        set
        {
            if (SetField(ref _alsoInstall, value))
            {
                _log.Add(value ? "Back up discs also installs each disc on the PS2." : "Back up discs no longer installs the discs on the PS2.");
                if (!value)
                {
                    foreach (var install in _ps2.Where(i => i.ByTick).ToList())
                    {
                        Cancel(install);
                    }
                }
            }
        }
    }

    /// <summary>A second line under the status line: how far the install on the PS2 has got, or how it ended.</summary>
    public string InstallLine
    {
        get => _installLine;
        private set
        {
            if (SetField(ref _installLine, value))
            {
                OnPropertyChanged(nameof(HasInstallLine));
                OnPropertyChanged(nameof(HasPanel));
            }
        }
    }

    public bool HasInstallLine => _installLine.Length > 0;

    /// <summary>The panel under the status shows the disc map, the install line, or both.</summary>
    public bool HasPanel => HasMap || HasInstallLine;

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
                    MapShown();
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
        set
        {
            if (SetField(ref _selectedBackup, value))
            {
                OnPropertyChanged(nameof(CanInstallBackup));
            }
        }
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
                if (!value)
                {
                    _autoPlay?.Dispose();
                    _autoPlay = null;
                }

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
        _autoPlay?.Dispose();
        _autoPlay = null;
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
            var busyDrive = _copyRun is not null ? _selected?.Disc.Drive : null;
            var now = DateTime.UtcNow;
            var due = _lookAgain.Where(p => p.Value <= now).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changes = await Task.Run(() =>
            {
                var found = new List<(string Drive, string Print, DiscInDrive? Disc)>();
                foreach (var drive in _drives.List())
                {
                    if (string.Equals(drive, busyDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var print = _drives.Fingerprint(drive);
                    if (_fingerprints.TryGetValue(drive, out var was) && was == print && !due.Contains(drive))
                    {
                        continue;
                    }

                    found.Add((drive, print, print.Length == 0 ? null : _drives.Look(drive)));
                }

                return found;
            }).ConfigureAwait(true);

            foreach (var (drive, print, disc) in changes)
            {
                _fingerprints[drive] = print;
                if (disc is { Identity: null })
                {
                    _lookAgain[drive] = DateTime.UtcNow + LookAgainAfter;
                }
                else
                {
                    _lookAgain.Remove(drive);
                }

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
        if (_cleaning is { } cleaning && string.Equals(drive, cleaning.Drive, StringComparison.OrdinalIgnoreCase))
        {
            await DiscBackAfterCleaningAsync(cleaning, disc).ConfigureAwait(true);
            return;
        }

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
            Status = disc switch
            {
                { Identity: null } => PageStatus.Idle(
                    "The drive can't read the disc yet",
                    $"The drive in {drive} may still be resting after a failed read. Loadout tries the disc again in half a minute."),
                { IsPs2Game: false } => PageStatus.Idle("This is not a PS2 game disc", $"The disc in {drive} has no PS2 game on it."),
                _ => PageStatus.Idle("Insert a PS2 disc", "Into a disc drive on this PC. Back up this disc copies it to a file here."),
            };
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

        var speeds = disc.Disc.Speeds?.Range(CultureInfo.CurrentCulture);
        var what = $"{disc.Title} ({disc.Disc.Identity?.DiscId?.Canonical}), a {Size(disc.Disc.Bytes)} {(disc.Disc.Media == DiscMedia.Cd ? "CD" : disc.Disc.Layers > 1 ? "two-layer DVD" : "DVD")} in drive {disc.Disc.Drive.TrimEnd(':')}{(string.IsNullOrEmpty(speeds) ? "" : $", which reads it at {speeds}")}";
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
            SetMap(map.Ranges.ToList(), map.Sectors, disc?.Media ?? DiscMedia.Dvd, disc?.Layer0Sectors ?? 0, -1, [.. map.CleanedRingsMm]);
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
        var follow = _alsoInstall ? StartFollowing(chosen, identity, files, resume) : null;
        _following = follow;
        IsWorking = true;
        if (!_drives.IsSimulated)
        {
            _autoPlay ??= new AutoPlayBlocker(chosen.Disc.Drive);
        }
        var cleaned = _resumeCleaned;
        var rings = _resumeRings;
        _resumeCleaned = false;
        _resumeRings = [];
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
            var copy = Task.Run(() => CopyAsync(chosen.Disc, identity, files, resume is not null, cleaned, rings, follow?.Feed, progress, run.Token));
            _copyRun = copy;
            OnPropertyChanged(nameof(CanPauseToClean));
            (outcome, map) = await copy.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            follow?.Feed?.Break();
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
            _following = null;
            OnPropertyChanged(nameof(CanPauseToClean));
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
        if (outcome == DiscCopyOutcome.Stopped && _cleanAfterStop)
        {
            _cleanAfterStop = false;
            WriteReport(files, chosen, started, "Paused to clean the disc.");
            WaitForCleaning(chosen, identity, map);
            return;
        }

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

        // An install still waiting for its turn hasn't opened the copy yet: as the copy is renamed now, the finished copy
        // is installed instead. One that has it open reads on from its open file.
        if (follow?.Feed is { } feed && feed.CloseUnopened())
        {
            follow.Superseded = true;
        }

        var backup = await FinishAsync(chosen, files, started).ConfigureAwait(true);
        if (_alsoInstall && (follow is null || (follow.Feed!.IsBroken && !follow.CancelledHere && !follow.CopyStopped)))
        {
            _ = InstallFinishedAsync(backup, byTick: true);
        }
    }

    private async Task<(DiscCopyOutcome, DiscCopyMap)> CopyAsync(DiscInDrive disc, DiscIdentity identity, DiscBackupFiles files, bool resume, bool cleaned, IReadOnlyList<double> rings, DiscCopyFeed? feed, IProgress<DiscCopyStatus> progress, CancellationToken cancellationToken)
    {
        using var drive = _drives.Open(disc.Drive);
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

            // Cleaned, in another drive, or the same one after a while: the passes start over on what is missing, in big pieces.
            map.StartOver(cleaned, rings);
        }
        else
        {
            map = DiscCopyMap.New(drive.Sectors, now.Format());
        }

        Directory.CreateDirectory(files.Folder);
        map.Save(files.Map);
        Note($"Backup of {files.Name} ({now.Format()}) from {disc.Drive}, {(resume ? cleaned ? $"going on after cleaning {map.Cleanings}" : "going on" : "new")}: {map.ReadSectors} of {map.Sectors} sectors copied.");
        await using var image = new FileStream(files.Image, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        var copier = new DiscCopier(drive, image, map, m => m.Save(files.Map), _drives.CopierSettings)
        {
            Note = note => Note(note.Message),
            Progress = progress,
            Feed = feed,
        };
        var outcome = await copier.RunAsync(cancellationToken).ConfigureAwait(false);
        Note(string.Create(CultureInfo.InvariantCulture, $"{copier.Failures} failed reads; the drive rested {copier.Rested.TotalMinutes:0.0} min."));
        return (outcome, map);
    }

    private async Task<DiscBackup> FinishAsync(DriveDisc chosen, DiscBackupFiles files, DateTime started)
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

        return backup;
    }

    private void Eject(string drive)
    {
        if (_drives.TryEject(drive))
        {
            Status = Status with { Detail = Status.Detail + " Insert the next disc." };
        }
    }

    private void Stop()
    {
        if (_cleaning is not null)
        {
            _cleaning = null;
            TroubleRings = [];
            IsWorking = false;
            Status = PageStatus.Idle("Stopped", "The copy is kept. Resume this disc goes on from there, after cleaning the disc or in another drive too.");
            StatusLine = string.Empty;
            OnPropertyChanged(nameof(CanPauseToClean));
            return;
        }

        if (_run is { } run)
        {
            Status = PageStatus.Busy("Stopping", "After the read under way, which can take a few seconds on a weak area.");
            _following?.CopyStopped = true;
            run.Cancel();
        }
    }

    private void PauseToClean()
    {
        if (_run is { } run && _cleaning is null)
        {
            _cleanAfterStop = true;
            _following?.CopyStopped = true;
            Status = PageStatus.Busy("Stopping to clean the disc", "After the read under way, which can take a few seconds on a weak area.");
            run.Cancel();
        }
    }

    // The copy is saved and stopped: open the tray, show where the trouble is, and wait for the disc to come back.
    private void WaitForCleaning(DriveDisc chosen, DiscIdentity identity, DiscCopyMap map)
    {
        var trouble = DiscTrouble.From(map.Ranges, chosen.Disc.Media, chosen.Disc.Layer0Sectors);
        _cleaning = (identity, chosen.Disc.Drive, chosen.Title);
        OnPropertyChanged(nameof(CanPauseToClean));
        RefreshList();
        TroubleRings = trouble.RingsMm;
        _resumeRings = trouble.RingsMm;
        var opened = _drives.TryEject(chosen.Disc.Drive);
        var where = trouble.Where(CultureInfo.CurrentCulture);
        var missing = trouble.MissingSectors > 0
            ? $"{Size(trouble.MissingSectors * DiscSectors.Size)} is missing in {trouble.Areas} small area{(trouble.Areas == 1 ? "" : "s")}{(where.Length > 0 ? ", " + where : "")}, circled on the disc. "
            : string.Empty;
        Status = PageStatus.Warn(
            opened ? "Clean the disc, then put it back" : "Take the disc out, clean it, and put it back",
            missing + "Wipe it with a soft lint-free cloth in straight lines from the centre outwards, never in circles, and use no cleaners. The copy goes on by itself.");
        StatusLine = "Waiting for " + chosen.Title + " to come back…";
    }

    private async Task DiscBackAfterCleaningAsync((DiscIdentity Identity, string Drive, string Title) cleaning, DiscInDrive? disc)
    {
        if (disc is null)
        {
            return;
        }

        // The drive can't read it yet, as just after the tray closes: it is tried again shortly.
        if (disc.Identity is null)
        {
            StatusLine = "Reading " + cleaning.Title + " again; the drive needs a moment…";
            return;
        }

        if (disc.Identity is not { } identity || !identity.IsSameDisc(cleaning.Identity))
        {
            Status = PageStatus.Warn("This is another disc", $"Put {cleaning.Title} back in to go on with its copy, or Stop to keep the copy for later.");
            return;
        }

        _cleaning = null;
        TroubleRings = [];
        IsWorking = false;
        OnPropertyChanged(nameof(CanPauseToClean));
        var entry = Discs.FirstOrDefault(d => string.Equals(d.Disc.Drive, cleaning.Drive, StringComparison.OrdinalIgnoreCase)) is { } shown
            ? shown with { Disc = disc }
            : new DriveDisc(disc, cleaning.Title);
        Discs.Remove(Discs.FirstOrDefault(d => string.Equals(d.Disc.Drive, cleaning.Drive, StringComparison.OrdinalIgnoreCase))!);
        Discs.Add(entry);
        SelectedDisc = entry;
        _log.Add($"{cleaning.Title} is back after cleaning: its copy goes on.");
        _resumeCleaned = true;
        _resumeRings = TroubleRings.Count > 0 ? TroubleRings : _resumeRings;
        await BackUpAsync(again: false).ConfigureAwait(true);
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

        SetMap(status.Ranges, status.TotalSectors, _selected?.Disc.Media ?? DiscMedia.Dvd, _selected?.Disc.Layer0Sectors ?? 0, status.Sector, status.CleanedRingsMm);
        var percent = 100.0 * status.ReadSectors / Math.Max(1, status.TotalSectors);
        var title = _selected?.Title ?? "the disc";
        Status = status.Stalled
            ? PageStatus.Warn("Little is being read", "Cleaning the disc helps most here: Pause to clean the disc, and the copy goes on where it was.")
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
                ? $"Reading normally{DriveNow(status.Sector)} · {speed} · about {Left(status.UntriedSectors * (_selected?.Disc.Media == DiscMedia.Cd ? 2352 : DiscSectors.Size) / status.BytesPerSecond)} left"
                : "Reading normally",
            DiscCopyPhase.Sweep when status.PieceSectors < 128 => $"Weak area at {at}: reading it in small pieces",
            DiscCopyPhase.Sweep when status.Backwards => $"Pass {status.Sweep + 1}: going back over {Size(status.MissingSectors * DiscSectors.Size)} from the end",
            DiscCopyPhase.Sweep => $"Pass {status.Sweep + 1}: going over {Size(status.MissingSectors * DiscSectors.Size)} again",
            _ => string.Empty,
        };
    }

    // The drive's own nominal speed where it reads now, from the curve it gave for this disc, such as " · drive 17x of 24x".
    private string DriveNow(long sector)
    {
        if (_selected?.Disc is not { Speeds: { } speeds } disc || speeds.KbPerSecondAt(sector, disc.Layer0Sectors) is not { } now)
        {
            return string.Empty;
        }

        var max = speeds.MaxKbPerSecond > 0 ? speeds.MaxKbPerSecond : speeds.Stretches.Max(s => Math.Max(s.StartKbPerSecond, s.EndKbPerSecond));
        return $" · drive {speeds.X(now, CultureInfo.CurrentCulture)} of {speeds.X(max, CultureInfo.CurrentCulture)}";
    }

    private static string Left(double seconds) => seconds switch
    {
        < 90 => "1 min",
        < 3600 => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(seconds / 60)} min"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{seconds / 3600:0.0} h"),
    };

    private void SetMap(IReadOnlyList<DiscMapRange> ranges, long sectors, DiscMedia media, long layer0, long current, IReadOnlyList<double>? cleanedRingsMm = null)
    {
        Map = new DiscMapView(ranges, sectors, media, layer0, current) { CleanedRingsMm = cleanedRingsMm ?? [], InstallSector = MarkerSector() };
        MapShown();
        var totals = DiscMapShades.Totals(ranges);
        var notRead = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AppTrack");

        // Every state is in the legend from the start, so it never grows a row during a copy; amounts change in place.
        if (Legend.Count == 0)
        {
            foreach (var shade in DiscMapBrushes.LegendOrder)
            {
                Legend.Add(new DiscMapLegendItem(shade, DiscMapBrushes.For(shade, notRead), DiscMapBrushes.Label(shade)));
            }
        }

        foreach (var item in Legend)
        {
            item.Amount = LegendSize(totals[item.Shade] * DiscSectors.Size);
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

        var mm = DiscGeometry.RadiusMm(from, map.Media, map.Layer0Sectors);
        foreach (var ring in map.CleanedRingsMm.Where(r => Math.Abs(r - mm) <= 1.5).Take(1))
        {
            var back = DiscTrouble.CameBackNear(map.Ranges, ring, map.Media, map.Layer0Sectors);
            text.Append('\n').Append(back > 0 ? $"Cleaned here: {Size(back * DiscSectors.Size)} came back" : "Cleaned here: nothing has come back yet");
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

    private void MapShown()
    {
        OnPropertyChanged(nameof(HasMap));
        OnPropertyChanged(nameof(HasPanel));
    }

    // Starts the install on the PS2 that follows the copy, when the copy has nothing missing so far: it reads the copy's
    // file as the copy fills it. Otherwise (a two-layer DVD, or a copy with gaps) the finished copy is installed instead.
    private Ps2Install? StartFollowing(DriveDisc chosen, DiscIdentity identity, DiscBackupFiles files, DiscBackup? resume)
    {
        if (identity.DiscId is not { } id
            || DiscBackupInstall.FollowingCopy(files, chosen.Title, id, chosen.Disc.Media, chosen.Disc.Sectors, chosen.Disc.Layers) is not { } item)
        {
            return null;
        }

        DiscCopyFeed feed;
        try
        {
            var known = resume is null ? null : DiscCopyMap.Load(resume.Files.Map);
            if (known is not null && (!DiscCopyFeed.CanFollow(known) || known.Sectors != chosen.Disc.Sectors))
            {
                return null;
            }

            feed = known is null ? new DiscCopyFeed(chosen.Disc.Sectors, 0) : DiscCopyFeed.For(known);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }

        var install = new Ps2Install(identity, feed, byTick: true);
        var stride = files.IsCd ? 2352 : DiscSectors.Size;
        _log.Add($"Installing {chosen.Title} on the PS2 as it is copied.");
        _ = RunPs2InstallAsync(install, item, _ => new DiscCopyFeedStream(feed, files.Image, stride));
        return install;
    }

    // A finished copy to the PS2: scanned and titled as the Install page does, then installed through its queue.
    private async Task InstallFinishedAsync(DiscBackup backup, bool byTick)
    {
        try
        {
            var path = DiscBackupInstall.ScanPath(backup.Files);
            var scan = await Task.Run(() => DiscScanner.ScanAsync([new ScanInput(path, ScanDepth.File)], null, new DiscScanOptions())).ConfigureAwait(true);
            var items = await QueueTitleLookup.ApplyAsync(scan.Items, _titles, CancellationToken.None).ConfigureAwait(true);
            var found = items.Count > 0 ? items[0] : null;
            if (found is not { Status: QueueItemStatus.Ready } item)
            {
                InstallLine = $"{backup.Files.Name} can't be installed on the PS2 ({found?.StatusLabel ?? "not a game image"}).";
                _log.Add(InstallLine);
                return;
            }

            await RunPs2InstallAsync(new Ps2Install(backup.Identity, null, byTick), item, open: null).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            InstallLine = $"{backup.Files.Name} was not installed on the PS2: {ex.Message}";
            _log.Add(InstallLine);
        }
    }

    private void InstallSelected()
    {
        if (_selectedBackup is { Result: not DiscBackupResult.Incomplete } row)
        {
            _log.Add($"Installing {row.Name} on the PS2 from its backup.");
            _ = InstallFinishedAsync(row.Backup, byTick: false);
        }
    }

    private void CancelInstalls()
    {
        foreach (var install in _ps2.ToList())
        {
            Cancel(install);
        }
    }

    private static void Cancel(Ps2Install install)
    {
        install.CancelledHere = true;
        install.Stop.Cancel();
    }

    // Through the Install page's queue, which runs one install at a time: this one waits for its turn there.
    private async Task RunPs2InstallAsync(Ps2Install install, QueuePreviewItem item, Func<QueuePreviewItem, Stream>? open)
    {
        _ps2.Add(install);
        OnPropertyChanged(nameof(IsInstallingOnPs2));
        if (_ps2.Count == 1)
        {
            InstallLine = "On the PS2: starting the install of " + item.DetectedTitle + "…";
        }

        BayQueueResult? result;
        string? notStarted;
        try
        {
            (result, notStarted) = await _installer.InstallNowAsync(item, open, status => ShowInstall(install, status), install.Stop.Token).ConfigureAwait(true);
        }
        finally
        {
            _ps2.Remove(install);
            OnPropertyChanged(nameof(IsInstallingOnPs2));
            install.Dispose();
            RefreshMarker();
        }

        // A row that read the copy as it was made points at its provisional name: it goes once that install is over.
        if (install.Feed is not null && result?.Outcome != BayInstallOutcome.Installed)
        {
            _installer.Forget(item);
        }

        var title = item.DetectedTitle;
        var removed = result is { PartialCopyLeft: true }
            ? " Loadout Bay could not remove what it had of it; the next install of it replaces that."
            : " Loadout Bay removed what it had of it.";
        var line = notStarted is not null
            ? $"{title} was not installed on the PS2: {notStarted}."
            : result?.Outcome switch
            {
                null => null,
                BayInstallOutcome.Installed => $"{title} is installed on the PS2{(result.PartitionName is { } name ? " as " + name : string.Empty)}.",
                BayInstallOutcome.AlreadyOnPs2 => $"{title} is already on the PS2.",
                BayInstallOutcome.Cancelled when install.Superseded => null,
                BayInstallOutcome.Cancelled when install.CopyStopped => $"The install of {title} on the PS2 stopped with the copy.{removed}",
                BayInstallOutcome.Cancelled when install.Feed is { IsBroken: true } && !install.CancelledHere =>
                    $"The install of {title} on the PS2 stopped at the copy's first weak area.{removed} The finished copy is installed once it is complete.",
                BayInstallOutcome.Cancelled => $"The install of {title} on the PS2 was cancelled.{removed}",
                _ => result.Message,
            };
        if (line is not null)
        {
            InstallLine = line;
            _log.Add("Back up discs: " + line);
        }

        // The install read the PS2's list back, so the list's On PS2 column follows it.
        RefreshList();
    }

    // The line shows the first install this page started; one waiting behind it shows once that one is done.
    private void ShowInstall(Ps2Install install, BayInstallStatus? status)
    {
        if (_ps2.FirstOrDefault() != install)
        {
            return;
        }

        if (status is null)
        {
            InstallLine = "On the PS2: waiting for the install under way to end…";
            return;
        }

        if (status.Checking)
        {
            InstallLine = "On the PS2: all sent; Loadout Bay reads it back to check it…";
            install.Sector = (long)(status.TotalBytes / DiscSectors.Size);
        }
        else
        {
            var now = DateTime.UtcNow;
            var written = status.WrittenBytes;
            if (written > 0 && install.FirstWritten is null)
            {
                install.FirstWritten = (now, written);
            }

            var rate = install.FirstWritten is { } first && (now - first.At).TotalSeconds >= 2
                ? (written - first.Bytes) / 1_000_000.0 / (now - first.At).TotalSeconds
                : 0;
            InstallLine = $"On the PS2: {Size((long)written)} of {Size((long)status.TotalBytes)}{(rate > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {rate:0.0} MB/s") : string.Empty)}";
            install.Sector = (long)(written / DiscSectors.Size);
        }

        RefreshMarker();
    }

    // Where the install that follows the disc on the map has got, for the bar's second marker; -1 when none follows it.
    private long MarkerSector()
    {
        var shown = _selected?.Disc.Identity;
        return shown is null
            ? -1
            : _ps2.FirstOrDefault(i => i.Feed is not null && i.Identity is { } id && id.IsSameDisc(shown))?.Sector ?? -1;
    }

    private void RefreshMarker()
    {
        var sector = MarkerSector();
        if (_map.Sectors > 0 && _map.InstallSector != sector)
        {
            Map = _map with { InstallSector = sector };
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

    /// <summary>An install on the PS2 this page started: one following a copy (with a feed), or one of a finished copy.</summary>
    private sealed class Ps2Install(DiscIdentity? identity, DiscCopyFeed? feed, bool byTick) : IDisposable
    {
        public DiscIdentity? Identity { get; } = identity;

        public DiscCopyFeed? Feed { get; } = feed;

        /// <summary>Started by Also install on PS2, so turning it off cancels it.</summary>
        public bool ByTick { get; } = byTick;

        /// <summary>Cancels the install: from the page, or when the copy it follows can't be followed any more.</summary>
        public CancellationTokenSource Stop { get; } = feed is null ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(feed.Broken);

        public bool CancelledHere { get; set; }

        /// <summary>The copy it follows was stopped, or paused for cleaning.</summary>
        public bool CopyStopped { get; set; }

        /// <summary>The copy finished before it opened it: the finished copy is installed instead.</summary>
        public bool Superseded { get; set; }

        public long Sector { get; set; } = -1;

        public (DateTime At, ulong Bytes)? FirstWritten { get; set; }

        public void Dispose()
        {
            Stop.Dispose();
            Feed?.Dispose();
        }
    }
}
