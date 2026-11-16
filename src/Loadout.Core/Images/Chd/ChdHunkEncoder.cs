using System.IO.Compression;

namespace Loadout.Images;

/// <summary>
/// One thread's compressors. Each hunk is tried with every codec of its kind of chd, as chdman does, and
/// the smallest result is kept; a hunk none of them makes smaller is stored as it is.
/// </summary>
internal sealed class ChdHunkEncoder : IDisposable
{
    /// <summary>The codec number of a hunk stored as it is.</summary>
    public const int Uncompressed = -1;

    /// <summary>chdman's createdvd list without FLAC, which only wins on audio: LZMA, zlib, Huffman.</summary>
    public static readonly string[] DvdCompressors = ["lzma", "zlib", "huff", ""];

    /// <summary>chdman's createcd list without FLAC: the sectors in LZMA or zlib, the subcode in zlib.</summary>
    public static readonly string[] CdCompressors = ["cdlz", "cdzl", "", ""];

    private const int DvdLzma = 0;
    private const int DvdZlib = 1;
    private const int DvdHuffman = 2;
    private const int CdLzma = 0;
    private const int CdZlib = 1;

    private readonly bool _cd;
    private readonly int _hunkBytes;
    private readonly ChdLzmaEncoder _lzma;
    private readonly MemoryStream _deflated;
    private readonly ChdHuffmanEncoder _huffman = new(256, 16);
    private readonly ChdHuffmanEncoder _small = new(24, 6);
    private readonly ChdBitWriter _bits = new();
    private readonly byte[] _first;
    private readonly byte[] _second;
    private readonly byte[] _frames;
    private readonly byte[] _subcode;

    public ChdHunkEncoder(bool cd, int hunkBytes)
    {
        _cd = cd;
        _hunkBytes = hunkBytes;
        var frames = hunkBytes / ChdCdCodec.FrameBytes;
        _lzma = new ChdLzmaEncoder(cd ? frames * ChdCdCodec.SectorBytes : hunkBytes);
        _deflated = new MemoryStream(hunkBytes + 1024);
        _first = new byte[hunkBytes];
        _second = new byte[hunkBytes];
        _frames = new byte[hunkBytes];
        _subcode = new byte[hunkBytes];
    }

    public void Dispose() => _deflated.Dispose();

    /// <summary>
    /// Compresses one whole hunk into destination, which holds a hunk. Gives the codec's number in the
    /// header's list and the length, or Uncompressed and the hunk's size when nothing makes it smaller.
    /// </summary>
    public (int Codec, int Length) Encode(ReadOnlySpan<byte> hunk, byte[] destination)
        => _cd ? EncodeCd(hunk, destination) : EncodeDvd(hunk, destination);

    private (int Codec, int Length) EncodeDvd(ReadOnlySpan<byte> hunk, byte[] destination)
    {
        var codec = Uncompressed;
        var best = _hunkBytes;

        // zlib first, as it is the quickest: its size then cuts LZMA and Huffman short.
        var length = Deflate(hunk, _first, best);
        if (length >= 0)
        {
            (codec, best) = (DvdZlib, length);
            _first.AsSpan(0, length).CopyTo(destination);
        }

        length = _lzma.Encode(hunk, _second, best);
        if (length >= 0)
        {
            (codec, best) = (DvdLzma, length);
            _second.AsSpan(0, length).CopyTo(destination);
        }

        length = Huffman(hunk, _second, best);
        if (length >= 0)
        {
            (codec, best) = (DvdHuffman, length);
            _second.AsSpan(0, length).CopyTo(destination);
        }

        if (codec == Uncompressed)
        {
            hunk.CopyTo(destination);
        }

        return (codec, best);
    }

