using ZstdSharp;

namespace Loadout.Images;

internal interface IChdCodec
{
    void Decode(byte[] source, int offset, int length, Span<byte> destination);
}

/// <summary>One thread's decoders and buffer, as none of them can be shared.</summary>
internal sealed class ChdDecoders : IDisposable
{
    private readonly IChdCodec?[] _codecs;
    private byte[] _compressed;

    public ChdDecoders(string[] compressors, int hunkBytes)
    {
        _codecs = compressors.Select(tag => tag.Length == 0 ? null : Create(tag, hunkBytes)).ToArray();
        _compressed = new byte[hunkBytes];
    }

    public static bool Knows(string tag) => tag is "zlib" or "zstd" or "lzma" or "huff" or "flac" or "cdzl" or "cdzs" or "cdlz" or "cdfl";

    public byte[] Compressed(int length)
    {
        if (_compressed.Length < length)
        {
            _compressed = new byte[length];
        }

        return _compressed;
    }

    public void Decode(int codec, byte[] source, int offset, int length, Span<byte> destination)
    {
        try
        {
            var decoder = _codecs[codec] ?? throw new ChdException("This chd is damaged: part of its data does not make sense.");
            decoder.Decode(source, offset, length, destination);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or ZstdException or InvalidDataException)
        {
            throw new ChdException("This chd is damaged: part of its data does not make sense.");
        }
    }

    public void Dispose()
    {
        foreach (var codec in _codecs)
        {
            (codec as IDisposable)?.Dispose();
        }
    }

    private static IChdCodec Create(string tag, int hunkBytes) => tag switch
    {
        "zlib" => new ChdZlibCodec(),
        "zstd" => new ChdZstdCodec(),
        "lzma" => new ChdLzmaCodec(),
        "huff" => new ChdHuffmanCodec(),
        "flac" => new ChdFlacCodec(),
        "cdzl" => new ChdCdCodec(new ChdZlibCodec(), new ChdZlibCodec(), hunkBytes),
        "cdzs" => new ChdCdCodec(new ChdZstdCodec(), new ChdZstdCodec(), hunkBytes),
        "cdlz" => new ChdCdCodec(new ChdLzmaCodec(), new ChdZlibCodec(), hunkBytes),
        "cdfl" => new ChdCdFlacCodec(hunkBytes),
        _ => throw new ChdException("This chd uses the " + tag + " compression, which is not used for PS2 games."),
    };
}

internal sealed class ChdZstdCodec : IChdCodec, IDisposable
{
    private readonly Decompressor _decompressor = new();

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        if (!_decompressor.TryUnwrap(source.AsSpan(offset, length), destination, out var written) || written != destination.Length)
        {
            throw new ChdException("This chd is damaged: part of its data ends early.");
        }
    }

    public void Dispose() => _decompressor.Dispose();
}

/// <summary>A code tree in the data's first bits, then a code for each byte.</summary>
internal sealed class ChdHuffmanCodec : IChdCodec
{
    private readonly ChdHuffman _small = new(24, 6);
    private readonly ChdHuffman _tree = new(256, 16);
    private readonly ChdBitReader _bits = new();

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        _bits.Reset(source, offset, length);
        _tree.ImportTreeHuffman(_bits, _small);
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (byte)_tree.DecodeOne(_bits);
        }
    }
}

/// <summary>Whole hunks treated as 16-bit stereo audio. The first byte gives the byte order.</summary>
internal sealed class ChdFlacCodec : IChdCodec
{
    private readonly ChdFlac _flac = new();

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        if (length < 1 || source[offset] is not ((byte)'L' or (byte)'B'))
        {
            throw new ChdException("This chd is damaged: part of its data does not make sense.");
        }

        _flac.Decode(source, offset + 1, length - 1, destination, bigEndian: source[offset] == (byte)'B');
    }
}

/// <summary>
/// CD hunks: the 2352 byte sectors and the 96 bytes of subcode of each frame are compressed apart.
/// Sectors whose sync and error correction could be calculated again were stored without them, and
/// a bit at the start marks each.
/// </summary>
internal sealed class ChdCdCodec : IChdCodec
{
    public const int FrameBytes = SectorBytes + SubcodeBytes;
    public const int SectorBytes = 2352;
    public const int SubcodeBytes = 96;

    private readonly IChdCodec _sectors;
    private readonly IChdCodec _subcode;
    private readonly byte[] _buffer;

    public ChdCdCodec(IChdCodec sectors, IChdCodec subcode, int hunkBytes)
    {
        _sectors = sectors;
        _subcode = subcode;
        _buffer = new byte[hunkBytes];
    }

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        var frames = destination.Length / FrameBytes;
        var lengthBytes = destination.Length < 65536 ? 2 : 3;
        var flagBytes = (frames + 7) / 8;
        var headerBytes = flagBytes + lengthBytes;
        var sectorsLength = (source[offset + flagBytes] << 8) | source[offset + flagBytes + 1];
        if (lengthBytes > 2)
        {
            sectorsLength = (sectorsLength << 8) | source[offset + flagBytes + 2];
        }

