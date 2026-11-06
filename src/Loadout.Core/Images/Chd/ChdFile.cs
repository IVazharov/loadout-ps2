using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Loadout.Images;

public enum ChdImageKind
{
    Dvd,
    Cd,
    Unsupported,
    Unknown,
}

/// <summary>A chd that cannot be read. The message is written for the person using Loadout.</summary>
public sealed class ChdException : IOException
{
    public ChdException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads a version 5 CHD, MAME's compressed disc image format, without chdman. The layout follows
/// MAME's chd.cpp (BSD-3-Clause; see ThirdParty/MameChd). Every hunk is checked against the CRC in the
/// map, as chdman does when it extracts. The SHA-1 of the data in the header is not compared: some tools
/// write a wrong one over correct data, and each hunk's CRC already covers the data.
/// </summary>
internal sealed class ChdFile : IDisposable
{
    /// <summary>Kept with converted images: a new value converts them again.</summary>
    public const string ReaderVersion = "loadout-chd-1";

    private const int HeaderBytes = 124;
    private const int MapEntryBytes = 12;
    private const int BatchBytes = 16 * 1024 * 1024;
    private const int MaxHunkBytes = 128 * 1024 * 1024;
    private const int MaxWindowBytes = 64 * 1024 * 1024;
    private const int MaxMetadataEntries = 4096;

    private const byte CompressionNone = 4;
    private const byte CompressionSelf = 5;
    private const byte CompressionParent = 6;
    private const byte CompressionRleSmall = 7;
    private const byte CompressionRleLarge = 8;
    private const byte CompressionSelf0 = 9;
    private const byte CompressionSelf1 = 10;
    private const byte CompressionParentSelf = 11;
    private const byte CompressionParent0 = 12;
    private const byte CompressionParent1 = 13;

    private static readonly string[] CdTags = ["CHT2", "CHTR", "CHCD"];
    private static readonly string[] UnsupportedTags = ["CHGT", "CHGD", "GDDD"];

    private readonly SafeFileHandle _handle;
    private readonly string[] _compressors;
    private readonly long _mapOffset;
    private readonly uint _hunkCount;
    private readonly ConcurrentBag<ChdDecoders> _decoders = [];
    private byte[]? _map;
    private byte[] _window = [];
    private long _windowStart;
    private int _windowLength;

    private ChdFile(
        SafeFileHandle handle,
        string[] compressors,
        long logicalBytes,
        long mapOffset,
        int hunkBytes,
        uint hunkCount)
    {
        _handle = handle;
        _compressors = compressors;
        LogicalBytes = logicalBytes;
        _mapOffset = mapOffset;
        HunkBytes = hunkBytes;
        _hunkCount = hunkCount;
    }

    public long LogicalBytes { get; }

    public int HunkBytes { get; }

    public IReadOnlyList<ChdMetadata> Metadata { get; private set; } = [];

    public ChdImageKind Kind => Classify(Metadata.Select(entry => entry.Tag));

    private bool Compressed => _compressors[0].Length > 0;

