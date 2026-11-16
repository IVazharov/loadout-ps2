using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Shell;
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

    // What a screen reader says for the item in the disc chooser, which shows the label too.
    public override string ToString() => Label;
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

/// <summary>What a notice from Back up discs is about.</summary>
public enum BackupNoticeKind
{
    /// <summary>A disc is backed up, with its result.</summary>
    BackedUp,

    /// <summary>Little has been read for a while: cleaning the disc is likely to help.</summary>
    NeedsCleaning,

    /// <summary>The copy stopped by itself, on an error.</summary>
    Stopped,
}

/// <summary>News from Back up discs for the user who may be in another window: a Windows notification, or a sound.</summary>
public sealed record BackupNotice(BackupNoticeKind Kind, string Title, string Text);

/// <summary>One backed-up disc in the page's list.</summary>
public sealed class BackupRow(DiscBackup backup, bool onPs2) : ObservableObject
{
    private string? _activity;

    public DiscBackup Backup { get; } = backup;

    public bool OnPs2 { get; } = onPs2;

    public string Name => Backup.Files.Name;

    /// <summary>What the copy takes on its drive.</summary>
    public string SizeLabel => BackupViewModel.Size(Backup.BytesOnDisk > 0 ? Backup.BytesOnDisk : Backup.Bytes);

    /// <summary>How the copy is kept: CHD, ISO or bin/cue.</summary>
    public string FormatLabel => Backup.Format switch
    {
        DiscBackupFormat.Chd => CopyFormats.Chd,
        DiscBackupFormat.BinCue => CopyFormats.BinCue,
        _ => CopyFormats.Iso,
    };

    public string DateLabel => Backup.ModifiedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public DiscBackupResult Result => Backup.Result;

    /// <summary>The copy being compressed, or waiting to be: said in place of its result until that is done.</summary>
    public string? Activity
    {
        get => _activity;
        set
        {
            if (SetField(ref _activity, value))
            {
                OnPropertyChanged(nameof(ResultLabel));
            }
        }
    }

    public string ResultLabel => _activity ?? Backup.Result switch
    {
        DiscBackupResult.MatchesRedump => "Matches Redump",
        DiscBackupResult.DoesNotMatchRedump => "Doesn't match",
        DiscBackupResult.NotInRedump => "Not in Redump",
        DiscBackupResult.NotCheckedYet => "Not checked yet",
        _ => string.Create(CultureInfo.CurrentCulture, $"Incomplete, {Math.Floor(100.0 * Backup.ReadSectors / Math.Max(1, Backup.Sectors)):0}%"),
    };

    public string OnPs2Label => OnPs2 ? "Yes" : "";

    public override string ToString() => Name + ", " + FormatLabel + ", " + ResultLabel;
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
    private readonly Func<Question, int?> _choose;

    // The copies picked in the list: Delete takes them all, the other buttons need exactly one.
    private IReadOnlyList<BackupRow> _picked = [];

    // The earlier copy that Back up again replaces once the new one is complete; null to keep both.
    private DiscBackup? _replacing;
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
    private TaskbarItemProgressState _taskbarState;
    private double _taskbarProgress;
    private string? _detailsReport;
    private bool _stallTold;
    private Ps2Install? _following;

    // Copies in line to become a chd, and the one being compressed: one at a time, beside any copy being made.
    private readonly List<Compression> _compressions = [];
    private Compression? _compressing;
    private string _compressingText = string.Empty;
    private CancellationTokenSource? _compressStop;
    private Task? _compressRun;
    private Task? _compressCore;