    private (int Codec, int Length) EncodeCd(ReadOnlySpan<byte> hunk, byte[] destination)
    {
        var frames = _hunkBytes / ChdCdCodec.FrameBytes;
        var sectorBytes = frames * ChdCdCodec.SectorBytes;
        var subcodeBytes = frames * ChdCdCodec.SubcodeBytes;
        var lengthBytes = _hunkBytes < 65536 ? 2 : 3;
        var flagBytes = (frames + 7) / 8;
        var headerBytes = flagBytes + lengthBytes;

        // The sectors side by side, then the subcode; a sector whose sync and parities can be
        // calculated again is stored without them, and its bit is set in the flags.
        destination.AsSpan(0, headerBytes).Clear();
        for (var frame = 0; frame < frames; frame++)
        {
            var source = hunk.Slice(frame * ChdCdCodec.FrameBytes, ChdCdCodec.FrameBytes);
            var sector = _frames.AsSpan(frame * ChdCdCodec.SectorBytes, ChdCdCodec.SectorBytes);
            source[..ChdCdCodec.SectorBytes].CopyTo(sector);
            source[ChdCdCodec.SectorBytes..].CopyTo(_frames.AsSpan(sectorBytes + (frame * ChdCdCodec.SubcodeBytes)));
            if (ChdCdEcc.Verify(sector))
            {
                destination[frame / 8] |= (byte)(1 << (frame % 8));
                ChdCdEcc.Clear(sector);
            }
        }

        var subcode = Deflate(_frames.AsSpan(sectorBytes, subcodeBytes), _subcode, _hunkBytes);
        if (subcode < 0)
        {
            return StoreCd(hunk, destination);
        }

        var codec = Uncompressed;
        var best = _hunkBytes;
        var sectors = _frames.AsSpan(0, sectorBytes);
        var baseLength = Deflate(sectors, _first, best - headerBytes - subcode);
        if (baseLength >= 0)
        {
            (codec, best) = (CdZlib, headerBytes + baseLength + subcode);
            Assemble(destination, flagBytes, lengthBytes, _first, baseLength, subcode);
        }

        var lzmaLimit = best - headerBytes - subcode;
        baseLength = lzmaLimit > 0 ? _lzma.Encode(sectors, _second, lzmaLimit) : -1;
        if (baseLength >= 0)
        {
            (codec, best) = (CdLzma, headerBytes + baseLength + subcode);
            Assemble(destination, flagBytes, lengthBytes, _second, baseLength, subcode);
        }

        return codec == Uncompressed ? StoreCd(hunk, destination) : (codec, best);
    }

    private (int Codec, int Length) StoreCd(ReadOnlySpan<byte> hunk, byte[] destination)
    {
        hunk.CopyTo(destination);
        return (Uncompressed, _hunkBytes);
    }

    private void Assemble(byte[] destination, int flagBytes, int lengthBytes, byte[] compressed, int baseLength, int subcode)
    {
        for (var index = 0; index < lengthBytes; index++)
        {
            destination[flagBytes + index] = (byte)(baseLength >> ((lengthBytes - 1 - index) * 8));
        }

        var start = flagBytes + lengthBytes;
        compressed.AsSpan(0, baseLength).CopyTo(destination.AsSpan(start));
        _subcode.AsSpan(0, subcode).CopyTo(destination.AsSpan(start + baseLength));
    }

    /// <summary>Raw deflate, as chdman's zlib codec writes it; -1 when it would take limit bytes or more.</summary>
    private int Deflate(ReadOnlySpan<byte> source, byte[] destination, int limit)
    {
        if (limit <= 0)
        {
            return -1;
        }

        _deflated.SetLength(0);
        using (var deflate = new DeflateStream(_deflated, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(source);
        }

        var length = (int)_deflated.Length;
        if (length >= limit)
        {
            return -1;
        }

        _deflated.GetBuffer().AsSpan(0, length).CopyTo(destination);
        return length;
    }

    /// <summary>A code tree, then a code for each byte; -1 when it would take limit bytes or more.</summary>
    private int Huffman(ReadOnlySpan<byte> source, byte[] destination, int limit)
    {
        if (limit <= 0)
        {
            return -1;
        }

        _huffman.ResetHistogram();
        foreach (var value in source)
        {
            _huffman.Count(value);
        }

        // No code for single bytes beats their entropy: when that alone reaches the limit, the codes
        // would too, so the tree is not worth building. The result is the same, sooner.
        if (_huffman.EntropyBits() / 8 >= limit + 1)
        {
            return -1;
        }

        _huffman.ComputeTree();
        _bits.Reset(destination, 0, limit);
        _huffman.ExportTreeHuffman(_bits, _small);
        foreach (var value in source)
        {
            _huffman.Encode(_bits, value);
            if (_bits.Overflow)
            {
                return -1;
            }
        }

        var length = _bits.Flush();
        return _bits.Overflow || length >= limit ? -1 : length;
    }
}