    public static ChdFile Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        try
        {
            var file = ReadHeader(handle);
            file.Metadata = file.ReadMetadata(ReadOffset(handle));
            return file;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static ChdImageKind Classify(IEnumerable<string> tags)
    {
        var known = tags.Select(tag => tag.Trim().ToUpperInvariant()).ToArray();
        var dvd = known.Contains("DVD");
        var cd = known.Any(tag => CdTags.Contains(tag));
        if (known.Any(tag => UnsupportedTags.Contains(tag)) || (dvd && cd))
        {
            return ChdImageKind.Unsupported;
        }

        if (dvd)
        {
            return ChdImageKind.Dvd;
        }

        return cd ? ChdImageKind.Cd : ChdImageKind.Unknown;
    }

    public uint HunkCount => _hunkCount;

    /// <summary>The most hunks to decode in one batch: about 16 MB, and at least one.</summary>
    public int BatchHunks => (int)Math.Min(_hunkCount, (uint)Math.Max(1, BatchBytes / HunkBytes));

    /// <summary>
    /// Decodes count hunks from first into destination, side by side. One call at a time: the
    /// batch's stored data is read in one go, into a window the next call replaces.
    /// </summary>
    public void DecodeHunks(uint first, int count, byte[] destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (count < 1 || first + (ulong)count > _hunkCount || destination.Length < (long)count * HunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        cancellationToken.ThrowIfCancellationRequested();
        LoadMap();
        LoadWindow(first, count);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };
        try
        {
            Parallel.For(
                0,
                count,
                options,
                () => _decoders.TryTake(out var decoders) ? decoders : new ChdDecoders(_compressors, HunkBytes),
                (index, _, decoders) =>
                {
                    ReadHunk(first + (uint)index, destination.AsSpan(index * HunkBytes, HunkBytes), decoders);
                    return decoders;
                },
                _decoders.Add);
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        while (_decoders.TryTake(out var decoders))
        {
            decoders.Dispose();
        }
    }

    private static ChdFile ReadHeader(SafeFileHandle handle)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        var read = ReadUpTo(handle, 0, header);
        if (read < 16 || !header[..8].SequenceEqual("MComprHD"u8))
        {
            throw new ChdException("This is not a complete chd file. It may still be copying.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (version != 5)
        {
            throw new ChdException("This chd is in an older format (version " + version + ") that Loadout does not read. chdman can update it with its copy command.");
        }

        if (read < HeaderBytes)
        {
            throw new ChdException("This is not a complete chd file. It may still be copying.");
        }

        var compressors = new string[4];
        for (var index = 0; index < compressors.Length; index++)
        {
            var tag = header.Slice(16 + (index * 4), 4);
            compressors[index] = BinaryPrimitives.ReadUInt32BigEndian(tag) == 0 ? string.Empty : Encoding.Latin1.GetString(tag);
            if (compressors[index].Length > 0 && !ChdDecoders.Knows(compressors[index]))
            {
                throw new ChdException("This chd uses the " + compressors[index] + " compression, which is not used for PS2 games.");
            }
        }

        var logical = BinaryPrimitives.ReadUInt64BigEndian(header[32..]);
        var map = BinaryPrimitives.ReadUInt64BigEndian(header[40..]);
        var hunkBytes = BinaryPrimitives.ReadUInt32BigEndian(header[56..]);
        var unitBytes = BinaryPrimitives.ReadUInt32BigEndian(header[60..]);
        if (hunkBytes == 0 || hunkBytes > MaxHunkBytes || unitBytes == 0 || unitBytes > hunkBytes
            || logical == 0 || logical > long.MaxValue || map == 0 || map > long.MaxValue)
        {
            throw new ChdException("This chd is damaged: its header does not make sense.");
        }

        var hunkCount = (logical + hunkBytes - 1) / hunkBytes;
        if (hunkCount > int.MaxValue / MapEntryBytes)
        {
            throw new ChdException("This chd is damaged: its header does not make sense.");
        }

        if (header.Slice(104, 20).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new ChdException("This chd only holds the differences from another chd, its parent, so it cannot be read by itself.");
        }

        if (compressors.Any(tag => tag is "cdzl" or "cdlz" or "cdzs" or "cdfl") && hunkBytes % ChdCdCodec.FrameBytes != 0)
        {
            throw new ChdException("This chd is damaged: its header does not make sense.");
        }

        return new ChdFile(handle, compressors, (long)logical, (long)map, (int)hunkBytes, (uint)hunkCount);
    }

    private static long ReadOffset(SafeFileHandle handle)
    {
        Span<byte> field = stackalloc byte[8];
        ReadExact(handle, 48, field);
        var offset = BinaryPrimitives.ReadUInt64BigEndian(field);
        return offset > long.MaxValue ? throw new ChdException("This chd is damaged: its header does not make sense.") : (long)offset;
    }

    private static int ReadUpTo(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void ReadExact(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        if (ReadUpTo(handle, offset, destination) != destination.Length)
        {
            throw new ChdException("This chd file ends early. It may still be copying.");
        }
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc = (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ value]);
        }

        return crc;
    }

    private static readonly ushort[] Crc16Table = BuildCrc16Table();

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (ushort)(index << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x8000) != 0 ? (ushort)((value << 1) ^ 0x1021) : (ushort)(value << 1);
            }

            table[index] = value;
        }

        return table;
    }

