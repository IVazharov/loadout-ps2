namespace Loadout.Images;

/// <summary>
/// Decodes raw deflate. Written after Mark Adler's puff.c and altered from it; see ThirdParty/Puff. A CD's every hunk and many of a DVD's are deflate, and
/// a DeflateStream for each of them, on every core, leaves the garbage collector stopping the threads
/// so often that a whole game took four times as long. This decoder allocates nothing per hunk.
/// </summary>
internal sealed class ChdZlibCodec : IChdCodec
{
    private const int MaxBits = 15;
    private const int LiteralCodes = 288;
    private const int DistanceCodes = 30;
    private const int EndOfBlock = 256;

    private static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly byte[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private readonly Table _literals = new(LiteralCodes);
    private readonly Table _distances = new(DistanceCodes);
    private readonly Table _codeLengths = new(19);
    private readonly Table _fixedLiterals = new(LiteralCodes);
    private readonly Table _fixedDistances = new(DistanceCodes);
    private readonly byte[] _lengths = new byte[LiteralCodes + DistanceCodes + 2];
    private byte[] _source = [];
    private int _position;
    private int _end;
    private uint _buffer;
    private int _bits;

    public ChdZlibCodec()
    {
        var lengths = _lengths.AsSpan(0, LiteralCodes);
        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..288].Fill(8);
        _fixedLiterals.Build(lengths);
        _lengths.AsSpan(0, DistanceCodes).Fill(5);
        _fixedDistances.Build(_lengths.AsSpan(0, DistanceCodes));
    }

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        _source = source;
        _position = offset;
        _end = offset + length;
        _buffer = 0;
        _bits = 0;
        var output = 0;
        bool last;
        do
        {
            last = ReadBits(1) != 0;
            switch (ReadBits(2))
            {
                case 0:
                    output = CopyStored(destination, output);
                    break;
                case 1:
                    output = DecodeBlock(destination, output, _fixedLiterals, _fixedDistances);
                    break;
                case 2:
                    ReadDynamicTables();
                    output = DecodeBlock(destination, output, _literals, _distances);
                    break;
                default:
                    throw Damaged();
            }
        }
        while (!last);

