using System.Runtime.CompilerServices;

namespace Loadout.Images;

/// <summary>
/// An LZMA decoder after the LZMA specification (Igor Pavlov, public domain), for the raw streams
/// chdman writes: lc 3, lp 0, pb 2 and no end marker. A hunk stands alone, so the output is its own
/// dictionary.
/// </summary>
internal sealed class ChdLzmaCodec : IChdCodec
{
    private const int LiteralContextBits = 3;
    private const int PositionMask = 3;
    private const int ProbabilityInitial = 1024;
    private const int States = 12;
    private const int MatchMinimum = 2;
    private const int EndPositionModel = 14;
    private const int FullDistances = 128;

    private const int IsMatch = 0;
    private const int IsRep = IsMatch + (States << 4);
    private const int IsRepG0 = IsRep + States;
    private const int IsRepG1 = IsRepG0 + States;
    private const int IsRepG2 = IsRepG1 + States;
    private const int IsRep0Long = IsRepG2 + States;
    private const int PositionSlot = IsRep0Long + (States << 4);
    private const int PositionDecoders = PositionSlot + (4 << 6);
    private const int Align = PositionDecoders + 1 + FullDistances - EndPositionModel;
    private const int LengthCoder = Align + 16;
    private const int LengthProbabilities = 2 + (16 << 3) + (16 << 3) + 256;
    private const int RepLengthCoder = LengthCoder + LengthProbabilities;
    private const int Literal = RepLengthCoder + LengthProbabilities;
    private const int LiteralProbabilities = 0x300 << LiteralContextBits;

    private readonly ushort[] _probabilities = new ushort[Literal + LiteralProbabilities];
    private byte[] _source = [];
    private int _position;
    private int _end;
    private uint _range;
    private uint _code;

