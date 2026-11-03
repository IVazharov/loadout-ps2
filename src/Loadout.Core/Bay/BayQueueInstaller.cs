using System.Globalization;
using Loadout.Artwork;
using Loadout.Core;
using Loadout.Images;
using Loadout.Library;

namespace Loadout.Bay;

/// <summary>What happened to one game of an install queue.</summary>
public enum BayInstallOutcome
{
    Installed,
    AlreadyOnPs2,
    IncompleteOnPs2,
    DoesNotFit,
    NotReady,
    CheckFailed,
    Failed,
    Cancelled,
    NotRun,
}

public sealed record BayQueueResult(QueuePreviewItem Item, BayInstallOutcome Outcome, string Message)
{
    /// <summary>True for a game installed with artwork asked for, when the PS2 ended up with none of it.</summary>
    public bool ArtworkMissing { get; init; }
}

/// <summary>The game being installed: its place in the queue, and how far its copy is.</summary>
public sealed record BayQueueProgress(int Index, int Count, QueuePreviewItem Item, BayInstallStatus Status);

/// <summary>
/// The queue's results in order, and the PS2's library read again at the end, or null when the
/// connection was lost before it could be read.
/// </summary>
public sealed record BayQueueRun(IReadOnlyList<BayQueueResult> Results, BayLibrary? Library);

/// <summary>
/// Installs a queue of scanned images through one connection to Loadout Bay. A game already on
/// the PS2, whole or not, is skipped by its disc ID, and so is one that cannot fit in the free space.
/// Only a Ready image is sent. Cancelling stops the game being copied, which stays on the PS2 as an
/// incomplete install, and leaves the rest of the queue unrun. With <c>checkCopies</c>, Loadout Bay
/// reads each game back and compares it before the game counts as installed. With <c>memoryCards</c>,
/// each game installed gets the memory card its plan names (<see cref="BayMemoryCards"/>); a card that
/// cannot be made is reported in the game's result and does not undo the install. With <c>artwork</c>,
/// each game installed gets its files from that cache, unless OPL's ART folder already has them; missing
/// or refused artwork is reported in the same way. It is downloaded before this runs, as a connection
/// left idle while it downloads would be closed.
/// </summary>
public static class BayQueueInstaller
{
    /// <summary>The INSTALL_BEGIN request for a Ready image, or null when it cannot be installed as it is.</summary>
    public static BayInstallRequest? RequestFor(QueuePreviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Status != QueueItemStatus.Ready || item.DataFile is null || item.Layer0Sectors == 0
            || !DiscId.TryParse(item.DiscId, out var discId))
        {
            return null;
        }

