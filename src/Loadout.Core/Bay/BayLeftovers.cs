namespace Loadout.Bay;

/// <summary>A <c>.part</c> file that a write cut short left in one of OPL's folders, with its size.</summary>
public sealed record BayLeftover(BayFolder Folder, string Name, long Bytes)
{
    public string Path => (Folder == BayFolder.App ? "APPS/Loadout-Bay" : Folder.ToString().ToUpperInvariant()) + "/" + Name;
}

/// <summary>
/// Deletes the <c>.part</c> files that writes cut short left in OPL's folders: by a full partition, before
/// Loadout Bay deleted them itself, or by a connection lost halfway through a file. Only Loadout Bay writes
/// .part files there, and each write runs within one connection, so at Connect every one is a leftover that
/// only takes room, as half a memory card once took 4.6 MiB of a console's OPL partition.
/// </summary>
public static class BayLeftovers
{
    private const string PartSuffix = ".part";

    private static readonly BayFolder[] Folders = [BayFolder.Art, BayFolder.Cfg, BayFolder.Vmc, BayFolder.App];

    public static async Task<IReadOnlyList<BayLeftover>> ClearAsync(string host, int port = BayWire.Port, CancellationToken cancellationToken = default)
    {
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var cleared = new List<BayLeftover>();
        foreach (var folder in Folders)
        {
            IReadOnlyList<BayFile> files;
            try
            {
                files = await bay.ListFilesAsync(folder, cancellationToken).ConfigureAwait(false);
            }
            catch (LoadoutBayException ex) when (ex.Status == BayWire.StatusNotFound)
            {
                continue;
            }

            var names = files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var file in files.Where(file => file.Name.EndsWith(PartSuffix, StringComparison.Ordinal)))
            {
                var name = file.Name[..^PartSuffix.Length];
                if (folder == BayFolder.Vmc)
                {
                    // Loadout Bay deletes only cards (.bin) in VMC, so the leftover is first made into a one-byte
                    // card of its own name, which every Loadout Bay can delete. A card of that name, which may
                    // hold saves, is never written over: its leftover stays.
                    if (names.Contains(name) || !name.EndsWith(".bin", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await bay.WriteFileAsync(BayFolder.Vmc, name, new byte[1], cancellationToken: cancellationToken).ConfigureAwait(false);
                    await bay.DeleteFileAsync(BayFolder.Vmc, name, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await bay.DeleteFileAsync(folder, file.Name, cancellationToken).ConfigureAwait(false);
                }

                cleared.Add(new BayLeftover(folder, file.Name, file.Size));
            }
        }

        return cleared;
    }
}
