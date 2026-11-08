using System.Globalization;
using Loadout.Core;
using Loadout.Updates;

namespace Loadout.App;

/// <summary>One release's notes in the update dialog.</summary>
public sealed record NotesSection(string Version, IReadOnlyList<NoteLine> Lines);

public enum UpdatePhase
{
    Ready,
    Working,
    Failed,
}

/// <summary>
/// The update dialog: what is new, the start check's setting, and Update now, which downloads the release,
/// checks it and swaps the files. The dialog restarts Loadout once <see cref="UpdateAsync"/> returns true.
/// </summary>
public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateInstaller? _installer;
    private readonly string? _cannotUpdate;
    private readonly Func<bool> _busy;
    private readonly OperationLog _log;
    private UpdatePhase _phase = UpdatePhase.Ready;
    private string _status = string.Empty;
    private double _progress;
    private CancellationTokenSource? _run;

    /// <param name="installer">Null when this Loadout can't replace itself; <paramref name="cannotUpdate"/> says why.</param>
    /// <param name="busy">True while an install or another task runs, which an update must not cut short.</param>
    public UpdateViewModel(UpdateOffer offer, SettingsViewModel settings, UpdateInstaller? installer, string? cannotUpdate, Func<bool> busy, OperationLog log)
    {
        Offer = offer ?? throw new ArgumentNullException(nameof(offer));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _installer = installer;
        _cannotUpdate = installer is null ? cannotUpdate ?? "This Loadout can't replace itself." : null;
        _busy = busy ?? throw new ArgumentNullException(nameof(busy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Notes = offer.Newer.Select(release => new NotesSection(release.Version.ToString(), ReleaseNotesText.Lines(release.Notes))).ToList();
        _status = _cannotUpdate ?? string.Empty;
    }

    public UpdateOffer Offer { get; }

    public SettingsViewModel Settings { get; }

    public string Title => "Loadout " + Offer.Version + " is out";

    public string Subtitle => "You have " + ProductInfo.Version + ". Update now downloads it ("
        + (Offer.Download.Size / 1024.0 / 1024.0).ToString("0.0", CultureInfo.CurrentCulture)
        + " MB), checks its signature, and restarts Loadout as the new version. Your settings and games stay as they are.";

    public IReadOnlyList<NotesSection> Notes { get; }

    public string ReleasePage => Offer.Release.PageUrl.AbsoluteUri;

    public UpdatePhase Phase
    {
        get => _phase;
        private set
        {
            if (SetField(ref _phase, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(StatusIsError));
                OnPropertyChanged(nameof(DismissLabel));
            }
        }
    }

    public bool IsWorking => Phase == UpdatePhase.Working;

    /// <summary>True from the moment the files are being swapped: the dialog can't be closed or cancelled then.</summary>
    public bool IsSwapping { get; private set; }

    public bool StatusIsError => Phase == UpdatePhase.Failed;

    /// <summary>Progress, or why Update now can't run, or what went wrong.</summary>
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>0 to 100 while downloading.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public bool CanUpdate => Phase != UpdatePhase.Working && _installer is not null;

    public string DismissLabel => Phase switch
    {
        UpdatePhase.Working => "_Cancel",
        UpdatePhase.Failed => "_Close",
        _ => "_Later",
    };

    /// <summary>Stops a download that is still running; the swap itself can't be stopped.</summary>
    public void Cancel()
    {
        if (!IsSwapping)
        {
            _run?.Cancel();
        }
    }

    public void Dispose() => _run?.Dispose();

    /// <summary>Downloads, checks and swaps in the new version. True when Loadout should now restart.</summary>
    public async Task<bool> UpdateAsync()
    {
        if (_installer is null)
        {
            return false;
        }

        _run?.Dispose();
        _run = new CancellationTokenSource();
        var cancellationToken = _run.Token;

        if (_busy())
        {
            Phase = UpdatePhase.Failed;
            Status = "An install or another task is running. Update when it has finished.";
            return false;
        }

        if (!_installer.CanWriteAppFolder())
        {
            Phase = UpdatePhase.Failed;
            Status = "Loadout can't write to its folder (" + _installer.AppFolder + "), so it can't replace itself. Get the new version from its release page.";
            _log.Add("Update to " + Offer.Version + " stopped: Loadout's folder is read-only.");
            return false;
        }

        Phase = UpdatePhase.Working;
        Progress = 0;
        var total = Offer.Download.Size;
        Status = "Downloading " + Offer.Download.Name + "…";
        _log.Add("Updating to Loadout " + Offer.Version + ": downloading " + Offer.Download.DownloadUrl + ".");
        try
        {
            var progress = new Progress<long>(done =>
            {
                if (total > 0)
                {
                    Progress = 100.0 * done / total;
                    Status = "Downloading… " + Megabytes(done) + " of " + Megabytes(total) + " MB";
                }
            });
            var zip = await _installer.DownloadAsync(Offer, progress, cancellationToken);
            Status = "Checking the download's signature…";
            var update = await Task.Run(() => _installer.Prepare(zip, Offer.Version), cancellationToken);
            Status = "Replacing Loadout's files…";
            IsSwapping = true;
            await Task.Run(() => _installer.Apply(update), CancellationToken.None);
            _log.Add("Loadout " + Offer.Version + " is in place; restarting.");
            Status = "Restarting Loadout…";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Phase = UpdatePhase.Ready;
            Status = "The update was cancelled. Nothing was changed.";
            _log.Add("Update to " + Offer.Version + " cancelled.");
            return false;
        }
        catch (UpdateInstallException ex)
        {
            IsSwapping = false;
            Phase = UpdatePhase.Failed;
            Status = ex.Message;
            _log.Add("Update to " + Offer.Version + " failed: " + ex.Message);
            return false;
        }
    }

    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.CurrentCulture);
}