        return new BayInstallRequest(
            discId.Canonical,
            discId.StartupFileName,
            item.DetectedTitle,
            item.Layer0Sectors,
            item.Layer1Sectors,
            item.HdlDiscType);
    }

    public static async Task<BayQueueRun> RunAsync(
        string host,
        IReadOnlyList<QueuePreviewItem> items,
        IProgress<BayQueueProgress>? progress = null,
        bool checkCopies = false,
        int port = BayWire.Port,
        int dataPort = BayWire.DataPort,
        MemoryCardRequest? memoryCards = null,
        ArtworkCache? artwork = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (!bay.Hello.HasHardDisk)
        {
            throw new LoadoutBayException(BayWire.DescribeStatus(BayWire.StatusNoDevice), BayWire.StatusNoDevice);
        }

        // Read fresh, so a game installed since the last Connect is not sent twice.
        var onPs2 = new Dictionary<string, InstalledGameRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in await bay.ListGamesAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = game.ToInstalledGameRow();
            if (row.DiscId.Length > 0)
            {
                onPs2.TryAdd(row.DiscId, row);
            }
        }

        var space = await bay.GetFreeSpaceAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<BayQueueResult>();
        var cardsMade = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? artOnPs2 = null;
        var connected = true;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!connected || cancellationToken.IsCancellationRequested)
            {
                results.Add(new BayQueueResult(item, BayInstallOutcome.NotRun, Name(item) + " was not installed: the queue stopped before it."));
                continue;
            }

            var request = RequestFor(item) is { } ready ? ready with { CheckCopy = checkCopies } : null;
            if (request is null)
            {
                results.Add(new BayQueueResult(item, BayInstallOutcome.NotReady, Name(item) + " was skipped: it is not ready to install (" + item.StatusLabel + ")."));
                continue;
            }

            if (onPs2.TryGetValue(request.DiscId, out var present))
            {
                results.Add(present.Incomplete
                    ? new BayQueueResult(item, BayInstallOutcome.IncompleteOnPs2, Name(item) + " was skipped: an incomplete install of it is on the PS2. Remove it in Installed Games, then install again.")
                    : new BayQueueResult(item, BayInstallOutcome.AlreadyOnPs2, Name(item) + " was skipped: it is already on the PS2 as " + present.PartitionName + "."));
                continue;
            }

            // Each 2048-byte sector takes four of the driver's 512-byte sectors. Partitions come in
            // fixed sizes, so a game can still not fit; Loadout Bay then refuses it before writing.
            var needed = (ulong)request.TotalBytes / FreeSpace.ApaSectorSizeBytes;
            if (needed > space.FreeSectors)
            {
                results.Add(new BayQueueResult(item, BayInstallOutcome.DoesNotFit, Name(item) + " was skipped: it needs " + Gb(needed * FreeSpace.ApaSectorSizeBytes) + " GB and the PS2 has " + Gb(space.FreeBytes) + " GB free."));
                continue;
            }

            var count = items.Count;
            var report = new Relay(status => progress?.Report(new BayQueueProgress(index, count, item, status)));
            try
            {
                using var sectors = IsoUserDataStream.Open(item.DataFile!);
                var start = await bay.InstallAsync(request, sectors, report, dataPort, cancellationToken).ConfigureAwait(false);
                onPs2[request.DiscId] = new InstalledGameRow(start.PartitionName, request.Title, request.DiscId, 0, 0, 0, 0, 0);
                var message = Name(item) + " was installed as " + start.PartitionName + ".";
                if (memoryCards?.Plans.FirstOrDefault(plan => plan.DiscId == request.DiscId) is { } plan)
                {
                    try
                    {
                        message += " " + await BayMemoryCards.GiveCardAsync(
                            bay, plan, memoryCards.SizeMegabytes, memoryCards.ReplaceExisting, cardsMade, DateTimeOffset.Now, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (LoadoutBayException ex)
                    {
                        message += " Its memory card could not be made: " + ex.Message;
                        connected = ex.Status != 0;
                    }
                }

                var artworkMissing = false;
                if (artwork is not null && connected)
                {
                    string note;
                    (note, artworkMissing, artOnPs2, connected) = await SendArtworkAsync(bay, item, artwork, artOnPs2).ConfigureAwait(false);
                    message += " " + note;
                }

                results.Add(new BayQueueResult(item, BayInstallOutcome.Installed, message) { ArtworkMissing = artworkMissing });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new BayQueueResult(item, BayInstallOutcome.Cancelled, Name(item) + " was cancelled. Its partial copy is on the PS2 as an incomplete install; remove it in Installed Games."));
                continue;
            }
            catch (LoadoutBayException ex)
            {
                var outcome = ex.Status switch
                {
                    BayWire.StatusExists => BayInstallOutcome.AlreadyOnPs2,
                    BayWire.StatusNoSpace or BayWire.StatusTooLarge => BayInstallOutcome.DoesNotFit,
                    BayWire.StatusCopyDiffers => BayInstallOutcome.CheckFailed,
                    _ => BayInstallOutcome.Failed,
                };
                results.Add(new BayQueueResult(item, outcome, Name(item) + (outcome == BayInstallOutcome.Failed ? " failed: " : " was skipped: ") + ex.Message));

                // Without a status from Loadout Bay, the failure was the connection itself.
                connected = ex.Status != 0;
            }
            catch (IOException ex)
            {
                results.Add(new BayQueueResult(item, BayInstallOutcome.Failed, Name(item) + " failed: its image could not be read. " + ex.Message));
            }

            if (connected)
            {
                space = await bay.GetFreeSpaceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (!connected)
        {
            return new BayQueueRun(results, null);
        }

        var games = await bay.ListGamesAsync(CancellationToken.None).ConfigureAwait(false);
        return new BayQueueRun(results, new BayLibrary(bay.Hello, space, games.Select(game => game.ToInstalledGameRow()).ToList()));
    }

    // Not cancellable, like the memory card: the game is on the PS2 by now, and this is small.
    private static async Task<(string Note, bool Missing, HashSet<string>? OnPs2, bool Connected)> SendArtworkAsync(
        LoadoutBayClient bay,
        QueuePreviewItem item,
        ArtworkCache artwork,
        HashSet<string>? onPs2)
    {
        if (!DiscId.TryParse(item.DiscId, out var discId))
        {
            return ("It has no disc ID to find artwork by.", true, onPs2, true);
        }

        try
        {
            onPs2 ??= (await bay.ListFilesAsync(BayFolder.Art, CancellationToken.None).ConfigureAwait(false)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
            var result = await BayArtwork.SendGameAsync(bay, new ArtworkGame(discId, item.DetectedTitle), artwork, onPs2, CancellationToken.None).ConfigureAwait(false);
            if (result.Plan.WithoutArt.Count > 0)
            {
                return ("No artwork could be downloaded for it.", true, onPs2, true);
            }

            var note = string.Create(CultureInfo.CurrentCulture, $"Its artwork was sent: {result.Sent.Count} of {result.Plan.ToSend.Count} files");
            note += result.Plan.AlreadyOnPs2 > 0 ? string.Create(CultureInfo.CurrentCulture, $", {result.Plan.AlreadyOnPs2} already on the PS2.") : ".";
            if (result.Failed.Count > 0)
            {
                note += " Refused: " + string.Join("; ", result.Failed.Select(failure => failure.File.RemoteName + ": " + failure.Reason));
            }

            return (note, result.Sent.Count == 0 && result.Plan.AlreadyOnPs2 == 0, onPs2, true);
        }
        catch (LoadoutBayException ex)
        {
            return ("Its artwork could not be sent: " + ex.Message, true, onPs2, ex.Status != 0);
        }
    }

    private static string Name(QueuePreviewItem item) =>
        string.IsNullOrWhiteSpace(item.DetectedTitle) ? Path.GetFileName(item.SourceFile) : item.DetectedTitle;

    private static string Gb(ulong bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture);

    // Reports straight away, on whichever thread the install reports from.
    private sealed class Relay(Action<BayInstallStatus> report) : IProgress<BayInstallStatus>
    {
        public void Report(BayInstallStatus value) => report(value);
    }
}