    public void Decode(byte[] source, int offset, int length, Span<byte> destination)
    {
        Array.Fill(_probabilities, (ushort)ProbabilityInitial);
        _source = source;
        _position = offset;
        _end = offset + length;
        _range = 0xFFFFFFFF;
        _code = 0;
        if (NextByte() != 0)
        {
            throw Damaged();
        }

        for (var index = 0; index < 4; index++)
        {
            _code = (_code << 8) | NextByte();
        }

        var state = 0;
        uint rep0 = 0;
        uint rep1 = 0;
        uint rep2 = 0;
        uint rep3 = 0;
        var output = 0;
        while (output < destination.Length)
        {
            var positionState = output & PositionMask;
            if (DecodeBit(IsMatch + (state << 4) + positionState) == 0)
            {
                destination[output] = DecodeLiteral(destination, output, state, rep0);
                output++;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            int matchLength;
            if (DecodeBit(IsRep + state) != 0)
            {
                if (output == 0)
                {
                    throw Damaged();
                }

                if (DecodeBit(IsRepG0 + state) == 0)
                {
                    if (DecodeBit(IsRep0Long + (state << 4) + positionState) == 0)
                    {
                        state = state < 7 ? 9 : 11;
                        if (rep0 >= output)
                        {
                            throw Damaged();
                        }

                        destination[output] = destination[output - (int)rep0 - 1];
                        output++;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (DecodeBit(IsRepG1 + state) == 0)
                    {
                        distance = rep1;
                    }
                    else
                    {
                        if (DecodeBit(IsRepG2 + state) == 0)
                        {
                            distance = rep2;
                        }
                        else
                        {
                            distance = rep3;
                            rep3 = rep2;
                        }

                        rep2 = rep1;
                    }

                    rep1 = rep0;
                    rep0 = distance;
                }

                matchLength = DecodeLength(RepLengthCoder, positionState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                matchLength = DecodeLength(LengthCoder, positionState);
                state = state < 7 ? 7 : 10;
                rep0 = DecodeDistance(matchLength);
            }

            if (rep0 >= output)
            {
                throw Damaged();
            }

            matchLength += MatchMinimum;
            var distanceBack = (int)rep0 + 1;
            var stop = Math.Min(destination.Length, output + matchLength);
            while (output < stop)
            {
                destination[output] = destination[output - distanceBack];
                output++;
            }
        }
    }

    private static ChdException Damaged() => new("This chd is damaged: part of its data does not make sense.");

    private byte DecodeLiteral(Span<byte> destination, int output, int state, uint rep0)
    {
        var previous = output > 0 ? destination[output - 1] : 0;
        var probabilities = Literal + (0x300 * (previous >> (8 - LiteralContextBits)));
        var symbol = 1;
        if (state >= 7)
        {
            if (rep0 >= output)
            {
                throw Damaged();
            }

            int match = destination[output - (int)rep0 - 1];
            do
            {
                var matchBit = (match >> 7) & 1;
                match <<= 1;
                var bit = DecodeBit(probabilities + ((1 + matchBit) << 8) + symbol);
                symbol = (symbol << 1) | bit;
                if (matchBit != bit)
                {
                    break;
                }
            }
            while (symbol < 0x100);
        }

        while (symbol < 0x100)
        {
            symbol = (symbol << 1) | DecodeBit(probabilities + symbol);
        }

        return (byte)symbol;
    }

    private int DecodeLength(int coder, int positionState)
    {
        if (DecodeBit(coder) == 0)
        {
            return DecodeTree(coder + 2 + (positionState << 3), 3);
        }

        if (DecodeBit(coder + 1) == 0)
        {
            return 8 + DecodeTree(coder + 2 + (16 << 3) + (positionState << 3), 3);
        }

        return 16 + DecodeTree(coder + 2 + (16 << 3) + (16 << 3), 8);
    }

    private uint DecodeDistance(int matchLength)
    {
        var lengthState = Math.Min(matchLength, 3);
        var slot = DecodeTree(PositionSlot + (lengthState << 6), 6);
        if (slot < 4)
        {
            return (uint)slot;
        }

        var directBits = (slot >> 1) - 1;
        var distance = (uint)(2 | (slot & 1)) << directBits;
        if (slot < EndPositionModel)
        {
            return distance + DecodeReverseTree(PositionDecoders + (int)distance - slot, directBits);
        }

        distance += DecodeDirectBits(directBits - 4) << 4;
        return distance + DecodeReverseTree(Align, 4);
    }

    private int DecodeTree(int probabilities, int bits)
    {
        var symbol = 1;
        for (var index = 0; index < bits; index++)
        {
            symbol = (symbol << 1) | DecodeBit(probabilities + symbol);
        }

        return symbol - (1 << bits);
    }

    private uint DecodeReverseTree(int probabilities, int bits)
    {
        var symbol = 1;
        uint result = 0;
        for (var index = 0; index < bits; index++)
        {
            var bit = DecodeBit(probabilities + symbol);
            symbol = (symbol << 1) | bit;
            result |= (uint)bit << index;
        }

        return result;
    }

    private uint DecodeDirectBits(int bits)
    {
        uint result = 0;
        for (; bits > 0; bits--)
        {
            _range >>= 1;
            _code -= _range;
            var mask = 0 - (_code >> 31);
            _code += _range & mask;
            result = (result << 1) + mask + 1;
            Normalize();
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeBit(int index)
    {
        ref var probability = ref _probabilities[index];
        var value = (uint)probability;
        var bound = (_range >> 11) * value;
        int bit;
        if (_code < bound)
        {
            _range = bound;
            probability = (ushort)(value + ((2048 - value) >> 5));
            bit = 0;
        }
        else
        {
            _code -= bound;
            _range -= bound;
            probability = (ushort)(value - (value >> 5));
            bit = 1;
        }

        Normalize();
        return bit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        if (_range < 1u << 24)
        {
            _range <<= 8;
            _code = (_code << 8) | NextByte();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte NextByte()
    {
        if (_position >= _end)
        {
            throw Damaged();
        }

        return _source[_position++];
    }
}
