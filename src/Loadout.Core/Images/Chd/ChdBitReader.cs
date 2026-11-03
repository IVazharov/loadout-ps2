using System.Numerics;

namespace Loadout.Images;

/// <summary>Reads bits, first the high bit of each byte. Past the end it reads zeros, as MAME's does (BSD-3-Clause; see ThirdParty/MameChd).</summary>
internal sealed class ChdBitReader
{
    private byte[] _source = [];
    private int _position;
    private int _end;
    private int _start;
    private ulong _buffer;
    private int _bits;

    public ChdBitReader()
    {
    }

    public ChdBitReader(byte[] source, int offset, int length) => Reset(source, offset, length);

    /// <summary>The whole bytes used so far, counted from the start.</summary>
    public int BytesConsumed => _position - (_bits / 8) - _start;

    public void Reset(byte[] source, int offset, int length)
    {
        _source = source;
        _start = offset;
        _position = offset;
        _end = offset + length;
        _buffer = 0;
        _bits = 0;
    }

    public uint Peek(int count)
    {
        if (_bits < count)
        {
            Fill();
        }

        return count == 0 ? 0 : (uint)(_buffer >> (64 - count));
    }

    public void Skip(int count)
    {
        if (_bits < count)
        {
            Fill();
        }

        // A shift by 64 would shift by nothing.
        _buffer = count == 64 ? 0 : _buffer << count;
        _bits -= count;
    }

    public uint Read(int count)
    {
        var value = Peek(count);
        Skip(count);
        return value;
    }

    public int ReadSigned(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var value = Read(count);
        return (int)(value << (32 - count)) >> (32 - count);
    }

    /// <summary>Counts the zero bits before the next one bit, and passes that bit.</summary>
    public uint ReadUnary()
    {
        uint count = 0;
        while (true)
        {
            if (_bits == 0)
            {
                if (_position >= _end)
                {
                    throw new ChdException("This chd is damaged: part of its data ends early.");
                }

                Fill();
            }

            var zeros = BitOperations.LeadingZeroCount(_buffer);
            if (zeros < _bits)
            {
                Skip(zeros + 1);
                return count + (uint)zeros;
            }

            count += (uint)_bits;
            _buffer = 0;
            _bits = 0;
        }
    }

    public void AlignToByte() => Skip(_bits % 8);

    private void Fill()
    {
        while (_bits <= 56)
        {
            ulong value = _position < _end ? _source[_position] : (byte)0;
            _position++;
            _buffer |= value << (56 - _bits);
            _bits += 8;
        }
    }
}
