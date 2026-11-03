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
/// for, or when the copy was not started from a USB drive or the hard disk; the new copy runs either way.
/// </param>
public sealed record BayUpgradeResult(string Version, bool Saved);

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
        await started.SendElfAsync(elf, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await started.SaveElfAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LoadoutBayException ex) when (ex.Status == BayWire.StatusNotPermitted)
        {
            // Started from somewhere other than a USB drive or the hard disk: the new copy runs, and the old file stays.
            return new BayUpgradeResult(started.Hello.Version, Saved: false);
        }

        return new BayUpgradeResult(started.Hello.Version, Saved: true);
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
