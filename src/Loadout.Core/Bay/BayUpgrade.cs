using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Loadout.Bay;

public enum BayUpgradeStage
{
    Sending,
    Starting,
    WaitingForRestart,
    Saving,
}

/// <param name="Version">The version the new copy names in its greeting.</param>
/// <param name="Saved">
/// Whether the new copy saved itself over the file it was started from. False when saving was not asked
/// for, or when it failed, such as for a copy not started from a USB drive or the hard disk, or for want of
/// room there; the new copy runs either way, and the old file stays whole.
/// </param>
/// <param name="NotSaved">Why a save that was asked for failed, in a sentence; null otherwise.</param>
public sealed record BayUpgradeResult(string Version, bool Saved, string? NotSaved = null);

/// <summary>OPL's partition's free bytes, and the bytes saving a new Loadout Bay there takes, when they fall short.</summary>
public sealed record BayUpgradeRoom(long FreeBytes, long NeededBytes);

/// <summary>
/// Puts a new Loadout Bay on the PS2 over the network. The running copy takes the ELF and starts it
/// from memory with a fresh run tag; once a copy answers with that tag, it gets the ELF again and
/// saves it over the file it was started from, keeping the old one as a backup.
/// </summary>
public static class BayUpgrade
{
    /// <summary>The old copy waits a second before its IOP reset; connecting before then reaches nobody.</summary>
    internal static TimeSpan FirstWait { get; set; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    public static async Task<BayUpgradeResult> RunAsync(
        string host,
        ReadOnlyMemory<byte> elf,
        bool save,
        IProgress<BayUpgradeStage>? stage = null,
        int port = BayWire.Port,
        TimeSpan? restartTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var tag = Convert.ToHexString(RandomNumberGenerator.GetBytes(BayWire.RunTagBytes / 2)).ToLowerInvariant();
        stage?.Report(BayUpgradeStage.Sending);
        using (var running = await LoadoutBayClient.ConnectForUpgradeAsync(host, port, cancellationToken).ConfigureAwait(false))
        {
            await running.SendElfAsync(elf, cancellationToken: cancellationToken).ConfigureAwait(false);
            stage?.Report(BayUpgradeStage.Starting);
            await running.RunElfAsync(tag, cancellationToken).ConfigureAwait(false);
        }

        stage?.Report(BayUpgradeStage.WaitingForRestart);
        using var started = await WaitForTagAsync(host, port, tag, restartTimeout ?? TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (!save)
        {
            return new BayUpgradeResult(started.Hello.Version, Saved: false);
        }

        stage?.Report(BayUpgradeStage.Saving);
        try
        {
            await started.SendElfAsync(elf, cancellationToken: cancellationToken).ConfigureAwait(false);
            await started.SaveElfAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LoadoutBayException ex) when (ex.Status == BayWire.StatusNotPermitted)
        {
            // Started from somewhere other than a USB drive or the hard disk: the new copy runs, and the old file stays.
            return new BayUpgradeResult(started.Hello.Version, Saved: false, "It wasn't started from a USB drive or the hard disk.");
        }
        catch (LoadoutBayException ex)
        {
            // The new copy runs all the same, and Loadout Bay writes the new file whole before it touches the old one.
            return new BayUpgradeResult(started.Hello.Version, Saved: false, ex.Message);
        }

        return new BayUpgradeResult(started.Hello.Version, Saved: true);
    }

    /// <summary>Room PFS takes beyond the ELF's own bytes: its inode and the rounding to whole zones.</summary>
    public const long SaveOverheadBytes = 64 * 1024;

    /// <summary>
    /// Whether OPL's partition lacks room to save a new Loadout Bay of <paramref name="elfBytes"/> beside the one
    /// in <c>APPS/Loadout-Bay</c>, where a copy that OPL started saves itself: the new file is written whole while
    /// the old one is still there. The new copy is the one that saves, and it deletes the old backup
    /// (<c>loadout-bay.elf.bak</c>) first when it needs its room, so the backup counts as room; the needed bytes
    /// given are those beyond it. Null when there is room, or when it can't be told: no copy in that folder (so the
    /// running one came from a USB drive, which this doesn't measure), or a Loadout Bay whose protocol differs
    /// from this Loadout's. Then the update goes ahead, and a failed save says why.
    /// </summary>
    public static async Task<BayUpgradeRoom?> CheckRoomAsync(string host, long elfBytes, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var files = await bay.ListFilesAsync(BayFolder.App, cancellationToken).ConfigureAwait(false);
            if (!files.Any(file => file.Name == HardDiskElfName))
            {
                return null;
            }

            var backup = files.FirstOrDefault(file => file.Name == HardDiskElfName + ".bak")?.Size ?? 0;
            var free = await bay.GetOplFreeBytesAsync(cancellationToken).ConfigureAwait(false);
            var needed = elfBytes + SaveOverheadBytes - backup;
            return free >= needed ? null : new BayUpgradeRoom(free, needed);
        }
        catch (LoadoutBayException)
        {
            return null;
        }
    }

    /// <summary>The ELF's name in <see cref="BayFolder.App"/>.</summary>
    public const string HardDiskElfName = "loadout-bay.elf";

    /// <summary>What OPL reads from <c>APPS/Loadout-Bay/title.cfg</c> to list Loadout Bay under Apps.</summary>
    public const string HardDiskTitleConfig = "title=Loadout Bay for PS2\nboot=" + HardDiskElfName + "\n";

    /// <summary>
    /// Puts Loadout Bay on the PS2's hard disk as an OPL app: the ELF and its <c>title.cfg</c> in
    /// <c>APPS/Loadout-Bay</c> in OPL's partition, replacing a copy already there. OPL lists it under Apps
    /// when its Applications start mode is on. No <c>loadout-bay.cfg</c> goes with it, so that copy takes
    /// its address over DHCP until one is put beside it.
    /// </summary>
    public static async Task PutOnHardDiskAsync(
        string host, ReadOnlyMemory<byte> elf, IProgress<long>? progress = null, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await bay.WriteFileAsync(BayFolder.App, HardDiskElfName, elf, progress, cancellationToken).ConfigureAwait(false);
        await bay.WriteFileAsync(BayFolder.App, "title.cfg", Encoding.ASCII.GetBytes(HardDiskTitleConfig), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    // The new copy loads its modules and asks for its address again, which takes a while.
    private static async Task<LoadoutBayClient> WaitForTagAsync(string host, int port, string tag, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        await Task.Delay(FirstWait, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string reason;
            try
            {
                var client = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                if (client.Hello.RunTag == tag)
                {
                    return client;
                }

                client.Dispose();
                reason = "the Loadout Bay that answers is not the one just sent.";
            }
            catch (LoadoutBayException ex)
            {
                reason = ex.Message;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new LoadoutBayException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The new Loadout Bay did not answer within {timeout.TotalSeconds:0} seconds: {reason} Check the PS2's screen, and start Loadout Bay from its USB drive if it shows nothing."));
            }

            await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
