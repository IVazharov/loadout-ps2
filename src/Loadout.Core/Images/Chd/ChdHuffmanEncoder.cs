namespace Loadout.Images;

/// <summary>
/// MAME's canonical Huffman encoder, ported from its huffman.cpp (BSD-3-Clause; see ThirdParty/MameChd):
/// a tree built from the counts of each code, flattened until no code is longer than maxBits, and
/// written out in either of the two forms ChdHuffman reads.
/// </summary>
internal sealed class ChdHuffmanEncoder
{
    private readonly int _codes;
    private readonly int _maxBits;
    private readonly uint[] _histogram;
    private readonly byte[] _lengths;
    private readonly uint[] _values;
    private readonly ulong[] _weights;
    private readonly int[] _parents;
    private readonly int[] _list;
    private readonly Comparer<int> _heaviestFirst;
    private readonly List<int> _symbols = [];
    private readonly List<int> _repeats = [];

    public ChdHuffmanEncoder(int codes, int maxBits)
    {
        _codes = codes;
        _maxBits = maxBits;
        _histogram = new uint[codes];
        _lengths = new byte[codes];
        _values = new uint[codes];
        _weights = new ulong[codes * 2];
        _parents = new int[codes * 2];
        _list = new int[codes * 2];

        // Largest weight first; equal weights by code, as MAME sorts them.
        _heaviestFirst = Comparer<int>.Create((a, b) => _weights[a] != _weights[b] ? _weights[b].CompareTo(_weights[a]) : a.CompareTo(b));
    }

    public void ResetHistogram() => Array.Clear(_histogram);

    public void Count(int code) => _histogram[code]++;

    public int Length(int code) => _lengths[code];

    public void Encode(ChdBitWriter bits, int code) => bits.Write(_values[code], _lengths[code]);

    /// <summary>The fewest bits any code for single symbols could take for what was counted.</summary>
    public double EntropyBits()
    {
        double total = 0;
        foreach (var count in _histogram)
        {
            total += count;
        }

        double bits = 0;
        foreach (var count in _histogram)
        {
            if (count != 0)
            {
                bits += count * Math.Log2(total / count);
            }
        }

        return bits;
    }

    /// <summary>Gives each code its length and value from the counts, as MAME's compute_tree_from_histo.</summary>
    public void ComputeTree()
    {
        uint total = 0;
        foreach (var count in _histogram)
        {
            total += count;
        }

        // Search for the largest weighting of the real counts that still fits in maxBits.
        uint lower = 0;
        var upper = total * 2;
        while (true)
        {
            var current = (upper + lower) / 2;
            var bits = BuildTree(total, current);
            if (bits <= _maxBits)
            {
                lower = current;
                if (current == total || upper - lower <= 1)
                {
                    break;
                }
            }
            else
            {
                upper = current;
            }
        }

        AssignCanonicalCodes();
    }

    /// <summary>The code lengths, run-length coded in fixed-width fields (MAME's export_tree_rle).</summary>
    public void ExportTreeRle(ChdBitWriter bits)
    {
        var width = _maxBits >= 16 ? 5 : _maxBits >= 8 ? 4 : 3;
        var repeat = 0;
        var last = -1;
        for (var code = 0; code < _codes; code++)
        {
            int value = _lengths[code];
            if (value == last)
            {
                repeat++;
            }
            else
            {
                if (repeat != 0)
                {
                    WriteRleTreeBits(bits, last, repeat, width);
                }

                last = value;
                repeat = 1;
            }
        }

        WriteRleTreeBits(bits, last, repeat, width);
    }