    private List<ChdMetadata> ReadMetadata(long offset)
    {
        var entries = new List<ChdMetadata>();
        Span<byte> head = stackalloc byte[16];
        while (offset != 0)
        {
            if (entries.Count == MaxMetadataEntries)
            {
                throw new ChdException("This chd is damaged: its metadata does not end.");
            }

            ReadExact(_handle, offset, head);
            var length = BinaryPrimitives.ReadInt32BigEndian(head[4..]) & 0x00FFFFFF;
            var next = BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            var data = new byte[length];
            ReadExact(_handle, offset + head.Length, data);
            entries.Add(new ChdMetadata(Encoding.Latin1.GetString(head[..4]), data));
            if (next > long.MaxValue)
            {
                throw new ChdException("This chd is damaged: its metadata does not end.");
            }

            offset = (long)next;
        }

        return entries;
    }

    /// <summary>The map is read only when data is, as a scan that finds the image converted needs none of it.</summary>
    private void LoadMap()
    {
        if (_map != null)
        {
            return;
        }

        if (!Compressed)
        {
            // Four bytes for each hunk: where it is, counted in hunks.
            var raw = new byte[_hunkCount * 4];
            ReadExact(_handle, _mapOffset, raw);
            _map = raw;
            return;
        }

        var head = new byte[16];
        ReadExact(_handle, _mapOffset, head);
        var mapBytes = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (mapBytes > int.MaxValue)
        {
            throw new ChdException("This chd is damaged: its map does not make sense.");
        }

        var compressed = new byte[mapBytes];
        ReadExact(_handle, _mapOffset + head.Length, compressed);
        _map = DecodeMap(head, compressed);
    }