        if (output != destination.Length)
        {
            throw new ChdException("This chd is damaged: part of its data ends early.");
        }
    }

    private static ChdException Damaged() => new("This chd is damaged: part of its data does not make sense.");

    private uint ReadBits(int count)
    {
        while (_bits < count)
        {
            if (_position >= _end)
            {
                throw new ChdException("This chd is damaged: part of its data ends early.");
            }

            _buffer |= (uint)_source[_position++] << _bits;
            _bits += 8;
        }

        var value = _buffer & ((1u << count) - 1);
        _buffer >>= count;
        _bits -= count;
        return value;
    }

    private int DecodeSymbol(Table table)
    {
        // Codes of each length are consecutive, so a code can be told by counting up the lengths.
        var code = 0;
        var first = 0;
        var index = 0;
        var counts = table.Counts;
        for (var length = 1; length <= MaxBits; length++)
        {
            code |= (int)ReadBits(1);
            int count = counts[length];
            if (code - count < first)
            {
                return table.Symbols[index + (code - first)];
            }

            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }

        throw Damaged();
    }

    private int CopyStored(Span<byte> destination, int output)
    {
        // Back to a whole byte: any bytes already taken into the buffer are given back.
        _position -= _bits / 8;
        _buffer = 0;
        _bits = 0;
        if (_position + 4 > _end)
        {
            throw new ChdException("This chd is damaged: part of its data ends early.");
        }

        var length = _source[_position] | (_source[_position + 1] << 8);
        var complement = _source[_position + 2] | (_source[_position + 3] << 8);
        _position += 4;
        if ((length ^ 0xFFFF) != complement || _position + length > _end || output + length > destination.Length)
        {
            throw Damaged();
        }

        _source.AsSpan(_position, length).CopyTo(destination[output..]);
        _position += length;
        return output + length;
    }

    private int DecodeBlock(Span<byte> destination, int output, Table literals, Table distances)
    {
        while (true)
        {
            var symbol = DecodeSymbol(literals);
            if (symbol < EndOfBlock)
            {
                if (output >= destination.Length)
                {
                    throw Damaged();
                }

                destination[output++] = (byte)symbol;
                continue;
            }

            if (symbol == EndOfBlock)
            {
                return output;
            }

            symbol -= 257;
            if (symbol >= LengthBase.Length)
            {
                throw Damaged();
            }

            var length = LengthBase[symbol] + (int)ReadBits(LengthExtra[symbol]);
            var code = DecodeSymbol(distances);
            if (code >= DistanceBase.Length)
            {
                throw Damaged();
            }

            var distance = DistanceBase[code] + (int)ReadBits(DistanceExtra[code]);
            if (distance > output || output + length > destination.Length)
            {
                throw Damaged();
            }

            for (var index = 0; index < length; index++)
            {
                destination[output] = destination[output - distance];
                output++;
            }
        }
    }

    private void ReadDynamicTables()
    {
        var literalCount = (int)ReadBits(5) + 257;
        var distanceCount = (int)ReadBits(5) + 1;
        var codeLengthCount = (int)ReadBits(4) + 4;
        if (literalCount > 286 || distanceCount > DistanceCodes)
        {
            throw Damaged();
        }

        Array.Clear(_lengths);
        for (var index = 0; index < codeLengthCount; index++)
        {
            _lengths[CodeLengthOrder[index]] = (byte)ReadBits(3);
        }

        _codeLengths.Build(_lengths.AsSpan(0, 19));
        Array.Clear(_lengths);
        var total = literalCount + distanceCount;
        var position = 0;
        while (position < total)
        {
            var symbol = DecodeSymbol(_codeLengths);
            if (symbol < 16)
            {
                _lengths[position++] = (byte)symbol;
                continue;
            }

            byte value = 0;
            int repeat;
            if (symbol == 16)
            {
                if (position == 0)
                {
                    throw Damaged();
                }

                value = _lengths[position - 1];
                repeat = 3 + (int)ReadBits(2);
            }
            else
            {
                repeat = symbol == 17 ? 3 + (int)ReadBits(3) : 11 + (int)ReadBits(7);
            }

            if (position + repeat > total)
            {
                throw Damaged();
            }

            _lengths.AsSpan(position, repeat).Fill(value);
            position += repeat;
        }

        _literals.Build(_lengths.AsSpan(0, literalCount));
        _distances.Build(_lengths.AsSpan(literalCount, distanceCount));
    }

    /// <summary>A canonical code: how many codes have each length, and the symbols in code order.</summary>
    private sealed class Table
    {
        private readonly ushort[] _offsets = new ushort[MaxBits + 2];

        public Table(int symbols)
        {
            Symbols = new ushort[symbols];
        }

        public ushort[] Counts { get; } = new ushort[MaxBits + 1];

        public ushort[] Symbols { get; }

        public void Build(ReadOnlySpan<byte> lengths)
        {
            Array.Clear(Counts);
            foreach (var length in lengths)
            {
                Counts[length]++;
            }

            // Too many codes of some length cannot be a code at all. Too few is allowed, as a block
            // may use a single distance code.
            var left = 1;
            for (var length = 1; length <= MaxBits; length++)
            {
                left = (left << 1) - Counts[length];
                if (left < 0)
                {
                    throw Damaged();
                }
            }

            _offsets[1] = 0;
            for (var length = 1; length <= MaxBits; length++)
            {
                _offsets[length + 1] = (ushort)(_offsets[length] + Counts[length]);
            }

            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != 0)
                {
                    Symbols[_offsets[lengths[symbol]]++] = (ushort)symbol;
                }
            }
        }
    }
}