    /// <summary>The code lengths, run-length coded with a small Huffman tree of their own (MAME's export_tree_huffman).</summary>
    public void ExportTreeHuffman(ChdBitWriter bits, ChdHuffmanEncoder small)
    {
        // Each run of one length becomes the length plus one, then, when it goes on, a 0 and its count.
        var symbols = _symbols;
        var repeats = _repeats;
        symbols.Clear();
        repeats.Clear();
        small.ResetHistogram();
        var last = 0;
        var code = 0;
        while (code < _codes)
        {
            int value = _lengths[code];
            var run = 1;
            while (code + run < _codes && _lengths[code + run] == value)
            {
                run++;
            }

            code += run;
            if (value != last || run == 1)
            {
                symbols.Add(value + 1);
                small.Count(value + 1);
                last = value;
                run--;
            }

            while (run > 0)
            {
                if (run == 1)
                {
                    symbols.Add(value + 1);
                    small.Count(value + 1);
                    run = 0;
                    break;
                }

                // A repeat of 2 to 8 fits in 3 bits; 7 there means 9 or more, with the rest after it.
                var take = Math.Min(run, 9 + ((1 << RepeatFullBits()) - 1));
                symbols.Add(0);
                small.Count(0);
                repeats.Add(take);
                run -= take;
            }
        }

        small.ComputeTree();

        var firstNonZero = 31;
        var lastNonZero = 0;
        for (var index = 1; index < small._codes; index++)
        {
            if (small._lengths[index] != 0)
            {
                if (firstNonZero == 31)
                {
                    firstNonZero = index;
                }

                lastNonZero = index;
            }
        }

        firstNonZero = Math.Min(firstNonZero, 8);
        bits.Write(small._lengths[0], 3);
        bits.Write((uint)(firstNonZero - 1), 3);
        for (var index = firstNonZero; index <= lastNonZero; index++)
        {
            bits.Write(small._lengths[index], 3);
        }

        bits.Write(7, 3);

        var fullBits = RepeatFullBits();
        var next = 0;
        foreach (var symbol in symbols)
        {
            small.Encode(bits, symbol);
            if (symbol == 0)
            {
                var length = repeats[next++] - 2;
                bits.Write((uint)Math.Min(length, 7), 3);
                if (length >= 7)
                {
                    bits.Write((uint)(length - 7), fullBits);
                }
            }
        }
    }

    private static void WriteRleTreeBits(ChdBitWriter bits, int value, int repeat, int width)
    {
        while (repeat > 0)
        {
            if (value == 1)
            {
                // 1 is the escape, so a real 1 is written twice.
                bits.Write(1, width);
                bits.Write(1, width);
                repeat--;
            }
            else if (repeat <= 2)
            {
                bits.Write((uint)value, width);
                repeat--;
            }
            else
            {
                var count = Math.Min(repeat - 3, (1 << width) - 1);
                bits.Write(1, width);
                bits.Write((uint)value, width);
                bits.Write((uint)count, width);
                repeat -= count + 3;
            }
        }
    }

    private int RepeatFullBits()
    {
        var remainder = _codes - 9;
        var bits = 0;
        while (remainder != 0)
        {
            remainder >>= 1;
            bits++;
        }

        return bits;
    }

    private int BuildTree(uint total, uint weight)
    {
        var items = 0;
        for (var code = 0; code < _codes; code++)
        {
            _parents[code] = -1;
            _weights[code] = 0;
            if (_histogram[code] != 0)
            {
                _list[items++] = code;
                _weights[code] = Math.Max(1, (ulong)_histogram[code] * weight / total);
            }
        }

        Array.Sort(_list, 0, items, _heaviestFirst);

        var next = _codes;
        while (items > 1)
        {
            var node1 = _list[--items];
            var node0 = _list[--items];
            var created = next++;
            _parents[created] = -1;
            _weights[created] = _weights[node0] + _weights[node1];
            _parents[node0] = created;
            _parents[node1] = created;

            var position = 0;
            while (position < items && _weights[created] <= _weights[_list[position]])
            {
                position++;
            }

            Array.Copy(_list, position, _list, position + 1, items - position);
            _list[position] = created;
            items++;
        }

        var maxBits = 0;
        for (var code = 0; code < _codes; code++)
        {
            var bits = 0;
            if (_histogram[code] != 0)
            {
                for (var node = code; _parents[node] >= 0; node = _parents[node])
                {
                    bits++;
                }

                // A single code still needs one bit.
                bits = Math.Max(bits, 1);
            }

            _lengths[code] = (byte)Math.Min(bits, 255);
            maxBits = Math.Max(maxBits, bits);
        }

        return maxBits;
    }

    private void AssignCanonicalCodes()
    {
        // As ChdHuffman builds its table: long codes take the lowest values.
        Span<uint> starts = stackalloc uint[33];
        starts.Clear();
        foreach (var length in _lengths)
        {
            starts[length]++;
        }

        uint current = 0;
        for (var length = 32; length > 0; length--)
        {
            var next = (current + starts[length]) >> 1;
            if (length != 1 && next * 2 != current + starts[length])
            {
                throw new InvalidOperationException("The Huffman code lengths do not make a complete code.");
            }

            starts[length] = current;
            current = next;
        }

        for (var code = 0; code < _codes; code++)
        {
            if (_lengths[code] != 0)
            {
                _values[code] = starts[_lengths[code]]++;
            }
        }
    }
}
