using System.Buffers.Binary;
using System.Text;

namespace Loadout.Discs;

/// <summary>A file on a disc: where its data is.</summary>
public sealed record IsoFile(string Path, long FirstSector, long Sectors, long Bytes)
{
    public long EndSector => FirstSector + Sectors;
}

/// <summary>The files found on a disc. <see cref="Complete"/> is false when a folder could not be read.</summary>
public sealed record IsoFileList(IReadOnlyList<IsoFile> Files, bool Complete);

/// <summary>How much of a file falls in some stretches of the disc.</summary>
public sealed record IsoFileShare(IsoFile File, long Sectors);

/// <summary>
/// Reads a disc's ISO9660 folders to find where each file is, from any source of 2048-byte sectors: the drive, or a copy
/// still being made (a folder in a stretch not read yet is left out, and the list says it is not complete).
/// </summary>
public static class IsoFileTree
{
    private const int MaxFolders = 20_000;
    private const int MaxFiles = 200_000;
    private const long MaxFolderBytes = 16L * 1024 * 1024;

    /// <summary>Reads one sector; returns false when it can't.</summary>
    public delegate bool SectorSource(long sector, Span<byte> destination);

    /// <summary>Lists every file on the disc, or only those in its root folder.</summary>
    public static IsoFileList Read(SectorSource read, long discSectors, bool rootOnly = false)
    {
        var sector = new byte[DiscSectors.Size];
        if (!read(DiscSectors.VolumeDescriptor, sector) || !IsVolumeDescriptor(sector))
        {
            return new IsoFileList([], Complete: false);
        }

        var files = new List<IsoFile>();
        var complete = true;
        var seen = new HashSet<long>();
        var folders = new Queue<(string Path, long Extent, long Length)>();
        var root = sector.AsSpan(156);
        folders.Enqueue((string.Empty, BinaryPrimitives.ReadUInt32LittleEndian(root[2..]), BinaryPrimitives.ReadUInt32LittleEndian(root[10..])));
        while (folders.Count > 0 && seen.Count < MaxFolders && files.Count < MaxFiles)
        {
            var (path, extent, length) = folders.Dequeue();
            if (!seen.Add(extent))
            {
                continue;
            }

            if (length is <= 0 or > MaxFolderBytes || extent + ((length + DiscSectors.Size - 1) / DiscSectors.Size) > discSectors)
            {
                complete = false;
                continue;
            }

            for (var offset = 0L; offset < length; offset += DiscSectors.Size)
            {
                if (!read(extent + (offset / DiscSectors.Size), sector))
                {
                    complete = false;
                    break;
                }

                foreach (var (name, childExtent, childLength, isFolder) in Records(sector, (int)Math.Min(DiscSectors.Size, length - offset)))
                {
                    var childPath = path.Length == 0 ? name : path + "/" + name;
                    if (isFolder)
                    {
                        if (!rootOnly)
                        {
                            folders.Enqueue((childPath, childExtent, childLength));
                        }
                    }
                    else
                    {
                        files.Add(new IsoFile(childPath, childExtent, (childLength + DiscSectors.Size - 1) / DiscSectors.Size, childLength));
                    }
                }
            }
        }

        return new IsoFileList([.. files.OrderBy(f => f.FirstSector).ThenBy(f => f.Path, StringComparer.Ordinal)], complete);
    }

    /// <summary>
    /// The files that have sectors in these stretches, with how many, biggest share first; and how many of the stretches'
    /// sectors are in no file (the disc's own areas and folders).
    /// </summary>
    public static (IReadOnlyList<IsoFileShare> Files, long OutsideFiles) FilesIn(IsoFileList list, IEnumerable<(long Start, long Count)> stretches)
    {
        var files = list.Files;
        var shares = new Dictionary<IsoFile, long>();
        var outside = 0L;
        foreach (var (start, count) in stretches)
        {
            var end = start + count;
            var covered = 0L;
            for (var i = FirstEndingAfter(files, start); i < files.Count && files[i].FirstSector < end; i++)
            {
                var overlap = Math.Min(end, files[i].EndSector) - Math.Max(start, files[i].FirstSector);
                if (overlap > 0)
                {
                    shares[files[i]] = shares.GetValueOrDefault(files[i]) + overlap;
                    covered += overlap;
                }
            }

            outside += Math.Max(0, count - covered);
        }

        return ([.. shares.Select(s => new IsoFileShare(s.Key, s.Value)).OrderByDescending(s => s.Sectors).ThenBy(s => s.File.Path, StringComparer.Ordinal)], outside);
    }

    private static bool IsVolumeDescriptor(ReadOnlySpan<byte> sector) =>
        sector[0] == 1 && sector[1..6].SequenceEqual("CD001"u8) && sector[156] >= 34;

    private static List<(string Name, long Extent, long Length, bool IsFolder)> Records(byte[] sector, int usable)
    {
        var records = new List<(string, long, long, bool)>();
        var offset = 0;
        while (offset < usable)
        {
            var length = sector[offset];
            if (length == 0)
            {
                // Records never cross a sector; the rest of this one is padding.
                break;
            }

            if (length < 34 || offset + length > usable)
            {
                break;
            }

            var nameLength = sector[offset + 32];
            if (33 + nameLength <= length)
            {
                var raw = sector.AsSpan(offset + 33, nameLength);
                // "\0" and "\1" are the folder itself and its parent.
                if (!(nameLength == 1 && raw[0] <= 1))
                {
                    var name = Encoding.ASCII.GetString(raw);
                    var version = name.IndexOf(';', StringComparison.Ordinal);
                    records.Add((
                        version >= 0 ? name[..version] : name,
                        BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(offset + 2)),
                        BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(offset + 10)),
                        (sector[offset + 25] & 0x02) != 0));
                }
            }

            offset += length;
        }

        return records;
    }

    private static int FirstEndingAfter(IReadOnlyList<IsoFile> files, long sector)
    {
        // Files are in order of their first sector and, on a PS2 disc, don't overlap: the first one that ends after the
        // sector is found by halving.
        int low = 0, high = files.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (files[middle].EndSector <= sector)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
