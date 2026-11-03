using System.Buffers.Binary;

namespace Loadout.Images;

/// <summary>
/// Decodes the FLAC frames chdman stores, written from the FLAC format's specification. Only what
/// CHD holds is read: frames of 16-bit stereo, with no stream header. The frames' own checksums are
/// not read, as each hunk has a CRC.
/// </summary>
internal sealed class ChdFlac
{
    private readonly ChdBitReader _bits = new();
    private readonly int[] _coefficients = new int[32];
    private int[] _first = [];
    private int[] _second = [];

    /// <summary>Fills the destination and returns the number of bytes the frames took.</summary>
    public int Decode(byte[] source, int offset, int length, Span<byte> destination, bool bigEndian)
    {
        _bits.Reset(source, offset, length);
        var output = 0;
        while (output < destination.Length)
        {
            output += DecodeFrame(destination[output..], bigEndian);
        }

        if (_bits.BytesConsumed > length)
        {
            throw Damaged();
        }

        return _bits.BytesConsumed;
    }

    private static ChdException Damaged() => new("This chd is damaged: part of its data does not make sense.");

    private int DecodeFrame(Span<byte> destination, bool bigEndian)
    {
        if (_bits.Read(14) != 0x3FFE)
        {
            throw Damaged();
        }

        _bits.Skip(2);
        var blockCode = (int)_bits.Read(4);
        var rateCode = (int)_bits.Read(4);
        var channelCode = (int)_bits.Read(4);
        var sizeCode = (int)_bits.Read(3);
        _bits.Skip(1);
        SkipCodedNumber();
        var block = blockCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockCode - 2),
            6 => (int)_bits.Read(8) + 1,
            7 => (int)_bits.Read(16) + 1,
            >= 8 => 256 << (blockCode - 8),
            _ => throw Damaged(),
        };
        if (rateCode == 12)
        {
            _bits.Skip(8);
        }
        else if (rateCode is 13 or 14)
        {
            _bits.Skip(16);
        }
        else if (rateCode == 15)
        {
            throw Damaged();
        }

        _bits.Skip(8);
        var depth = sizeCode switch
        {
            0 or 4 => 16,
            1 => 8,
            2 => 12,
            5 => 20,
            6 => 24,
            _ => throw Damaged(),
        };
        if ((channelCode != 1 && channelCode is < 8 or > 10) || block * 4 > destination.Length)
        {
            throw Damaged();
        }

        if (_first.Length < block)
        {
            _first = new int[block];
            _second = new int[block];
        }

        DecodeSubframe(_first, block, depth + (channelCode == 9 ? 1 : 0));
        DecodeSubframe(_second, block, depth + (channelCode is 8 or 10 ? 1 : 0));
        _bits.AlignToByte();
        _bits.Skip(16);

        for (var index = 0; index < block; index++)
        {
            int left = _first[index];
            int right = _second[index];
            switch (channelCode)
            {
                case 8:
                    right = left - right;
                    break;
                case 9:
                    left += right;
                    break;
                case 10:
                    var mid = (left << 1) | (right & 1);
                    left = (mid + right) >> 1;
                    right = (mid - right) >> 1;
                    break;
            }

            var sample = destination.Slice(index * 4, 4);
            if (bigEndian)
            {
                BinaryPrimitives.WriteInt16BigEndian(sample, (short)left);
                BinaryPrimitives.WriteInt16BigEndian(sample[2..], (short)right);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(sample, (short)left);
                BinaryPrimitives.WriteInt16LittleEndian(sample[2..], (short)right);
            }
        }

        return block * 4;
    }

    private void SkipCodedNumber()
    {
        var first = _bits.Read(8);
        var extra = first switch
        {
            < 0x80 => 0,
            >= 0xC0 and < 0xE0 => 1,
            >= 0xE0 and < 0xF0 => 2,
            >= 0xF0 and < 0xF8 => 3,
            >= 0xF8 and < 0xFC => 4,
            >= 0xFC and < 0xFE => 5,
            0xFE => 6,
            _ => throw Damaged(),
        };
        for (; extra > 0; extra--)
        {
            _bits.Skip(8);
        }
    }

    private void DecodeSubframe(int[] samples, int block, int depth)
    {
        if (_bits.Read(1) != 0)
        {
            throw Damaged();
        }

        var type = (int)_bits.Read(6);
        var wasted = 0;
        if (_bits.Read(1) != 0)
        {
            wasted = (int)_bits.ReadUnary() + 1;
        }

        var bits = depth - wasted;
        if (bits <= 0)
        {
            throw Damaged();
        }

        if (type == 0)
        {
            samples.AsSpan(0, block).Fill(_bits.ReadSigned(bits));
        }
        else if (type == 1)
        {
            for (var index = 0; index < block; index++)
            {
                samples[index] = _bits.ReadSigned(bits);
            }
        }
        else if (type is >= 8 and <= 12)
        {
            DecodeFixed(samples, block, type - 8, bits);
        }
        else if (type >= 32)
        {
            DecodeLpc(samples, block, type - 31, bits);
        }
        else
        {
            throw Damaged();
        }

        if (wasted > 0)
        {
            for (var index = 0; index < block; index++)
            {
                samples[index] <<= wasted;
            }
        }
    }

    private void DecodeFixed(int[] samples, int block, int order, int bits)
    {
        ReadWarmUp(samples, block, order, bits);
        ReadResidual(samples, block, order);
        switch (order)
        {
            case 1:
                for (var i = 1; i < block; i++)
                {
                    samples[i] += samples[i - 1];
                }

                break;
            case 2:
                for (var i = 2; i < block; i++)
                {
                    samples[i] += (2 * samples[i - 1]) - samples[i - 2];
                }

                break;
            case 3:
                for (var i = 3; i < block; i++)
                {
                    samples[i] += (3 * samples[i - 1]) - (3 * samples[i - 2]) + samples[i - 3];
                }

                break;
            case 4:
                for (var i = 4; i < block; i++)
                {
                    samples[i] += (4 * samples[i - 1]) - (6 * samples[i - 2]) + (4 * samples[i - 3]) - samples[i - 4];
                }

                break;
        }
    }

    private void DecodeLpc(int[] samples, int block, int order, int bits)
    {
        ReadWarmUp(samples, block, order, bits);
        var precision = (int)_bits.Read(4) + 1;
        var shift = _bits.ReadSigned(5);
        if (precision == 16 || shift < 0)
        {
            throw Damaged();
        }

        for (var index = 0; index < order; index++)
        {
            _coefficients[index] = _bits.ReadSigned(precision);
        }

        ReadResidual(samples, block, order);
        for (var i = order; i < block; i++)
        {
            long sum = 0;
            for (var j = 0; j < order; j++)
            {
                sum += (long)_coefficients[j] * samples[i - 1 - j];
            }

            samples[i] += (int)(sum >> shift);
        }
    }

    private void ReadWarmUp(int[] samples, int block, int order, int bits)
    {
        if (order > block)
        {
            throw Damaged();
        }

        for (var index = 0; index < order; index++)
        {
            samples[index] = _bits.ReadSigned(bits);
        }
    }

    private void ReadResidual(int[] samples, int block, int order)
    {
        var method = _bits.Read(2);
        if (method > 1)
        {
            throw Damaged();
        }

        var parameterBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 15 : 31;
        var partitionOrder = (int)_bits.Read(4);
        var partitionSize = block >> partitionOrder;
        var position = order;
        for (var partition = 0; partition < 1 << partitionOrder; partition++)
        {
            var count = partition == 0 ? partitionSize - order : partitionSize;
            if (count < 0)
            {
                throw Damaged();
            }

            var parameter = (int)_bits.Read(parameterBits);
            if (parameter == escape)
            {
                var raw = (int)_bits.Read(5);
                for (var index = 0; index < count; index++)
                {
                    samples[position++] = _bits.ReadSigned(raw);
                }

                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var value = (_bits.ReadUnary() << parameter) | _bits.Read(parameter);
                samples[position++] = (int)(value >> 1) ^ -(int)(value & 1);
            }
        }
    }
}