    public BackupViewModel(
        IDiscDrives drives,
        OperationLog log,
        SettingsViewModel settings,
        IDiscTitleCatalog titles,
        RedumpDatCache redump,
        InstallViewModel installer,
        Func<Question, bool> confirm,
        Func<Question, int?> choose,
        Func<IReadOnlyCollection<string>> installedDiscIds,
        InstallTicks ticks,
        CopyFormats formats)
    {
        Ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        Formats = formats ?? throw new ArgumentNullException(nameof(formats));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _titles = titles ?? throw new ArgumentNullException(nameof(titles));
        _redump = redump ?? throw new ArgumentNullException(nameof(redump));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _choose = choose ?? throw new ArgumentNullException(nameof(choose));
        _installedDiscIds = installedDiscIds ?? throw new ArgumentNullException(nameof(installedDiscIds));
        BackUpCommand = new AsyncRelayCommand(BackUpAsync, Fail);
        BackUpAgainCommand = new AsyncRelayCommand(() => BackUpAsync(again: true), Fail);
        StopCommand = new RelayCommand(Stop);
        PauseToCleanCommand = new RelayCommand(PauseToClean);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        DeleteCommand = new RelayCommand(DeletePicked);
        ShowReportCommand = new RelayCommand(ShowReport);
        InstallBackupCommand = new RelayCommand(InstallSelected);
        ConvertCommand = new RelayCommand(ConvertSelected);
        DetailsCommand = new RelayCommand(ShowDetails);
        SaveReportCommand = new RelayCommand(SaveReport);
        CancelInstallCommand = new RelayCommand(CancelInstalls);
        SkipCheckCommand = new RelayCommand(() => _installer.SkipCheck());
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
                ResumeCompressions();
            }
        };
        RefreshList();
        ResumeCompressions();
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

    /// <summary>A disc backed up, one that needs cleaning, a copy stopped on an error: for a notification when Loadout isn't in front.</summary>
    public event EventHandler<BackupNotice>? Notice;

    /// <summary>
    /// The taskbar button: green progress while a copy reads, yellow when it needs the user (little read, or waiting for the
    /// cleaned disc), red when it stopped on an error, nothing otherwise.
    /// </summary>
    public TaskbarItemProgressState TaskbarState
    {
        get => _taskbarState;
        private set => SetField(ref _taskbarState, value);
    }

    /// <summary>How much of the disc is in the copy, 0 to 1, for the taskbar button.</summary>
    public double TaskbarProgress
    {
        get => _taskbarProgress;
        private set => SetField(ref _taskbarProgress, value);
    }

    /// <summary>Opens the report of the copy that just ended, from the Details link in the status.</summary>
    public ICommand DetailsCommand { get; }

    /// <summary>A copy has just ended (finished, stopped, or on an error) and its report can be opened.</summary>
    public bool HasDetails => _detailsReport is not null;

    /// <summary>Saves a copy of the picked backup's report where the user chooses, for a problem report.</summary>
    public ICommand SaveReportCommand { get; }

    /// <summary>A backup is picked in the list, so its report can be shown or saved.</summary>
    public bool CanSaveReport => Single is not null;

    /// <summary>Sends the copies picked in the list to the Recycle Bin, after one question.</summary>
    public ICommand DeleteCommand { get; }

    /// <summary>One copy or more is picked in the list, so Delete has something to take.</summary>
    public bool CanDelete => _picked.Count > 0;

    // The one copy picked in the list, or null when none or several are.
    private BackupRow? Single => _picked.Count == 1 ? _picked[0] : null;

    /// <summary>Installs the backup picked in the list on the PS2 now, with this page's ticks.</summary>
    public ICommand InstallBackupCommand { get; }

    /// <summary>This page's own install ticks (artwork, memory cards, the check), for every install it starts.</summary>
    public InstallTicks Ticks { get; }

    /// <summary>How this page keeps its copies: a DVD as a chd or an iso, a CD as a bin and cue or a chd.</summary>
    public CopyFormats Formats { get; }

    /// <summary>Compresses the copy picked in the list to a chd; once it reads back the same, its iso or bin goes to the Recycle Bin.</summary>
    public ICommand ConvertCommand { get; }

    /// <summary>One complete copy that isn't a chd yet is picked, and it isn't being made or compressed.</summary>
    public bool CanConvert => Single is { Result: not DiscBackupResult.Incomplete } row
        && row.Backup.Format != DiscBackupFormat.Chd
        && !IsCompressing(row.Name)
        && !(_working && _current is { } current && Same(current.Files.Name, row.Name));

    /// <summary>Cancels the installs on the PS2 this page started; Loadout Bay removes what it had of them.</summary>
    public ICommand CancelInstallCommand { get; }

    /// <summary>The backup picked in the list is complete, so it can go to the PS2.</summary>
    public bool CanInstallBackup => Single is { Result: not DiscBackupResult.Incomplete };

    /// <summary>An install on the PS2 that this page started is under way, or waits for its turn.</summary>
    public bool IsInstallingOnPs2 => _ps2.Count > 0;

    /// <summary>The backup that closing Loadout would stop, said for the question before it closes; null when none runs.</summary>
    public string? RunningBackup
    {
        get
        {
            var backup = _working && _current is { } current
                ? $"A backup of {current.Disc.Title} is running. Closing stops it, and Resume this disc goes on from there next time."
                : _cleaning is { } cleaning
                    ? $"The backup of {cleaning.Title} waits for the cleaned disc. Closing stops it, and Resume this disc goes on from there next time."
                    : null;
            var compressing = _compressing is { } job
                ? $"{job.Files.Name} is being compressed. Closing stops that{(job.Interim ? ", and it starts again next time." : "; the copy stays as it is.")}"
                : null;
            return backup is null ? compressing : compressing is null ? backup : backup + " " + compressing;
        }
    }

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
                OnPropertyChanged(nameof(CanSaveReport));
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

        // The chd half written is removed; an interim one starts again next time, from its mark.
        _compressStop?.Cancel();
        try
        {
            _compressCore?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
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
            : string.Create(CultureInfo.CurrentCulture, $"{backups.Count} DISC{(backups.Count == 1 ? "" : "S")} · {Size(backups.Sum(b => b.BytesOnDisk > 0 ? b.BytesOnDisk : b.Bytes)).ToUpperInvariant()}{(verified > 0 ? $" · {verified} VERIFIED" : "")}{(incomplete > 0 ? $" · {incomplete} INCOMPLETE" : "")}");
        OnPropertyChanged(nameof(HasBackups));
        UpdateSameDisc();
        ShowCompressing();
    }

    // Back up again's Replace, once the new copy is complete: the earlier copy goes to the Recycle Bin and the new one
    // takes its name. An earlier copy that matches Redump stays when the new one doesn't. Returns a sentence for the status.
    private string Replace(ref DiscBackup backup)
    {
        if (_replacing is not { } old || string.Equals(old.Files.Name, backup.Files.Name, StringComparison.OrdinalIgnoreCase))
        {
            _replacing = null;
            return string.Empty;
        }

        _replacing = null;
        _compressions.RemoveAll(c => Same(c.Files.Name, old.Files.Name));
        if (old.Result == DiscBackupResult.MatchesRedump && backup.Result != DiscBackupResult.MatchesRedump)
        {
            _log.Add($"Kept {old.Files.Name}, which matches Redump, beside the new copy {backup.Files.Name}, which doesn't.");
            return $" {old.Files.Name} matches Redump and this copy doesn't, so both are kept.";
        }

        try
        {
            BackupRecycler.Recycle(old.Files);
            backup = backup with { Files = backup.Files.RenameTo(old.Files.Name) };
            _log.Add($"Replaced the earlier copy {old.Files.Name}: it is in the Recycle Bin.");
            return " It replaced the earlier copy, which is in the Recycle Bin.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _log.Add($"The earlier copy {old.Files.Name} could not be replaced: {ex.Message}");
            return $" The earlier copy could not be replaced ({ex.Message}), so both are kept.";
        }
    }

    private static string ResultWords(DiscBackupResult result) => result switch
    {
        DiscBackupResult.MatchesRedump => "it matches Redump",
        DiscBackupResult.DoesNotMatchRedump => "it doesn't match Redump",
        DiscBackupResult.NotInRedump => "Redump doesn't list it",
        DiscBackupResult.Incomplete => "incomplete",
        _ => "not checked against Redump yet",
    };

    /// <summary>The copies picked in the list, as the list says each time its picking changes.</summary>
    public void SetPicked(IReadOnlyList<BackupRow> picked)
    {
        _picked = picked ?? [];
        OnPropertyChanged(nameof(CanSaveReport));
        OnPropertyChanged(nameof(CanInstallBackup));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanConvert));
    }

    // Delete: one question for all the picked copies, then their files go to the Recycle Bin. The copy being made is left.
    private void DeletePicked()
    {
        var rows = _picked.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var busy = _working && _current is { } current
            ? rows.FirstOrDefault(r => string.Equals(r.Name, current.Files.Name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (busy is not null)
        {
            Status = PageStatus.Warn(busy.Name + " is being backed up", "Stop the copy first to delete it.");
            return;
        }

        var compressing = _compressing is { } job ? rows.FirstOrDefault(r => Same(r.Name, job.Files.Name)) : null;
        if (compressing is not null)
        {
            Status = PageStatus.Warn(compressing.Name + " is being compressed", "It can be deleted once that is done, in a few minutes.");
            return;
        }

        var size = Size(rows.Sum(r => r.Backup.Bytes));
        var one = rows.Count == 1;
        if (!_confirm(new Question(
            one ? "Delete " + rows[0].Name + "?" : string.Create(CultureInfo.CurrentCulture, $"Delete {rows.Count} copies?"),
            (one ? $"This {size} copy and its report, hashes and map go" : string.Create(CultureInfo.CurrentCulture, $"These {rows.Count} copies ({size}) and their reports, hashes and maps go"))
                + " to the Recycle Bin, where you can restore them. Games on the PS2 are not touched.",
            "_Delete",
            "Cancel",
            QuestionKind.Warn,
            Destructive: true,
            Items: one ? null : [.. rows.Select(r => r.Name)])))
        {
            return;
        }

        _compressions.RemoveAll(c => rows.Any(r => Same(r.Name, c.Files.Name)));
        var failed = new List<string>();
        foreach (var row in rows)
        {
            try
            {
                BackupRecycler.Recycle(row.Backup.Files);
                _log.Add($"Deleted the backup {row.Name}: it is in the Recycle Bin.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                failed.Add(row.Name + ": " + ex.Message);
                _log.Add($"The backup {row.Name} could not be deleted: {ex.Message}");
            }
        }

        RefreshList();
        Status = failed.Count == 0
            ? PageStatus.Ok(one ? "Deleted " + rows[0].Name : string.Create(CultureInfo.CurrentCulture, $"Deleted {rows.Count} copies"), "In the Recycle Bin, if you want them back.")
            : PageStatus.Error(failed.Count == rows.Count ? "Nothing was deleted" : "Some copies were not deleted", string.Join(" ", failed));
    }

    // The copy that just ended is picked in the list, so its buttons (Show report, Install on PS2) act on it.
    private void SelectCopy(DiscBackupFiles files) =>
        SelectedBackup = Backups.FirstOrDefault(b => string.Equals(b.Name, files.Name, StringComparison.OrdinalIgnoreCase))
            ?? SelectedBackup;

    private void Fail(Exception ex)
    {
        IsWorking = false;
        var why = StoppedBecause(ex);
        Status = PageStatus.Error("The backup stopped", why);
        _log.Add($"Back up discs: {why} ({UserFacingText.FirstLine(ex.Message)})");
        TaskbarState = TaskbarItemProgressState.Error;
        Notify(BackupNoticeKind.Stopped, "The backup stopped", why);
    }

    // Why a copy stopped, in plain words, for the page and the notification; the log and the report keep Windows' own
    // message too. What was read is kept, so each one ends with how to go on.
    private string StoppedBecause(Exception ex)
    {
        const int DiskFull = unchecked((int)0x80070070);
        const int HandleDiskFull = unchecked((int)0x80070027);
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        const int NotReady = unchecked((int)0x80070015);
        const int NotConnected = unchecked((int)0x8007048F);
        const string GoOn = " What was read is kept: Resume this disc goes on from there.";
        var folder = _settings.BackupFolder;
        return ex switch
        {
            UnauthorizedAccessException => $"Loadout may not write in the backup folder, {folder}. Check the folder's permissions, or choose another one in Settings.{GoOn}",
            IOException { HResult: DiskFull or HandleDiskFull } => $"The drive of the backup folder, {folder}, is full. Make room on it, or choose another folder in Settings.{GoOn}",
            IOException { HResult: SharingViolation or LockViolation } => $"Another program has a file of this copy open. Close it.{GoOn}",
            IOException { HResult: NotReady or NotConnected } => $"The disc drive stopped answering: was it unplugged? Plug it in again.{GoOn}",
            _ => UserFacingText.Describe(ex.Message) + GoOn,
        };
    }

    private void Notify(BackupNoticeKind kind, string title, string text) => Notice?.Invoke(this, new BackupNotice(kind, title, text));

    // The report of the copy that just ended, for the Details link; null hides the link.
    private void SetDetails(string? report)
    {
        _detailsReport = report;
        OnPropertyChanged(nameof(HasDetails));
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
                    $"The drive in {drive} may still be spinning the disc up, or resting after a failed read. Loadout tries the disc again in half a minute."),
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

        TaskbarState = TaskbarItemProgressState.None;
        var speeds = disc.Disc.Speeds?.Range(CultureInfo.CurrentCulture);
        var what = $"{disc.Title} ({disc.Disc.Identity?.DiscId?.Canonical}), a {Size(disc.Disc.Bytes)} {(disc.Disc.Media == DiscMedia.Cd ? "CD" : disc.Disc.Layers > 1 ? "two-layer DVD" : "DVD")} in drive {disc.Disc.Drive.TrimEnd(':')}{(string.IsNullOrEmpty(speeds) ? "" : $", which reads it at {speeds}")}";
        Status = _sameDisc switch
        {
            { Result: DiscBackupResult.Incomplete } same => PageStatus.Idle(
                "This disc's backup is not complete",
                $"{what}. {Percent(same)} of it is copied; Resume this disc goes on from there."),
            { } same => PageStatus.Ok("This disc is backed up", $"{what}, as {same.Files.Name}. Back up again makes another copy, beside it or in its place."),
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

        _replacing = null;
        if (again && _sameDisc is { } first)
        {
            var choice = _choose(new Question(
                "Back up this disc again?",
                $"{chosen.Title} is backed up as {first.Files.Name} ({ResultWords(first.Result)}). The new copy can go beside it, or replace it. "
                    + "The earlier copy is only replaced once the new one is complete, and goes to the Recycle Bin.",
                "_Back up again",
                "Cancel",
                Items: ["Keep both copies", "Replace " + first.Files.Name],
                ChooseItem: true));
            if (choice is not { } picked)
            {
                return;
            }

            _replacing = picked == 1 ? first : null;
        }

        if (!RoomToCompress(chosen, folder, resume is not null))
        {
            _replacing = null;
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
        _stallTold = false;
        SetDetails(null);
        TaskbarProgress = resume is null ? 0 : (double)resume.ReadSectors / Math.Max(1, resume.Sectors);
        TaskbarState = TaskbarItemProgressState.Normal;
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
            var why = StoppedBecause(ex);
            Status = PageStatus.Error("The backup stopped", why);
            _log.Add($"Backing up {chosen.Title} stopped: {why} ({UserFacingText.FirstLine(ex.Message)})");
            WriteReport(files, chosen, started, $"Stopped: {why} Windows said: {UserFacingText.FirstLine(ex.Message)}");
            _replacing = null;
            RefreshList();
            SelectCopy(files);
            SetDetails(files.Report);
            TaskbarState = TaskbarItemProgressState.Error;
            Notify(BackupNoticeKind.Stopped, $"Backing up {chosen.Title} stopped", why);
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
            _replacing = null;
            RefreshList();
            SelectCopy(files);
            SetDetails(files.Report);
            TaskbarState = TaskbarItemProgressState.None;
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

        if (Formats.ChdFor(backup.Files.IsCd))
        {
            Compress(backup, interim: true);
        }
    }

    // Before a copy that is to become a chd: its drive needs room for the image and, while it is compressed, for a chd up
    // to as big beside it. Asks when it may not have that; without the room the copy is kept as an iso or a bin and cue.
    private bool RoomToCompress(DriveDisc chosen, string folder, bool resume)
    {
        var cd = chosen.Disc.Media == DiscMedia.Cd;
        if (!Formats.ChdFor(cd))
        {
            return true;
        }

        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }

        var bytes = chosen.Disc.Sectors * (cd ? 2352L : DiscSectors.Size);
        if (free >= (resume ? 0 : bytes) + bytes)
        {
            return true;
        }

        return _confirm(new Question(
            "There may not be room to compress this copy",
            $"The copy of {chosen.Title} takes {Size(bytes)}, and while it is compressed its chd is made beside it, which can take as much again. "
                + $"The drive of the backup folder has {Size(free)} free. Without the room, the copy is kept as {(cd ? "a bin and cue" : "an iso")}, and Convert to CHD compresses it later.",
            "_Back up anyway",
            "Cancel",
            QuestionKind.Warn));
    }

    // Puts a complete copy in line to become a chd. An interim one (kept as an iso or bin only until its chd is made) is
    // marked, so compressing it starts again after Loadout was closed.
    private void Compress(DiscBackup backup, bool interim)
    {
        var files = backup.Files;
        if (IsCompressing(files.Name))
        {
            return;
        }

        if (interim)
        {
            try
            {
                File.WriteAllText(files.ChdPending, string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Add($"{files.Name} could not be marked to be compressed: {ex.Message}");
            }
        }

        _compressions.Add(new Compression(files, backup.Identity, interim));
        ShowCompressing();
        if (_compressRun is null || _compressRun.IsCompleted)
        {
            _compressRun = CompressAllAsync();
        }
    }

    private bool IsCompressing(string name) =>
        (_compressing is { } now && Same(now.Files.Name, name)) || _compressions.Any(c => Same(c.Files.Name, name));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task CompressAllAsync()
    {
        try
        {
            while (!_closed && _compressions.Count > 0)
            {
                var job = _compressions[0];
                _compressions.RemoveAt(0);
                _compressing = job;
                _compressingText = "Compressing…";
                using var stop = new CancellationTokenSource();
                _compressStop = stop;
                ShowCompressing();
                try
                {
                    await CompressOneAsync(job, stop.Token).ConfigureAwait(true);
                }
                finally
                {
                    _compressStop = null;
                    _compressing = null;
                }
            }
        }
        finally
        {
            if (!_closed)
            {
                RefreshList();
            }
        }
    }

    private async Task CompressOneAsync(Compression job, CancellationToken stop)
    {
        var files = job.Files;
        var kind = files.IsCd ? "bin" : "iso";
        var keptAs = files.IsCd ? "a bin and cue" : "an iso";
        var started = DateTime.Now;
        string result;
        try
        {
            DiscBackupCompression? made = null;
            if (!File.Exists(files.Chd))
            {
                _log.Add($"Compressing {files.Name} to a chd.");
                var progress = new Progress<double>(share =>
                {
                    var text = string.Create(CultureInfo.CurrentCulture, $"Compressing… {share * 100:0}%");
                    if (text != _compressingText && _compressing is not null)
                    {
                        _compressingText = text;
                        ShowCompressing();
                    }
                });
                var core = DiscBackupCompressor.CompressAsync(files, progress, stop);
                _compressCore = core;
                made = await core.ConfigureAwait(true);
            }

            var removed = await RemoveUncompressedAsync(job, stop).ConfigureAwait(true);
            result = made is null
                ? "The chd was made already" + removed
                : string.Create(CultureInfo.CurrentCulture, $"The chd is {Size(made.ChdBytes)}, {100.0 * made.ChdBytes / made.ImageBytes:0}% of the {kind}, and reads back the same{removed}");
            _log.Add($"Compressed {files.Name}: {result}");
            if (!_working && _cleaning is null)
            {
                Status = PageStatus.Ok("Compressed " + files.Name, result);
            }
        }
        catch (OperationCanceledException) when (_closed)
        {
            // Its mark stays: compressing it starts again when Loadout next starts.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            const int DiskFull = unchecked((int)0x80070070);
            const int HandleDiskFull = unchecked((int)0x80070027);
            result = ex switch
            {
                IOException { HResult: DiskFull or HandleDiskFull } =>
                    $"There was not enough room on its drive to compress it, so it is kept as {keptAs}. Convert to CHD compresses it once there is room.",
                OperationCanceledException => $"Compressing it stopped, so it is kept as {keptAs}.",
                InvalidDataException => $"Its chd did not read back the same as the copy, so it is kept as {keptAs}.",
                _ => $"It could not be compressed ({UserFacingText.FirstLine(ex.Message)}), so it is kept as {keptAs}.",
            };
            _log.Add($"{files.Name}: {result}");
            if (!_working && _cleaning is null)
            {
                Status = PageStatus.Warn(files.Name + " was not compressed", result);
            }
        }
        finally
        {
            _compressCore = null;
        }

        TryDelete(files.ChdPending);
        AppendReport(files, started, "Compressing to " + Path.GetFileName(files.Chd), result);
    }

    // The image the chd took the place of goes once nothing installs from it: deleted for good when it was only the copy
    // being made, sent to the Recycle Bin when it was kept as it was. Returns the end of the report's sentence.
    private async Task<string> RemoveUncompressedAsync(Compression job, CancellationToken stop)
    {
        var files = job.Files;
        var paths = files.Uncompressed.Where(File.Exists).ToList();
        if (paths.Count == 0)
        {
            return ".";
        }

        while (InUse(job, paths))
        {
            if (_compressingText != "Compressed; waits for the install")
            {
                _compressingText = "Compressed; waits for the install";
                ShowCompressing();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(true);
        }

        _installer.ForgetFiles(paths);
        var kind = files.IsCd ? "bin and cue" : "iso";
        try
        {
            if (job.Interim)
            {
                foreach (var path in paths)
                {
                    File.Delete(path);
                }

                return $": the {kind} it was made from is deleted.";
            }

            BackupRecycler.Recycle(paths);
            return $": the {kind} is in the Recycle Bin.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return $", but the {kind} could not be removed ({UserFacingText.FirstLine(ex.Message)}), so both are kept.";
        }
    }

    // An install on the PS2 this page started is of the same disc, or a row of the Install page's queue reads the files.
    private bool InUse(Compression job, IReadOnlyCollection<string> paths) =>
        _ps2.Any(install => install.Identity is { } of && job.Identity is { } identity && of.IsSameDisc(identity))
        || _installer.IsReading(paths);

    // The copy being compressed says how far it is, in place of its result; one waiting says so.
    private void ShowCompressing()
    {
        foreach (var row in Backups)
        {
            row.Activity = _compressing is { } now && Same(now.Files.Name, row.Name)
                ? _compressingText
                : _compressions.Any(c => Same(c.Files.Name, row.Name)) ? "Waiting to compress" : null;
        }

        OnPropertyChanged(nameof(CanConvert));
    }

    private void ConvertSelected()
    {
        if (CanConvert && Single is { } row)
        {
            _log.Add($"Converting {row.Name} to a chd.");
            Compress(row.Backup, interim: false);
        }
    }

    // Compressing that closing Loadout stopped starts again; any chd half written is removed first.
    private void ResumeCompressions()
    {
        var folder = _settings.BackupFolder;
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (var part in Directory.EnumerateFiles(folder, "*" + DiscBackupFiles.ChdExtension + ".part"))
            {
                var name = Path.GetFileName(part)[..^(DiscBackupFiles.ChdExtension + ".part").Length];
                if (!IsCompressing(name))
                {
                    TryDelete(part);
                }
            }

            foreach (var backup in DiscBackupLibrary.PendingCompression(folder))
            {
                if (backup.Result == DiscBackupResult.Incomplete)
                {
                    TryDelete(backup.Files.ChdPending);
                    continue;
                }

                Compress(backup, interim: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add("Copies left to compress could not be looked for: " + ex.Message);
        }
    }

    private void AppendReport(DiscBackupFiles files, DateTime started, string what, string result)
    {
        try
        {
            File.AppendAllText(files.Report, string.Create(CultureInfo.InvariantCulture, $"== {what}, started {started:yyyy-MM-dd HH:mm:ss}\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {result}\n\n"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add($"The report of {files.Name} could not be written: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
        var replaced = Replace(ref backup);
        IsWorking = false;
        var name = backup.Files.Name;
        (Status, var line) = backup.Result switch
        {
            DiscBackupResult.MatchesRedump => (
                PageStatus.Ok("Backed up " + name, "It matches Redump: the copy is exact. Saved in " + backup.Files.Folder + "." + replaced),
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
        _log.Add($"Backed up {name}: {CopySummary(backup.Files)}{line}.");
        WriteReport(backup.Files, chosen, started, "Complete: " + line + ".");
        RefreshList();
        SelectCopy(backup.Files);
        SetDetails(backup.Files.Report);
        TaskbarState = TaskbarItemProgressState.None;
        if (_keepGoing)
        {
            Eject(chosen.Disc.Drive);
        }

        Notify(
            BackupNoticeKind.BackedUp,
            backup.Result == DiscBackupResult.MatchesRedump ? $"{chosen.Title} backed up, matches Redump" : Status.Headline,
            (backup.Result switch
            {
                DiscBackupResult.MatchesRedump => "The copy is exact: " + name + ".",
                DiscBackupResult.DoesNotMatchRedump => "Another printing, or a read error the drive did not notice: Back up again reads it once more.",
                DiscBackupResult.NotInRedump => "Redump doesn't list this disc, so the copy can't be checked against it.",
                _ => "Redump's list could not be had; the copy is checked when Loadout next gets it.",
            }) + (_keepGoing ? " Insert the next disc." : string.Empty));
        return backup;
    }

    // How the copy went, for its line on the Logs page, such as "99.3% on the first pass, 2 weak areas, 2 cleanings, ".
    private static string CopySummary(DiscBackupFiles files)
    {
        try
        {
            var map = DiscCopyMap.Load(files.Map);
            return DiscCopySummary.Describe(map.Ranges, map.Sectors, map.Cleanings) + ", ";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return string.Empty;
        }
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
            TaskbarState = TaskbarItemProgressState.None;
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
        TaskbarState = TaskbarItemProgressState.Paused;
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
        TaskbarProgress = percent / 100;
        TaskbarState = status.Stalled ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
        if (status.Stalled && !_stallTold)
        {
            Notify(BackupNoticeKind.NeedsCleaning, title + " needs cleaning to go on", "Little has been read for a while. Pause to clean the disc, and the copy goes on where it was.");
        }

        _stallTold = status.Stalled;
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
                ? $"Reading normally{SpeedAsX(status.BytesPerSecond)} · {speed} · about {Left(status.UntriedSectors * (_selected?.Disc.Media == DiscMedia.Cd ? 2352 : DiscSectors.Size) / status.BytesPerSecond)} left"
                : "Reading normally",
            DiscCopyPhase.Sweep when status.PieceSectors < 128 => $"Weak area at {at}: reading it in small pieces",
            DiscCopyPhase.Sweep when status.Backwards => $"Pass {status.Sweep + 1}: going back over {Size(status.MissingSectors * DiscSectors.Size)} from the end",
            DiscCopyPhase.Sweep => $"Pass {status.Sweep + 1}: going over {Size(status.MissingSectors * DiscSectors.Size)} again",
            _ => string.Empty,
        };
    }

    // The speed Loadout measures, as the drive's x, and the drive's top speed for this disc, such as " · 3.8x of 8x". The
    // drive's own curve isn't used here: it can claim less than it reads (2.8x while reading 5.3 MB/s, a 3.8x).
    private string SpeedAsX(double bytesPerSecond)
    {
        if (_selected?.Disc is not { } disc || bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        var oneX = 1000 * (disc.Speeds?.OneX ?? (disc.Media == DiscMedia.Cd ? 176.4 : 1385.0));
        var now = string.Create(CultureInfo.CurrentCulture, $" · {bytesPerSecond / oneX:0.0}x");
        if (disc.Speeds is not { } speeds)
        {
            return now;
        }

        var max = speeds.MaxKbPerSecond > 0 ? speeds.MaxKbPerSecond : speeds.Stretches.Select(s => Math.Max(s.StartKbPerSecond, s.EndKbPerSecond)).DefaultIfEmpty(0).Max();
        return max > 0 ? now + " of " + speeds.X(max, CultureInfo.CurrentCulture) : now;
    }

    private static string Left(double seconds) => seconds switch
    {
        < 55 => string.Create(CultureInfo.CurrentCulture, $"{Math.Max(5, Math.Ceiling(seconds / 5) * 5)} s"),
        < 90 => "1 min",
        < 3600 => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(seconds / 60)} min"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{seconds / 3600:0.0} h"),
    };

    private void SetMap(IReadOnlyList<DiscMapRange> ranges, long sectors, DiscMedia media, long layer0, long current, IReadOnlyList<double>? cleanedRingsMm = null)
    {
        var (install, check) = MarkerSectors();
        Map = new DiscMapView(ranges, sectors, media, layer0, current) { CleanedRingsMm = cleanedRingsMm ?? [], InstallSector = install, CheckSector = check };
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
        if (Single is { Result: not DiscBackupResult.Incomplete } row)
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
            (result, notStarted) = await _installer.InstallNowAsync(item, open, status => ShowInstall(install, status), Ticks.Options, install.Stop.Token).ConfigureAwait(true);
        }
        finally
        {
            _ps2.Remove(install);
            OnPropertyChanged(nameof(IsInstallingOnPs2));
            OnPropertyChanged(nameof(CanSkipCheck));
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
                BayInstallOutcome.Installed => $"{title} is installed on the PS2{(result.PartitionName is { } name ? " as " + name : string.Empty)}."
                    + (result.CheckSkipped ? " It was not checked: its check was skipped." : string.Empty),
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

        // The last report after a check is not more sending; the result line follows it.
        if (!status.Checking && install.SawCheck)
        {
            return;
        }

        if (status.Checking)
        {
            install.SawCheck = true;
            install.Sector = (long)(status.TotalBytes / DiscSectors.Size);
            if (status.CheckedBytes is { } checkedBytes)
            {
                // Loadout Bay 0.11.0 and later say how far the check is: the CHECK marker walks the bar meanwhile.
                var now = DateTime.UtcNow;
                install.FirstChecked ??= (now, checkedBytes);
                var first = install.FirstChecked.Value;
                var seconds = (now - first.At).TotalSeconds;
                var pace = seconds >= 2 ? (checkedBytes - first.Bytes) / seconds : 0;
                InstallLine = $"On the PS2: checking {Size((long)checkedBytes)} of {Size((long)status.TotalBytes)}"
                    + (pace > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {pace / 1_000_000:0.0} MB/s · about {Left((status.TotalBytes - checkedBytes) / pace)} left") : string.Empty);
                install.CheckSector = (long)(checkedBytes / DiscSectors.Size);
            }
            else
            {
                InstallLine = "On the PS2: all sent; Loadout Bay reads it back to check it…";
            }
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
            install.CheckSector = -1;
        }

        OnPropertyChanged(nameof(CanSkipCheck));
        RefreshMarker();
    }

    /// <summary>The install this page started is being checked, and Loadout Bay says how far: the check can be skipped.</summary>
    public bool CanSkipCheck => _ps2.FirstOrDefault() is { CheckSector: >= 0 };

    /// <summary>Skips the rest of that check: the game stays on the PS2, not checked.</summary>
    public ICommand SkipCheckCommand { get; }

    // The bar's markers for the install that follows the disc on the map: how far it has got (PS2), or while it is
    // checked, how far its check has got (CHECK), which then takes the PS2 marker's place; -1 when none shows.
    private (long Install, long Check) MarkerSectors()
    {
        var shown = _selected?.Disc.Identity;
        var install = shown is null ? null : _ps2.FirstOrDefault(i => i.Feed is not null && i.Identity is { } id && id.IsSameDisc(shown));
        return install is null ? (-1, -1)
            : install.CheckSector >= 0 ? (-1, install.CheckSector)
            : (install.Sector, -1);
    }

    private void RefreshMarker()
    {
        var (sector, check) = MarkerSectors();
        if (_map.Sectors > 0 && (_map.InstallSector != sector || _map.CheckSector != check))
        {
            Map = _map with { InstallSector = sector, CheckSector = check };
        }
    }

    private void OpenFolder()
    {
        var folder = _settings.BackupFolder;
        if (Single is { } row && File.Exists(row.Backup.Files.Image))
        {
            Process.Start("explorer.exe", "/select,\"" + row.Backup.Files.Image + "\"");
        }
        else if (folder.Length > 0 && Directory.Exists(folder))
        {
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }
    }

    private void ShowDetails()
    {
        if (_detailsReport is { } report && File.Exists(report))
        {
            Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
        }
    }

    private void SaveReport()
    {
        if (Single is not { } row)
        {
            return;
        }

        if (!File.Exists(row.Backup.Files.Report))
        {
            Status = PageStatus.Idle("No report for " + row.Name, "It was made before reports were kept, or its report was removed.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the report of " + row.Name,
            FileName = Path.GetFileName(row.Backup.Files.Report),
            Filter = "Text files (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.Copy(row.Backup.Files.Report, dialog.FileName, overwrite: true);
            _log.Add($"Saved the report of {row.Name} as {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = PageStatus.Error("The report could not be saved", ex.Message);
        }
    }

    private void ShowReport()
    {
        if (Single is { } row && File.Exists(row.Backup.Files.Report))
        {
            Process.Start(new ProcessStartInfo(row.Backup.Files.Report) { UseShellExecute = true });
        }
        else if (Single is { } other)
        {
            Status = PageStatus.Idle("No report for " + other.Name, "It was made before reports were kept, or its report was removed.");
        }
    }

    /// <summary>An install on the PS2 this page started: one following a copy (with a feed), or one of a finished copy.</summary>
    // A copy in line to become a chd. An interim one was only an iso or bin until its chd is made, and that goes for good.
    private sealed record Compression(DiscBackupFiles Files, DiscIdentity? Identity, bool Interim);

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

        /// <summary>While Loadout Bay checks it and says how far: the sector its check has reached; -1 otherwise.</summary>
        public long CheckSector { get; set; } = -1;

        public (DateTime At, ulong Bytes)? FirstWritten { get; set; }

        public (DateTime At, ulong Bytes)? FirstChecked { get; set; }

        /// <summary>Everything was sent and Loadout Bay checks it, or did.</summary>
        public bool SawCheck { get; set; }

        public void Dispose()
        {
            Stop.Dispose();
            Feed?.Dispose();
        }
    }
}
