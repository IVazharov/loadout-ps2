namespace Loadout.Images;

/// <summary>MAME's canonical Huffman decoder, ported from its huffman.cpp (BSD-3-Clause; see ThirdParty/MameChd).</summary>
internal sealed class ChdHuffman
{
    private readonly int _codes;
    private readonly int _maxBits;
    private readonly byte[] _lengths;
    private readonly uint[] _values;
    private readonly ushort[] _lookup;

    public ChdHuffman(int codes, int maxBits)
    {
        _codes = codes;
        _maxBits = maxBits;
        _lengths = new byte[codes];
        _values = new uint[codes];
        _lookup = new ushort[1 << maxBits];
    }

    public int DecodeOne(ChdBitReader bits)
    {
        var entry = _lookup[bits.Peek(_maxBits)];
        bits.Skip(entry & 0x1F);
        return entry >> 5;
    }

    public void ImportTreeRle(ChdBitReader bits)
    {
        var width = _maxBits >= 16 ? 5 : _maxBits >= 8 ? 4 : 3;
        var node = 0;
        while (node < _codes)
        {
            var length = (int)bits.Read(width);
            if (length != 1)
            {
                _lengths[node++] = (byte)length;
                continue;
            }

            length = (int)bits.Read(width);
            if (length == 1)
            {
                _lengths[node++] = 1;
                continue;
            }

            var repeat = (int)bits.Read(width) + 3;
            if (node + repeat > _codes)
            {
                throw new ChdException("This chd is damaged: part of its data does not make sense.");
            }

            _lengths.AsSpan(node, repeat).Fill((byte)length);
            node += repeat;
        }

        Build();
    }

    public void ImportTreeHuffman(ChdBitReader bits, ChdHuffman small)
    {
        // A small tree first, which the code lengths of the real one are coded with.
        small._lengths[0] = (byte)bits.Read(3);
        var start = (int)bits.Read(3) + 1;
        var count = 0;
        for (var index = 1; index < 24; index++)
        {
            if (index < start || count == 7)
            {
                small._lengths[index] = 0;
            }
            else
            {
                count = (int)bits.Read(3);
                small._lengths[index] = count == 7 ? (byte)0 : (byte)count;
            }
        }

        small.Build();

        var remainder = _codes - 9;
        var fullBits = 0;
        while (remainder != 0)
        {
            remainder >>= 1;
            fullBits++;
        }

        byte last = 0;
        var code = 0;
        while (code < _codes)
        {
            var value = small.DecodeOne(bits);
            if (value != 0)
            {
                last = (byte)(value - 1);
                _lengths[code++] = last;
                continue;
            }

            var repeat = (int)bits.Read(3) + 2;
            if (repeat == 7 + 2)
            {
                repeat += (int)bits.Read(fullBits);
            }

            for (; repeat != 0 && code < _codes; repeat--)
            {
                _lengths[code++] = last;
            }
        }

        Build();
    }

    private void Build()
    {
        // Long codes take the lowest values.
        Span<uint> starts = stackalloc uint[33];
        starts.Clear();
        foreach (var length in _lengths)
        {
            if (length > _maxBits)
            {
                throw new ChdException("This chd is damaged: part of its data does not make sense.");
            }

            starts[length]++;
        }

        uint current = 0;
        for (var length = 32; length > 0; length--)
        {
            var next = (current + starts[length]) >> 1;
            if (length != 1 && next * 2 != current + starts[length])
            {
                throw new ChdException("This chd is damaged: part of its data does not make sense.");
            }

            starts[length] = current;
            current = next;
        }

        Array.Clear(_lookup);
        for (var code = 0; code < _codes; code++)
        {
            var length = _lengths[code];
            if (length == 0)
            {
                continue;
            }

            _values[code] = starts[length]++;
            var shift = _maxBits - length;
            var first = _values[code] << shift;
            var size = 1u << shift;
            if (first + size > _lookup.Length)
            {
                throw new ChdException("This chd is damaged: part of its data does not make sense.");
            }

            _lookup.AsSpan((int)first, (int)size).Fill((ushort)((code << 5) | length));
        }
    }
}