    private byte[] DecodeMap(byte[] head, byte[] compressed)
    {
        var firstOffset = ((long)BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4)) << 32) | BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(6));
        var expectedCrc = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(10));
        int lengthBits = head[12];
        int selfBits = head[13];
        int parentBits = head[14];
        var count = (int)_hunkCount;
        var map = new byte[count * MapEntryBytes];
        var bits = new ChdBitReader(compressed, 0, compressed.Length);
        var tree = new ChdHuffman(16, 8);
        tree.ImportTreeRle(bits);

        byte lastCompression = 0;
        var repeat = 0;
        for (var hunk = 0; hunk < count; hunk++)
        {
            if (repeat > 0)
            {
                map[hunk * MapEntryBytes] = lastCompression;
                repeat--;
                continue;
            }

            var value = (byte)tree.DecodeOne(bits);
            if (value == CompressionRleSmall)
            {
                map[hunk * MapEntryBytes] = lastCompression;
                repeat = 2 + tree.DecodeOne(bits);
            }
            else if (value == CompressionRleLarge)
            {
                map[hunk * MapEntryBytes] = lastCompression;
                repeat = 2 + 16 + (tree.DecodeOne(bits) << 4);
                repeat += tree.DecodeOne(bits);
            }
            else
            {
                map[hunk * MapEntryBytes] = lastCompression = value;
            }
        }

        var current = firstOffset;
        uint lastSelf = 0;
        long lastParent = 0;
        for (var hunk = 0; hunk < count; hunk++)
        {
            var entry = map.AsSpan(hunk * MapEntryBytes, MapEntryBytes);
            var offset = current;
            uint length = 0;
            ushort crc = 0;
            switch (entry[0])
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    length = bits.Read(lengthBits);
                    current += length;
                    crc = (ushort)bits.Read(16);
                    break;
                case CompressionNone:
                    length = (uint)HunkBytes;
                    current += length;
                    crc = (ushort)bits.Read(16);
                    break;
                case CompressionSelf:
                    lastSelf = bits.Read(selfBits);
                    offset = lastSelf;
                    break;
                case CompressionParent:
                    lastParent = bits.Read(parentBits);
                    offset = lastParent;
                    break;
                case CompressionSelf1:
                    lastSelf++;
                    entry[0] = CompressionSelf;
                    offset = lastSelf;
                    break;
                case CompressionSelf0:
                    entry[0] = CompressionSelf;
                    offset = lastSelf;
                    break;
                case CompressionParentSelf:
                case CompressionParent0:
                case CompressionParent1:
                    entry[0] = CompressionParent;
                    break;
                default:
                    throw new ChdException("This chd is damaged: its map does not make sense.");
            }

            entry[1] = (byte)(length >> 16);
            entry[2] = (byte)(length >> 8);
            entry[3] = (byte)length;
            BinaryPrimitives.WriteUInt16BigEndian(entry[4..], (ushort)(offset >> 32));
            BinaryPrimitives.WriteUInt32BigEndian(entry[6..], (uint)offset);
            BinaryPrimitives.WriteUInt16BigEndian(entry[10..], crc);
        }

        if (Crc16(map) != expectedCrc)
        {
            throw new ChdException("This chd is damaged: its map does not match its checksum.");
        }

        return map;
    }

    /// <summary>
    /// Reads the stored data of a batch of hunks in one go. A file not yet in the system's cache takes
    /// far longer to read in thousands of small pieces out of order, from every thread.
    /// </summary>
    private void LoadWindow(uint first, int count)
    {
        _windowLength = 0;
        if (!Compressed)
        {
            return;
        }

        var map = _map!;
        var start = long.MaxValue;
        long end = 0;
        for (var hunk = first; hunk < first + (uint)count; hunk++)
        {
            var entry = map.AsSpan((int)hunk * MapEntryBytes, MapEntryBytes);
            if (entry[0] > CompressionNone)
            {
                continue;
            }

            var offset = ((long)BinaryPrimitives.ReadUInt16BigEndian(entry[4..]) << 32) | BinaryPrimitives.ReadUInt32BigEndian(entry[6..]);
            start = Math.Min(start, offset);
            end = Math.Max(end, offset + ((entry[1] << 16) | (entry[2] << 8) | entry[3]));
        }

        if (end <= start || end - start > MaxWindowBytes)
        {
            return;
        }

        var length = (int)(end - start);
        if (_window.Length < length)
        {
            _window = new byte[length];
        }

        ReadExact(_handle, start, _window.AsSpan(0, length));
        _windowStart = start;
        _windowLength = length;
    }

    private bool InWindow(long offset, int length) => offset >= _windowStart && offset + length <= _windowStart + _windowLength;

    private void ReadHunk(uint hunk, Span<byte> destination, ChdDecoders decoders)
    {
        var map = _map!;
        if (!Compressed)
        {
            var position = (long)BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan((int)hunk * 4)) * HunkBytes;
            if (position == 0)
            {
                destination.Clear();
            }
            else
            {
                ReadExact(_handle, position, destination);
            }

            return;
        }

        var entry = map.AsSpan((int)hunk * MapEntryBytes, MapEntryBytes);
        var length = (entry[1] << 16) | (entry[2] << 8) | entry[3];
        var offset = ((long)BinaryPrimitives.ReadUInt16BigEndian(entry[4..]) << 32) | BinaryPrimitives.ReadUInt32BigEndian(entry[6..]);
        var crc = BinaryPrimitives.ReadUInt16BigEndian(entry[10..]);
        switch (entry[0])
        {
            case 0:
            case 1:
            case 2:
            case 3:
                if (InWindow(offset, length))
                {
                    decoders.Decode(entry[0], _window, (int)(offset - _windowStart), length, destination);
                    break;
                }

                var compressed = decoders.Compressed(length);
                ReadExact(_handle, offset, compressed.AsSpan(0, length));
                decoders.Decode(entry[0], compressed, 0, length, destination);
                break;
            case CompressionNone:
                if (InWindow(offset, HunkBytes))
                {
                    _window.AsSpan((int)(offset - _windowStart), HunkBytes).CopyTo(destination);
                    break;
                }

                ReadExact(_handle, offset, destination);
                break;
            case CompressionSelf:
                if (offset >= hunk)
                {
                    throw new ChdException("This chd is damaged: its map does not make sense.");
                }

                ReadHunk((uint)offset, destination, decoders);
                return;
            default:
                throw new ChdException("This chd only holds the differences from another chd, its parent, so it cannot be read by itself.");
        }

        if (Crc16(destination) != crc)
        {
            throw new ChdException("This chd is damaged: part of its data does not match its checksum.");
        }
    }
}

internal sealed record ChdMetadata(string Tag, byte[] Data);