        var sectorBytes = frames * SectorBytes;
        _sectors.Decode(source, offset + headerBytes, sectorsLength, _buffer.AsSpan(0, sectorBytes));
        _subcode.Decode(source, offset + headerBytes + sectorsLength, length - headerBytes - sectorsLength, _buffer.AsSpan(sectorBytes, frames * SubcodeBytes));

        for (var frame = 0; frame < frames; frame++)
        {
            var target = destination.Slice(frame * FrameBytes, FrameBytes);
            _buffer.AsSpan(frame * SectorBytes, SectorBytes).CopyTo(target);
            _buffer.AsSpan(sectorBytes + (frame * SubcodeBytes), SubcodeBytes).CopyTo(target[SectorBytes..]);
            if ((source[offset + (frame / 8)] & (1 << (frame % 8))) != 0)
            {
                ChdCdEcc.Restore(target);
            }
        }
    }
}

/// <summary>CD hunks whose sectors are 16-bit big-endian audio, followed by the subcode in deflate.</summary>
internal sealed class ChdCdFlacCodec : IChdCodec
{
    private readonly ChdFlac _flac = new();
    private readonly ChdZlibCodec _subcode = new();
    private readonly byte[] _buffer;

    public ChdCdFlacCodec(int hunkBytes)
    {
        _buffer = new byte[hunkBytes];
    }

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        var frames = destination.Length / ChdCdCodec.FrameBytes;
        var sectorBytes = frames * ChdCdCodec.SectorBytes;
        var used = _flac.Decode(source, offset, length, _buffer.AsSpan(0, sectorBytes), bigEndian: true);
        _subcode.Decode(source, offset + used, length - used, _buffer.AsSpan(sectorBytes, frames * ChdCdCodec.SubcodeBytes));
        for (var frame = 0; frame < frames; frame++)
        {
            var target = destination.Slice(frame * ChdCdCodec.FrameBytes, ChdCdCodec.FrameBytes);
            _buffer.AsSpan(frame * ChdCdCodec.SectorBytes, ChdCdCodec.SectorBytes).CopyTo(target);
            _buffer.AsSpan(sectorBytes + (frame * ChdCdCodec.SubcodeBytes), ChdCdCodec.SubcodeBytes).CopyTo(target[ChdCdCodec.SectorBytes..]);
        }
    }
}

/// <summary>Puts back a CD sector's sync bytes and error correction codes, ported from MAME's cdrom.cpp (BSD-3-Clause; see ThirdParty/MameChd).</summary>
internal static class ChdCdEcc
{
    private const int ModeOffset = 15;
    private const int DataOffset = 12;
    private const int ParityPOffset = 0x81C;
    private const int ParityPBytes = 86;
    private const int ParityPComponents = 24;
    private const int ParityQOffset = ParityPOffset + (2 * ParityPBytes);
    private const int ParityQBytes = 52;
    private const int ParityQComponents = 43;

    private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
    private static readonly byte[] Low = new byte[256];
    private static readonly byte[] High = new byte[256];
    private static readonly int[] ParityP = new int[ParityPBytes * ParityPComponents];
    private static readonly int[] ParityQ = new int[ParityQBytes * ParityQComponents];

    static ChdCdEcc()
    {
        for (var index = 0; index < 256; index++)
        {
            var doubled = (index << 1) ^ ((index & 0x80) != 0 ? 0x11D : 0);
            Low[index] = (byte)doubled;
            High[index ^ (byte)doubled] = (byte)index;
        }

        for (var row = 0; row < ParityPBytes; row++)
        {
            for (var component = 0; component < ParityPComponents; component++)
            {
                ParityP[(row * ParityPComponents) + component] = row + (86 * component);
            }
        }

        for (var row = 0; row < ParityQBytes; row++)
        {
            for (var component = 0; component < ParityQComponents; component++)
            {
                ParityQ[(row * ParityQComponents) + component] = (2 * (((row >> 1) * 43 + (component * 44)) % 1118)) + (row & 1);
            }
        }
    }

    public static void Restore(Span<byte> sector)
    {
        Sync.CopyTo(sector);
        for (var row = 0; row < ParityPBytes; row++)
        {
            var (first, second) = Compute(sector, ParityP.AsSpan(row * ParityPComponents, ParityPComponents));
            sector[ParityPOffset + row] = first;
            sector[ParityPOffset + ParityPBytes + row] = second;
        }

        for (var row = 0; row < ParityQBytes; row++)
        {
            var (first, second) = Compute(sector, ParityQ.AsSpan(row * ParityQComponents, ParityQComponents));
            sector[ParityQOffset + row] = first;
            sector[ParityQOffset + ParityQBytes + row] = second;
        }
    }

    private static (byte First, byte Second) Compute(ReadOnlySpan<byte> sector, ReadOnlySpan<int> offsets)
    {
        byte first = 0;
        byte second = 0;
        foreach (var offset in offsets)
        {
            // In mode 2 the error correction takes the first four data bytes as zero.
            var value = sector[ModeOffset] == 2 && offset < 4 ? (byte)0 : sector[DataOffset + offset];
            first ^= value;
            second ^= value;
            first = Low[first];
        }

        first = High[Low[first] ^ second];
        second ^= first;
        return (first, second);
    }
}
