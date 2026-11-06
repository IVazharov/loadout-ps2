using System.Buffers.Binary;
using System.Text;
using Loadout.Core;

namespace Loadout.Images;

internal enum SectorLayout
{
    Headerless2048,
    Mode1Raw2352,
    Mode2Raw2352,
}

internal sealed record ImageFacts
{
    public bool Readable { get; init; }

    public bool Corrupt { get; init; }

    public string? Failure { get; init; }

    public bool TrailingNonDataSectors { get; init; }

    public MediaKind Media { get; init; }

    public uint Layer0Sectors { get; init; }

    public uint Layer1Sectors { get; init; }

    public int VolumeSetSize { get; init; }

    public int VolumeSequence { get; init; }

    public string? VolumeLabel { get; init; }

    public DiscId? DiscId { get; init; }

    public bool AmbiguousId { get; init; }

    public long ImageBytes { get; init; }

    public static ImageFacts Failed(string path, string reason)
        => new()
        {
            Corrupt = true,
            Failure = reason,
            ImageBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
        };
}

internal static class Iso9660Image
{
    private static ReadOnlySpan<byte> Sync => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>Reads an iso or bin, or a chd as the iso or bin chdman extracts from it.</summary>
    public static ImageFacts Read(string path, SectorLayout? forcedLayout, bool declaredCompactDisc)
    {
        try
        {
            using Stream stream = ChdImageStream.IsChd(path)
                ? ChdImageStream.Open(path)
                : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadFacts(stream, path, forcedLayout, declaredCompactDisc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageFacts.Failed(path, ex.Message);
        }
    }

    /// <summary>Reads an image from an open, seekable stream. The path names it in a failure.</summary>
    public static ImageFacts Read(Stream stream, string path, SectorLayout? forcedLayout, bool declaredCompactDisc)
    {
        try
        {
            return ReadFacts(stream, path, forcedLayout, declaredCompactDisc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageFacts.Failed(path, ex.Message);
        }
    }

    private static ImageFacts ReadFacts(Stream stream, string path, SectorLayout? forcedLayout, bool declaredCompactDisc)
    {
        var length = stream.Length;
        if (!TryResolveLayout(stream, length, forcedLayout, out var layout, out var layoutError))
        {
            return ImageFacts.Failed(path, layoutError ?? "Unsupported image layout.");
        }

        var stride = Stride(layout);
        if (length < 17L * stride || length % stride != 0)
        {
            return ImageFacts.Failed(path, "Image is truncated or not an integral number of sectors.");
        }

        var fileSectors = (uint)(length / stride);
        var sector = new byte[2048];
        if (!TryReadUserSector(stream, layout, 16, sector) || !IsPrimaryDescriptor(sector))
        {
            return ImageFacts.Failed(path, "ISO9660 primary volume descriptor was not found.");
        }

        var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(128));
        if (blockSize is not 0 and not 2048)
        {
            return ImageFacts.Failed(path, "ISO9660 logical block size is not 2048.");
        }

        var layer0 = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80));
        var setSize = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(120));
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(124));
        var label = Encoding.Latin1.GetString(sector, 40, 32).Trim('\0', ' ');
        if (layer0 < 17 || layer0 > fileSectors)
        {
            return new ImageFacts
            {
                Corrupt = true,
                Failure = "ISO9660 volume size does not fit the image.",
                VolumeLabel = string.IsNullOrWhiteSpace(label) ? null : label,
                ImageBytes = length,
            };
        }

        var layer1 = 0u;
        var trailingAudio = false;
        var media = layout == SectorLayout.Headerless2048 ? MediaKind.Dvd5 : MediaKind.Cd;
        if (fileSectors > layer0)
        {
            var next = new byte[2048];
            var nextReadable = TryReadUserSector(stream, layout, layer0, next);
            var nextIsDescriptor = nextReadable && IsPrimaryDescriptor(next);
            if (layout != SectorLayout.Headerless2048 && !RawSectorHasSync(stream, layer0, stride))
            {
                // Audio sectors in a BIN have no CD sync. Do not fold them into layer 0.
                trailingAudio = true;
                media = MediaKind.CdMultiTrack;
            }
            else if (nextIsDescriptor)
            {
                // Layer 1's volume starts 16 sectors before layer 0 ends, so its descriptor sits at sector
                // layer0 and its volume size counts those 16 sectors again. Keep only the sectors after layer 0.
                var layer1Volume = BinaryPrimitives.ReadUInt32LittleEndian(next.AsSpan(80));
                layer1 = layer1Volume > 16 ? layer1Volume - 16 : 0;
                if (layer1 == 0 || fileSectors < layer0 + layer1)
                {
                    return ImageFacts.Failed(path, "DVD9 layer 1 descriptor does not fit the image.");
                }

                // A DVD9 game entry keeps the two volume-space counts. Do not flatten DVD9 into layer 0.
                media = MediaKind.Dvd9;
            }
            else if (nextReadable)
            {
                layer0 = fileSectors;
            }
        }

        if (declaredCompactDisc && media == MediaKind.Dvd5)
        {
            media = MediaKind.Cd;
        }

        var root = new byte[2048];
        if (!TryReadUserSector(stream, layout, 16, root) || !IsPrimaryDescriptor(root))
        {
            return ImageFacts.Failed(path, "ISO9660 primary volume descriptor was not found.");
        }

        if (!TryReadDirectory(stream, layout, root, out var entries))
        {
            return new ImageFacts
            {
                Readable = true,
                Corrupt = true,
                Failure = "ISO9660 root directory could not be read.",
                Media = media,
                Layer0Sectors = layer0,
                Layer1Sectors = layer1,
                VolumeSetSize = setSize,
                VolumeSequence = sequence,
                VolumeLabel = string.IsNullOrWhiteSpace(label) ? null : label,
                TrailingNonDataSectors = trailingAudio,
                ImageBytes = length,
            };
        }

        var ids = new List<DiscId>();
        var invalidBoot = false;
        foreach (var entry in entries)
        {
            if (!IsSystemCnf(entry.Name))
            {
                continue;
            }

            var bytes = ReadFileBytes(stream, layout, entry.Extent, entry.Length);
            var parsed = SystemCnf.Parse(Encoding.UTF8.GetString(bytes));
            if (parsed.Ambiguous)
            {
                return Build(true, false, null, true, null, media, layer0, layer1, setSize, sequence, label, trailingAudio, length);
            }

            if (parsed.DiscId is { } id)
            {
                ids.Add(id);
            }
            else if (parsed.HadBoot2)
            {
                invalidBoot = true;
            }
        }

        var distinct = ids.Select(id => id.Canonical).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1 || (distinct.Count == 1 && invalidBoot))
        {
            return Build(true, false, null, true, null, media, layer0, layer1, setSize, sequence, label, trailingAudio, length);
        }

        DiscId? discId = distinct.Count == 1 ? ids.First(id => id.Canonical == distinct[0]) : null;
        return Build(true, false, null, false, discId, media, layer0, layer1, setSize, sequence, label, trailingAudio, length);
    }

    private static ImageFacts Build(
        bool readable,
        bool corrupt,
        string? failure,
        bool ambiguous,
        DiscId? discId,
        MediaKind media,
        uint layer0,
        uint layer1,
        int setSize,
        int sequence,
        string label,
        bool trailingAudio,
        long length)
        => new()
        {
            Readable = readable,
            Corrupt = corrupt,
            Failure = failure,
            AmbiguousId = ambiguous,
            DiscId = discId,
            Media = media,
            Layer0Sectors = layer0,
            Layer1Sectors = layer1,
            VolumeSetSize = setSize,
            VolumeSequence = sequence,
            VolumeLabel = string.IsNullOrWhiteSpace(label) ? null : label,
            TrailingNonDataSectors = trailingAudio,
            ImageBytes = length,
        };

    private static bool TryResolveLayout(
        Stream stream,
        long length,
        SectorLayout? forcedLayout,
        out SectorLayout layout,
        out string? error)
    {
        layout = SectorLayout.Headerless2048;
        error = null;
        var hasSync = length >= 16 && RawSectorHasSync(stream, 0, 2352);
        if (forcedLayout is { } forced)
        {
            if (forced == SectorLayout.Headerless2048 && hasSync)
            {
                error = "CUE mode does not match the image bytes.";
                return false;
            }

            if (forced != SectorLayout.Headerless2048 && !hasSync)
            {
                error = "CUE mode does not match the image bytes.";
                return false;
            }

            if (forced != SectorLayout.Headerless2048 && !ModeMatches(stream, forced))
            {
                error = "CUE mode does not match the image bytes.";
                return false;
            }

            layout = forced;
            return true;
        }

        if (!hasSync)
        {
            layout = SectorLayout.Headerless2048;
            return true;
        }

        Span<byte> header = stackalloc byte[16];
        stream.Position = 0;
        if (stream.Read(header) != 16)
        {
            error = "Image header could not be read.";
            return false;
        }

        layout = header[15] switch
        {
            1 => SectorLayout.Mode1Raw2352,
            2 => SectorLayout.Mode2Raw2352,
            _ => SectorLayout.Headerless2048,
        };
        if (header[15] is not 1 and not 2)
        {
            error = "CD sync is present but the mode byte is not MODE1 or MODE2.";
            return false;
        }

        return true;
    }

    private static bool ModeMatches(Stream stream, SectorLayout layout)
    {
        Span<byte> header = stackalloc byte[16];
        stream.Position = 0;
        return stream.Read(header) == 16 && header[15] == (layout == SectorLayout.Mode1Raw2352 ? 1 : 2);
    }

    private static bool TryReadDirectory(Stream stream, SectorLayout layout, byte[] pvd, out List<DirectoryEntry> entries)
    {
        entries = new List<DirectoryEntry>();
        if (pvd.Length < 190 || pvd[156] < 34)
        {
            return false;
        }

        var extent = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158));
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166));
        if (dataLength == 0 || dataLength > 1024 * 1024)
        {
            return false;
        }

        var remaining = dataLength;
        var lsn = extent;
        var offset = 2048;
        var current = Array.Empty<byte>();
        while (remaining > 0)
        {
            if (offset >= 2048)
            {
                current = new byte[2048];
                if (!TryReadUserSector(stream, layout, lsn, current))
                {
                    return false;
                }

                lsn++;
                offset = 0;
            }

            var recordLength = current[offset];
            if (recordLength == 0)
            {
                var skip = 2048 - offset;
                if (skip <= 0 || skip > remaining)
                {
                    break;
                }

                remaining -= (uint)skip;
                offset = 2048;
                continue;
            }

            if (recordLength < 33 || offset + recordLength > 2048 || recordLength > remaining)
            {
                return false;
            }

            var nameLength = current[offset + 32];
            if (nameLength > recordLength - 33)
            {
                return false;
            }

            var name = Encoding.ASCII.GetString(current, offset + 33, nameLength);
            var entryExtent = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset + 2));
            var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset + 10));
            var flags = current[offset + 25];
            if ((flags & 0x02) == 0 && nameLength > 1)
            {
                entries.Add(new DirectoryEntry(name, entryExtent, entryLength));
            }

            remaining -= recordLength;
            offset += recordLength;
        }

        return true;
    }

    private static byte[] ReadFileBytes(Stream stream, SectorLayout layout, uint extent, uint length)
    {
        var capped = length > 64 * 1024 ? 64 * 1024 : length;
        var data = new byte[capped];
        var copied = 0;
        var lsn = extent;
        var sector = new byte[2048];
        while (copied < data.Length)
        {
            if (!TryReadUserSector(stream, layout, lsn, sector))
            {
                break;
            }

            var take = Math.Min(2048, data.Length - copied);
            Buffer.BlockCopy(sector, 0, data, copied, take);
            copied += take;
            lsn++;
        }

        if (copied == data.Length)
        {
            return data;
        }

        Array.Resize(ref data, copied);
        return data;
    }

    private static bool TryReadUserSector(Stream stream, SectorLayout layout, uint lsn, byte[] destination)
    {
        var stride = Stride(layout);
        var offset = DataOffset(layout);
        var position = (long)lsn * stride;
        if (position < 0 || position + stride > stream.Length)
        {
            return false;
        }

        stream.Position = position;
        var raw = stride == 2048 ? destination : new byte[stride];
        if (stream.Read(raw, 0, stride) != stride)
        {
            return false;
        }

        if (stride == 2048)
        {
            return true;
        }

        Buffer.BlockCopy(raw, offset, destination, 0, 2048);
        return true;
    }

    private static bool RawSectorHasSync(Stream stream, uint lsn, int stride)
    {
        var position = (long)lsn * stride;
        if (position < 0 || position + 12 > stream.Length)
        {
            return false;
        }

        Span<byte> sync = stackalloc byte[12];
        stream.Position = position;
        if (stream.Read(sync) != 12)
        {
            return false;
        }

        return sync.SequenceEqual(Sync);
    }

    private static bool IsPrimaryDescriptor(byte[] sector)
        => sector.Length >= 6
            && sector[0] == 0x01
            && sector[1] == (byte)'C'
            && sector[2] == (byte)'D'
            && sector[3] == (byte)'0'
            && sector[4] == (byte)'0'
            && sector[5] == (byte)'1';

    private static bool IsSystemCnf(string name)
    {
        var semi = name.IndexOf(';');
        if (semi >= 0)
        {
            name = name[..semi];
        }

        return name.Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase);
    }

    private static int Stride(SectorLayout layout) => layout == SectorLayout.Headerless2048 ? 2048 : 2352;

    private static int DataOffset(SectorLayout layout) => layout switch
    {
        SectorLayout.Mode1Raw2352 => 16,
        SectorLayout.Mode2Raw2352 => 24,
        _ => 0,
    };

    private readonly record struct DirectoryEntry(string Name, uint Extent, uint Length);
}

internal readonly record struct SystemCnfResult(DiscId? DiscId, bool Ambiguous, bool HadBoot2);

internal static class SystemCnf
{
    public static SystemCnfResult Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SystemCnfResult(null, false, false);
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var ids = new List<DiscId>();
        var invalid = false;
        var hadBoot = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            if (!key.Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hadBoot = true;
            var value = line[(equals + 1)..].Trim().Trim('"');
            var separator = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
            var fileName = (separator >= 0 ? value[(separator + 1)..] : value).Trim().Trim('"');
            if (fileName.EndsWith(";1", StringComparison.Ordinal))
            {
                fileName = fileName[..^2];
            }

            if (DiscId.TryParse(fileName, out var discId))
            {
                ids.Add(discId);
            }
            else
            {
                invalid = true;
            }
        }

        var distinct = ids.Select(id => id.Canonical).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1 || (invalid && distinct.Count > 0))
        {
            return new SystemCnfResult(null, true, true);
        }

        if (distinct.Count == 1)
        {
            return new SystemCnfResult(ids[0], false, true);
        }

        return new SystemCnfResult(null, false, hadBoot);
    }
}
