using System.Globalization;

namespace Loadout.Discs;

public enum DiscBackupResult
{
    /// <summary>Sectors are still missing; the copy can go on.</summary>
    Incomplete,

    /// <summary>Complete, but Redump's list could not be had yet.</summary>
    NotCheckedYet,

    MatchesRedump,

    DoesNotMatchRedump,

    NotInRedump,
}

/// <summary>One backed-up disc as its files say.</summary>
public sealed record DiscBackup(
    DiscBackupFiles Files,
    DiscIdentity? Identity,
    long Sectors,
    long ReadSectors,
    int Cleanings,
    DiscBackupResult Result,
    DateTime ModifiedUtc)
{
    public long Bytes => Sectors * DiscSectors.Size;
}

/// <summary>
/// The backups in a folder. The folder is the truth: each copy's map names its disc and says how far the copy got, and its
/// hashes file says what Redump's list made of it. Nothing about them is kept anywhere else.
/// </summary>
public static class DiscBackupLibrary
{
    private const string MapExtension = ".map";

    public static IReadOnlyList<DiscBackup> List(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var backups = new List<DiscBackup>();
        foreach (var mapPath in Directory.EnumerateFiles(folder, "*" + MapExtension))
        {
            if (Read(new DiscBackupFiles(folder, Path.GetFileNameWithoutExtension(mapPath))) is { } backup)
            {
                backups.Add(backup);
            }
        }

        return [.. backups.OrderBy(b => b.Files.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The backup whose files have this name, or null when its map is missing or can't be read.</summary>
    public static DiscBackup? Read(DiscBackupFiles files)
    {
        DiscCopyMap map;
        try
        {
            map = DiscCopyMap.Load(files.Map);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }

        var identity = DiscIdentity.TryParse(map.Identity, out var parsed) ? parsed : null;
        var image = new FileInfo(files.Image);
        return new DiscBackup(
            files,
            identity,
            map.Sectors,
            map.ReadSectors,
            map.Cleanings,
            map.IsComplete && image.Exists ? ResultFrom(files.Hashes) : DiscBackupResult.Incomplete,
            image.Exists ? image.LastWriteTimeUtc : File.GetLastWriteTimeUtc(files.Map));
    }

    /// <summary>The backup of the same disc, if the folder has one: complete ones first.</summary>
    public static DiscBackup? FindSameDisc(IEnumerable<DiscBackup> backups, DiscIdentity identity) =>
        backups
            .Where(b => b.Identity is { } known && known.IsSameDisc(identity))
            .OrderBy(b => b.Result == DiscBackupResult.Incomplete)
            .FirstOrDefault();

    /// <summary>
    /// Ends a complete copy: hashes it, checks it against Redump's list when there is one, gives it Redump's name when it
    /// matches, and writes its hashes file. Returns the backup as it now is.
    /// </summary>
    public static async Task<DiscBackup> FinishAsync(DiscBackupFiles files, RedumpDat? redump, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        DiscImageHashes hashes;
        await using (var image = new FileStream(files.Image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            hashes = await DiscImageHashes.ComputeAsync(image, progress, cancellationToken).ConfigureAwait(false);
        }

        return Record(files, hashes, redump);
    }

    /// <summary>Checks a complete copy that waited for Redump's list, from the hashes it has already.</summary>
    public static DiscBackup? CheckLater(DiscBackup backup, RedumpDat redump)
    {
        if (backup.Result != DiscBackupResult.NotCheckedYet || ReadHashes(backup.Files.Hashes) is not { } hashes)
        {
            return null;
        }

        return Record(backup.Files, hashes, redump);
    }

    private static DiscBackup Record(DiscBackupFiles files, DiscImageHashes hashes, RedumpDat? redump)
    {
        var check = redump?.Check(hashes.Size, hashes.Sha1);
        if (check is { Verdict: RedumpVerdict.Matches, Game: { } game })
        {
            files = files.RenameTo(DiscBackupFiles.NameFor(game));
        }

        var temporary = files.Hashes + ".new";
        File.WriteAllText(temporary, hashes.Format(Path.GetFileName(files.Image), check, redump?.Version));
        File.Move(temporary, files.Hashes, overwrite: true);
        return Read(files) ?? throw new IOException($"The map of {files.Name} could not be read back.");
    }

    private static DiscBackupResult ResultFrom(string hashesPath)
    {
        string? line;
        try
        {
            line = File.ReadLines(hashesPath).FirstOrDefault(l => l.StartsWith("Redump: ", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiscBackupResult.NotCheckedYet;
        }

        return line switch
        {
            null => DiscBackupResult.NotCheckedYet,
            _ when line.StartsWith("Redump: matches", StringComparison.Ordinal) => DiscBackupResult.MatchesRedump,
            _ when line.StartsWith("Redump: doesn't match", StringComparison.Ordinal) => DiscBackupResult.DoesNotMatchRedump,
            _ when line.StartsWith("Redump: not in the list", StringComparison.Ordinal) => DiscBackupResult.NotInRedump,
            _ => DiscBackupResult.NotCheckedYet,
        };
    }

    private static DiscImageHashes? ReadHashes(string path)
    {
        try
        {
            // The first line is the copy's file name; the fields follow.
            var fields = File.ReadLines(path)
                .Skip(1)
                .Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0])
                .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.Ordinal);
            return fields.TryGetValue("Size", out var size) && long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                && fields.TryGetValue("CRC-32", out var crc) && fields.TryGetValue("MD5", out var md5) && fields.TryGetValue("SHA-1", out var sha1)
                ? new DiscImageHashes(bytes, crc, md5, sha1)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
