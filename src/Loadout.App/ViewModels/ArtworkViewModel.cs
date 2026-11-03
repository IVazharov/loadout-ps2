using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Input;
using Loadout.Artwork;
using Loadout.Bay;
using Loadout.Core;
using Loadout.Library;

namespace Loadout.App;

/// <summary>
/// The Artwork page. Download fetches the covers, icons, backgrounds, and screenshots of the installed
/// games into the artwork cache; Send to PS2 writes the cached files the PS2 lacks into OPL's ART folder
/// through Loadout Bay, or every one of them when the user asks to replace what is there.
/// </summary>
public sealed class ArtworkViewModel : ObservableObject, IDisposable
{
    private readonly ArtworkCache _cache;
    private readonly ArtworkDownloader _downloader;
    private readonly Func<string> _hostFor;
    private readonly Func<IReadOnlyList<InstalledGameRow>> _installed;
    private readonly OperationLog _log;
    private readonly AsyncRelayCommand _download;
    private readonly AsyncRelayCommand _send;
    private CancellationTokenSource? _work;
    private bool _replaceExisting;
    private int _cached;
    private PageStatus _status;

    public ArtworkViewModel(
        ArtworkCache cache,
        ArtworkDownloader downloader,
        Func<string> hostFor,
        Func<IReadOnlyList<InstalledGameRow>> installed,
        OperationLog log)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _hostFor = hostFor ?? throw new ArgumentNullException(nameof(hostFor));
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _download = new AsyncRelayCommand(DownloadAsync);
        _send = new AsyncRelayCommand(SendAsync);
        CancelCommand = new RelayCommand(() => _work?.Cancel());
        _status = IdleStatus();
    }

    public ObservableCollection<ArtworkRow> Games { get; } = new();

    public ICommand DownloadCommand => _download;

    public ICommand SendCommand => _send;

    public ICommand CancelCommand { get; }

    public bool IsIdle => _work is null;

    public bool IsBusy => _work is not null;

    /// <summary>False until there are installed games, when the page shows its empty state instead of the table.</summary>
    public bool HasGames => Games.Count > 0;

    /// <summary>Send also replaces files the PS2 already has. Each new file is whole before the old one goes.</summary>
    public bool ReplaceExisting
    {
        get => _replaceExisting;
        set => SetField(ref _replaceExisting, value);
    }

    public PageStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>The page header's summary, such as "38 OF 42 CACHED".</summary>
    public string Summary => Games.Count == 0
        ? ""
        : string.Create(CultureInfo.CurrentCulture, $"{_cached} OF {Games.Count} CACHED");

    /// <summary>Stops a download or send that is still running. The window calls this when it closes.</summary>
    public void Dispose()
    {
        _work?.Cancel();
        _download.Dispose();
        _send.Dispose();
    }

    /// <summary>Shows the installed games with what the cache holds for each. The page calls this when it opens.</summary>
    public void Refresh()
    {
        Games.Clear();
        _cached = 0;
        foreach (var game in Targets())
        {
            var kinds = OplArtCatalog.Kinds.Count(kind => _cache.TryGet(game.DiscId, kind) is not null);
            _cached += kinds > 0 ? 1 : 0;
            Games.Add(new ArtworkRow(
                game.DiscId.Canonical,
                game.Title,
                _cache.CoverOrIconPath(game.DiscId.Canonical),
                kinds == 0 ? "Not downloaded" : kinds.ToString(CultureInfo.CurrentCulture) + " of " + OplArtCatalog.Kinds.Count.ToString(CultureInfo.CurrentCulture) + " kinds cached"));
        }

        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(Summary));
        // A result stays until the next download or send; only the idle line follows the list.
        if (IsIdle && Status.Kind == StatusKind.Idle)
        {
            Status = IdleStatus();
        }
    }

    private PageStatus IdleStatus() =>
        Games.Count == 0
            ? PageStatus.Idle("No installed games yet", "Connect on Installed Games first.")
            : PageStatus.Idle(
                string.Create(CultureInfo.CurrentCulture, $"Artwork is cached for {_cached} of {Games.Count} games"),
                _cached < Games.Count ? "Download artwork fetches what is missing." : "Send to PS2 copies it into OPL's ART folder.");

    // Whole games with a disc ID, once each: art is named after the disc, not the partition.
    private List<ArtworkGame> Targets() =>
        _installed()
            .Where(BayGameChanges.CanEdit)
            .Select(row => DiscId.TryParse(row.DiscId, out var discId) ? new ArtworkGame(discId, row.Title) : null)
            .OfType<ArtworkGame>()
            .DistinctBy(game => game.DiscId)
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private async Task DownloadAsync()
    {
        var games = Targets();
        if (!IsIdle || games.Count == 0)
        {
            Status = games.Count == 0 ? IdleStatus() : Status;
            return;
        }

        using var work = Begin();
        var withArt = 0;
        try
        {
            for (var i = 0; i < games.Count; i++)
            {
                Status = PageStatus.Busy(
                    "Downloading artwork",
                    string.Create(CultureInfo.CurrentCulture, $"{games[i].Title} ({i + 1} of {games.Count})"),
                    i * 100.0 / games.Count);
                var cached = await _downloader.CollectAsync(games[i].DiscId, work.Token);
                withArt += cached.Count > 0 ? 1 : 0;
            }

            Status = PageStatus.Ok(
                string.Create(CultureInfo.CurrentCulture, $"Artwork is cached for {withArt} of {games.Count} games"),
                withArt < games.Count ? "Games without it have none online." : "Send to PS2 copies it into OPL's ART folder.");
        }
        catch (OperationCanceledException)
        {
            Status = PageStatus.Warn("Download cancelled", "What was downloaded stays cached.");
        }
        catch (HttpRequestException ex)
        {
            Status = PageStatus.Error("Artwork wasn't downloaded", ex.Message);
        }
        finally
        {
            End();
        }

        _log.Add(Status.LogLine);
    }

    private async Task SendAsync()
    {
        var host = _hostFor().Trim();
        var games = Targets();
        if (!IsIdle || host.Length == 0 || games.Count == 0)
        {
            Status = host.Length == 0 ? PageStatus.NoAddress : games.Count == 0 ? IdleStatus() : Status;
            return;
        }

        using var work = Begin();
        const string Sending = "Sending artwork to the PS2";
        Status = PageStatus.Busy(Sending, "Asking Loadout Bay which artwork the PS2 has.");
        var progress = new Progress<ArtworkSendProgress>(step =>
        {
            var percent = step.BytesTotal == 0 ? 100 : step.BytesDone * 100.0 / step.BytesTotal;
            Status = PageStatus.Busy(
                Sending,
                step.CurrentFile.Length == 0
                    ? Status.Detail
                    : string.Create(CultureInfo.CurrentCulture, $"File {step.FilesDone + 1} of {step.FilesTotal}: {step.CurrentFile} · {Mb(step.BytesDone)} of {Mb(step.BytesTotal)} MB"),
                percent);
        });
        try
        {
            var result = await BayArtwork.SendAsync(host, games, _cache, ReplaceExisting, progress, cancellationToken: work.Token);
            foreach (var failure in result.Failed)
            {
                _log.Add(failure.File.RemoteName + " was not sent. " + failure.Reason);
            }

            Status = SendStatus(result);
        }
        catch (OperationCanceledException)
        {
            Status = PageStatus.Warn("Sending cancelled", "The file being sent was left as it was on the PS2.");
        }
        catch (LoadoutBayException ex)
        {
            Status = PageStatus.Error("Artwork wasn't sent", PageStatus.BayReason(ex));
        }
        finally
        {
            End();
        }

        _log.Add(Status.LogLine);
    }

    private static PageStatus SendStatus(ArtworkSendResult result)
    {
        var plan = result.Plan;
        var headline = plan.ToSend.Count == 0
            ? "The PS2 already has all the cached artwork"
            : string.Create(CultureInfo.CurrentCulture, $"Sent {result.Sent.Count} of {plan.ToSend.Count} files");
        var detail = new List<string>();
        if (plan.ToSend.Count > 0)
        {
            detail.Add(Mb(plan.Bytes) + " MB.");
        }

        if (plan.AlreadyOnPs2 > 0)
        {
            detail.Add(string.Create(CultureInfo.CurrentCulture, $"{plan.AlreadyOnPs2} files were already on the PS2 and were kept."));
        }

        if (plan.WithoutArt.Count > 0)
        {
            detail.Add(string.Create(CultureInfo.CurrentCulture, $"{plan.WithoutArt.Count} games have no cached artwork: Download first."));
        }

        if (result.Failed.Count > 0)
        {
            detail.Add(string.Create(CultureInfo.CurrentCulture, $"{result.Failed.Count} files were refused: see Logs."));
            return PageStatus.Warn(headline, string.Join(" ", detail));
        }

        return PageStatus.Ok(headline, string.Join(" ", detail));
    }

    private CancellationTokenSource Begin()
    {
        _work = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsBusy));
        return _work;
    }

    private void End()
    {
        _work = null;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsBusy));
        Refresh();
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture);
}

/// <summary>A game on the Artwork page: its picture from the cache, if any, and what the cache holds.</summary>
public sealed record ArtworkRow(string DiscId, string Title, string? ArtPath, string Cached)
{
    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => Title;
}
