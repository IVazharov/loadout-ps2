using Loadout.Core;
using Loadout.Library;

namespace Loadout.Bay;

/// <summary>
/// Removes and edits games on the PS2. Each call opens one connection to Loadout Bay and closes it
/// again, the way <see cref="BayLibrary.ReadAsync"/> does, and Loadout Bay checks every game again
/// before it changes anything.
/// </summary>
public static class BayGameChanges
{
    /// <summary>True for a game Remove can take: an incomplete install, or a whole game named like one.</summary>
    public static bool CanRemove(InstalledGameRow game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Incomplete || CanEdit(game);
    }

    /// <summary>True for a whole game whose header Loadout Bay read: one named like a game, not an incomplete install.</summary>
    public static bool CanEdit(InstalledGameRow game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return !game.Incomplete && game.Title != BayGame.UnreadableTitle && BayGame.DiscIdOfName(game.PartitionName) is not null;
    }

    /// <summary>
    /// Removes each game over one connection: a whole game with DELETE_GAME, an incomplete install with
    /// REMOVE_INCOMPLETE. A game Loadout Bay refuses does not stop the others.
    /// </summary>
    public static async Task<BayRemoval> RemoveAsync(
        string host, IReadOnlyList<InstalledGameRow> games, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var removed = new List<InstalledGameRow>();
        var failed = new List<BayRemovalFailure>();
        foreach (var game in games)
        {
            try
            {
                if (game.Incomplete)
                {
                    await bay.RemoveIncompleteAsync(game.PartitionName, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await bay.DeleteGameAsync(game.PartitionName, cancellationToken).ConfigureAwait(false);
                }

                removed.Add(game);
            }
            catch (LoadoutBayException ex)
            {
                failed.Add(new BayRemovalFailure(game, ex.Message));
            }
        }

        return new BayRemoval(removed, failed);
    }

    /// <summary>
    /// Reads the game's header again, applies <paramref name="change"/> to its current values, and
    /// writes it, all over one connection. Returns the game as the PS2's disk now holds it.
    /// </summary>
    public static async Task<InstalledGameRow> EditAsync(
        string host, string partitionName, Func<BayGameEdit, BayGameEdit> change, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var current = await bay.ReadGameAsync(partitionName, cancellationToken).ConfigureAwait(false);
        if (current.Header is null)
        {
            throw new LoadoutBayException("Loadout Bay found no game header to edit in " + partitionName + ".");
        }

        var game = await bay.UpdateGameAsync(partitionName, change(BayGameEdit.Of(current.Header)), cancellationToken).ConfigureAwait(false);
        return game.ToInstalledGameRow();
    }

    /// <summary>
    /// The game's settings file in OPL's CFG folder, or <see cref="OplGameSettings.Empty"/> when it has
    /// none. A PS2 without OPL's partition is an error, not empty settings.
    /// </summary>
    public static async Task<OplGameSettings> ReadSettingsAsync(
        string host, DiscId discId, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return await ReadSettingsAsync(bay, discId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the game's compatibility modes and transfer mode into its settings file, over the file as
    /// the PS2 holds it now, keeping its other lines. Nothing is written when they are already set.
    /// Returns the settings as written.
    /// </summary>
    public static async Task<OplGameSettings> SaveSettingsAsync(
        string host, DiscId discId, byte compatibilityModes, int dma, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var current = await ReadSettingsAsync(bay, discId, cancellationToken).ConfigureAwait(false);
        if (current.CompatibilityModes == compatibilityModes && current.Dma == dma)
        {
            return current;
        }

        var content = current.With(compatibilityModes, dma);
        var name = OplGameSettings.FileName(discId);
        if (content.Length == 0)
        {
            // Nothing left but the defaults: OPL reads a missing file the same way.
            await bay.DeleteFileAsync(BayFolder.Cfg, name, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await bay.WriteFileAsync(BayFolder.Cfg, name, content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return OplGameSettings.Parse(content);
    }

    /// <summary>
    /// Deletes these games' files in OPL's partition: every <c>ART/&lt;startup&gt;_*</c> image and
    /// <c>CFG/&lt;startup&gt;.cfg</c>, over one connection. Files of other games are never touched.
    /// Returns the files deleted, and each one Loadout Bay refused, with the reason.
    /// </summary>
    public static async Task<BayFileRemoval> RemoveOplFilesAsync(
        string host, IReadOnlyList<DiscId> discs, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discs);
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var art = await bay.ListFilesAsync(BayFolder.Art, cancellationToken).ConfigureAwait(false);
        var cfg = await bay.ListFilesAsync(BayFolder.Cfg, cancellationToken).ConfigureAwait(false);
        var targets = new List<(BayFolder Folder, string Name)>();
        foreach (var discId in discs.Distinct())
        {
            var stem = discId.OplFileStem;
            targets.AddRange(art.Where(file => file.Name.StartsWith(stem + "_", StringComparison.Ordinal)).Select(file => (BayFolder.Art, file.Name)));
            targets.AddRange(cfg.Where(file => file.Name == OplGameSettings.FileName(discId)).Select(file => (BayFolder.Cfg, file.Name)));
        }

        var deleted = new List<string>();
        var failed = new List<(string Name, string Reason)>();
        foreach (var (folder, name) in targets)
        {
            var path = folder.ToString().ToUpperInvariant() + "/" + name;
            try
            {
                await bay.DeleteFileAsync(folder, name, cancellationToken).ConfigureAwait(false);
                deleted.Add(path);
            }
            catch (LoadoutBayException ex) when (ex.Status != 0)
            {
                failed.Add((path, ex.Message));
            }
        }

        return new BayFileRemoval(deleted, failed);
    }

    private static async Task<OplGameSettings> ReadSettingsAsync(LoadoutBayClient bay, DiscId discId, CancellationToken cancellationToken)
    {
        var name = OplGameSettings.FileName(discId);
        var files = await bay.ListFilesAsync(BayFolder.Cfg, cancellationToken).ConfigureAwait(false);
        return files.Any(file => file.Name == name)
            ? OplGameSettings.Parse(await bay.ReadFileAsync(BayFolder.Cfg, name, cancellationToken).ConfigureAwait(false))
            : OplGameSettings.Empty;
    }
}

/// <summary>What Remove did: the games Loadout Bay removed, and each it refused, with the reason.</summary>
public sealed record BayRemoval(IReadOnlyList<InstalledGameRow> Removed, IReadOnlyList<BayRemovalFailure> Failed);

public sealed record BayRemovalFailure(InstalledGameRow Game, string Reason);

/// <summary>Which of OPL's files a clean-up deleted, as <c>ART/name</c> or <c>CFG/name</c>, and each it could not.</summary>
public sealed record BayFileRemoval(IReadOnlyList<string> Deleted, IReadOnlyList<(string Name, string Reason)> Failed);
