using Loadout.Library;

namespace Loadout.Bay;

/// <summary>What one Connect read from Loadout Bay: its greeting, the hard disk's space, and the games as Installed Games rows.</summary>
public sealed record BayLibrary(BayHello Hello, BayFreeSpace Space, IReadOnlyList<InstalledGameRow> Games)
{
    /// <summary>
    /// Connects, reads the hard disk's space and every game, and closes the connection again.
    /// Loadout Bay serves one connection at a time, so holding this one open would keep the speed test waiting.
    /// <paramref name="connected"/> hears when the connection is up and the slow part, reading the games, begins.
    /// </summary>
    public static async Task<BayLibrary> ReadAsync(
        string host,
        int port = BayWire.Port,
        IProgress<BayHello>? connected = null,
        CancellationToken cancellationToken = default)
    {
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (!bay.Hello.HasHardDisk)
        {
            throw new LoadoutBayException(BayWire.DescribeStatus(BayWire.StatusNoDevice), BayWire.StatusNoDevice);
        }

        connected?.Report(bay.Hello);
        var space = await bay.GetFreeSpaceAsync(cancellationToken).ConfigureAwait(false);
        var games = await bay.ListGamesAsync(cancellationToken).ConfigureAwait(false);
        return new BayLibrary(bay.Hello, space, games.Select(game => game.ToInstalledGameRow()).ToList());
    }
}
