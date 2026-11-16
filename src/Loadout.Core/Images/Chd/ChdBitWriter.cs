namespace Loadout.Images;

/// <summary>
/// Writes bits, the high bit of each byte first, as ChdBitReader reads them and as MAME's bitstream_out
/// writes them (BSD-3-Clause; see ThirdParty/MameChd). Past the limit it only notes the overflow.
/// </summary>
internal sealed class ChdBitWriter
{
    private byte[] _destination = [];
    private int _start;
    private int _position;
    private int _limit;
    private ulong _accumulator;
    private int _bits;

    public bool Overflow { get; private set; }

    public void Reset(byte[] destination, int offset, int limit)
    {
        _destination = destination;
        _start = offset;
        _position = offset;
        _limit = Math.Min(destination.Length, offset + limit);
        _accumulator = 0;
        _bits = 0;
        Overflow = false;
    }

    /// <summary>Writes the low count bits of value, count from 0 to 32.</summary>
    public void Write(uint value, int count)
    {
        if (count == 0)
        {
            return;
        }

        _accumulator = (_accumulator << count) | (value & (uint)((1UL << count) - 1));
        _bits += count;
        while (_bits >= 8)
        {
            _bits -= 8;
            Emit((byte)(_accumulator >> _bits));
        }

        _accumulator &= (1UL << _bits) - 1;
    }

    /// <summary>Writes out the last bits, padded with zeros, and gives the bytes written.</summary>
    public int Flush()
    {
        if (_bits > 0)
        {
            Emit((byte)(_accumulator << (8 - _bits)));
            _bits = 0;
            _accumulator = 0;
        }

        return _position - _start;
    }

    private void Emit(byte value)
    {
        if (_position < _limit)
        {
            _destination[_position++] = value;
        }
        else
        {
            Overflow = true;
        }
    }
}
