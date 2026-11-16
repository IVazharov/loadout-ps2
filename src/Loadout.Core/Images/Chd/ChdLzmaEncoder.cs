namespace Loadout.Images;

/// <summary>
/// Compresses one CHD hunk with LZMA, as chdman does: lc 3, lp 0, pb 2, 64 fast bytes, a binary tree
/// match finder with 4-byte hashing, the optimal parser, and no end marker. The hunk is its own
/// dictionary, which is how ChdLzmaCodec reads it back. Ported from the C# encoder of Igor Pavlov's
/// LZMA SDK, which is in the public domain (see ThirdParty/LzmaSdk); the structure and names follow it
/// so the two can be read side by side. One instance per thread; it keeps its tables between hunks.
/// </summary>
internal sealed class ChdLzmaEncoder
{
    private const uint RepDistances = 4;
    private const uint States = 12;
    private const int PosSlotBits = 6;
    private const int LenToPosStatesBits = 2;
    private const uint LenToPosStates = 1 << LenToPosStatesBits;
    private const uint MatchMinLen = 2;
    private const int AlignBits = 4;
    private const uint AlignTableSize = 1 << AlignBits;
    private const uint AlignMask = AlignTableSize - 1;
    private const uint StartPosModelIndex = 4;
    private const uint EndPosModelIndex = 14;
    private const uint FullDistances = 1 << ((int)EndPosModelIndex / 2);
    private const int PosStatesBitsMax = 4;
    private const uint PosStatesMax = 1 << PosStatesBitsMax;
    private const int LowLenBits = 3;
    private const int MidLenBits = 3;
    private const int HighLenBits = 8;
    private const uint LowLenSymbols = 1 << LowLenBits;
    private const uint MidLenSymbols = 1 << MidLenBits;
    private const uint LenSymbols = LowLenSymbols + MidLenSymbols + (1 << HighLenBits);
    private const uint MatchMaxLen = MatchMinLen + LenSymbols - 1;

    private const int PosStateBits = 2;
    private const uint PosStateMask = (1 << PosStateBits) - 1;
    private const int LiteralContextBits = 3;
    private const uint FastBytes = 64;
    private const uint InfinityPrice = 0xFFFFFFF;
    private const uint Options = 1 << 12;

    private const int BitModelTotalBits = 11;
    private const uint BitModelTotal = 1 << BitModelTotalBits;
    private const int MoveBits = 5;
    private const int MoveReducingBits = 2;
    private const int BitPriceShiftBits = 6;
    private const uint TopValue = 1 << 24;

    private static readonly byte[] FastPos = BuildFastPos();
    private static readonly uint[] ProbPrices = BuildProbPrices();

    private readonly MatchFinder _matchFinder;
    private readonly Optimal[] _optimum = new Optimal[Options];
    private readonly uint[] _isMatch = new uint[States << PosStatesBitsMax];
    private readonly uint[] _isRep = new uint[States];
    private readonly uint[] _isRepG0 = new uint[States];
    private readonly uint[] _isRepG1 = new uint[States];
    private readonly uint[] _isRepG2 = new uint[States];
    private readonly uint[] _isRep0Long = new uint[States << PosStatesBitsMax];
    private readonly uint[] _posSlotEncoder = new uint[LenToPosStates << PosSlotBits];
    private readonly uint[] _posEncoders = new uint[FullDistances - EndPosModelIndex];
    private readonly uint[] _posAlignEncoder = new uint[1 << AlignBits];
    private readonly LenPriceTableEncoder _lenEncoder = new();
    private readonly LenPriceTableEncoder _repMatchLenEncoder = new();
    private readonly uint[] _literals = new uint[0x300 << LiteralContextBits];
    private readonly uint[] _matchDistances = new uint[(MatchMaxLen * 2) + 2];
    private readonly uint[] _posSlotPrices = new uint[1 << (PosSlotBits + LenToPosStatesBits)];
    private readonly uint[] _distancesPrices = new uint[FullDistances << LenToPosStatesBits];
    private readonly uint[] _alignPrices = new uint[AlignTableSize];
    private readonly uint[] _tempPrices = new uint[FullDistances];
    private readonly uint[] _repDistances = new uint[RepDistances];
    private readonly uint[] _reps = new uint[RepDistances];
    private readonly uint[] _repLens = new uint[RepDistances];

    private uint _state;
    private byte _previousByte;
    private uint _longestMatchLength;
    private uint _numDistancePairs;
    private uint _additionalOffset;
    private uint _optimumEndIndex;
    private uint _optimumCurrentIndex;
    private bool _longestMatchWasFound;
    private uint _alignPriceCount;
    private uint _matchPriceCount;
    private uint _distTableSize;

    private ulong _low;
    private uint _range;
    private uint _cacheSize;
    private byte _cache;
    private byte[] _output = [];
    private int _outputPosition;
    private int _outputLimit;
    private bool _overflow;

    /// <summary>Makes an encoder for hunks of up to maxBytes.</summary>
    public ChdLzmaEncoder(int maxBytes)
    {
        _matchFinder = new MatchFinder((uint)maxBytes, FastBytes);
        var dictionaryLog = 0;
        while (dictionaryLog < 30 && (uint)maxBytes > 1u << dictionaryLog)
        {
            dictionaryLog++;
        }

        _distTableSize = (uint)dictionaryLog * 2;
    }

    /// <summary>
    /// Compresses source into destination and gives the length, or -1 when it would take limit bytes or
    /// more (the caller then keeps a smaller result, or the hunk as it is).
    /// </summary>
    public int Encode(ReadOnlySpan<byte> source, byte[] destination, int limit)
    {
        _output = destination;
        _outputLimit = Math.Min(limit, destination.Length);
        _matchFinder.Init(source);
        Init();
        FillDistancesPrices();
        FillAlignPrices();
        _lenEncoder.SetTableSize(FastBytes + 1 - MatchMinLen);
        _lenEncoder.UpdateTables(1 << PosStateBits);
        _repMatchLenEncoder.SetTableSize(FastBytes + 1 - MatchMinLen);
        _repMatchLenEncoder.UpdateTables(1 << PosStateBits);

        CodeAll();
        return _overflow || _outputPosition >= limit ? -1 : _outputPosition;
    }

    private static byte[] BuildFastPos()
    {
        const byte fastSlots = 22;
        var table = new byte[1 << 11];
        var c = 2;
        table[0] = 0;
        table[1] = 1;
        for (byte slotFast = 2; slotFast < fastSlots; slotFast++)
        {
            var k = 1u << ((slotFast >> 1) - 1);
            for (uint j = 0; j < k; j++, c++)
            {
                table[c] = slotFast;
            }
        }

        return table;
    }

    private static uint[] BuildProbPrices()
    {
        const int bits = BitModelTotalBits - MoveReducingBits;
        var prices = new uint[BitModelTotal >> MoveReducingBits];
        for (var i = bits - 1; i >= 0; i--)
        {
            var start = 1u << (bits - i - 1);
            var end = 1u << (bits - i);
            for (var j = start; j < end; j++)
            {
                prices[j] = ((uint)i << BitPriceShiftBits) + (((end - j) << BitPriceShiftBits) >> (bits - i - 1));
            }
        }

        return prices;
    }

    private static uint GetPosSlot(uint pos)
    {
        if (pos < 1 << 11)
        {
            return FastPos[pos];
        }

        if (pos < 1 << 21)
        {
            return (uint)(FastPos[pos >> 10] + 20);
        }

        return (uint)(FastPos[pos >> 20] + 40);
    }

    private static uint GetPosSlot2(uint pos)
    {
        if (pos < 1 << 17)
        {
            return (uint)(FastPos[pos >> 6] + 12);
        }

        if (pos < 1 << 27)
        {
            return (uint)(FastPos[pos >> 16] + 32);
        }

        return (uint)(FastPos[pos >> 26] + 52);
    }

    private static uint GetLenToPosState(uint len)
    {
        len -= MatchMinLen;
        return len < LenToPosStates ? len : LenToPosStates - 1;
    }

    private static uint StateUpdateChar(uint state) => state < 4 ? 0 : state < 10 ? state - 3 : state - 6;

    private static uint StateUpdateMatch(uint state) => state < 7 ? 7u : 10u;

    private static uint StateUpdateRep(uint state) => state < 7 ? 8u : 11u;

    private static uint StateUpdateShortRep(uint state) => state < 7 ? 9u : 11u;

    private static bool StateIsChar(uint state) => state < 7;

    private static uint GetPrice(uint probability, uint symbol)
        => ProbPrices[(((probability - symbol) ^ (uint)-(int)symbol) & (BitModelTotal - 1)) >> MoveReducingBits];

    private static uint GetPrice0(uint probability) => ProbPrices[probability >> MoveReducingBits];

    private static uint GetPrice1(uint probability) => ProbPrices[(BitModelTotal - probability) >> MoveReducingBits];

    private static void InitProbabilities(Span<uint> probabilities) => probabilities.Fill(BitModelTotal >> 1);

    private static uint BitTreeGetPrice(uint[] models, int start, int levels, uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (var bitIndex = levels; bitIndex > 0;)
        {
            bitIndex--;
            var bit = (symbol >> bitIndex) & 1;
            price += GetPrice(models[start + (int)m], bit);
            m = (m << 1) + bit;
        }

        return price;
    }

    private static uint BitTreeReverseGetPrice(uint[] models, int start, int levels, uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (var i = levels; i > 0; i--)
        {
            var bit = symbol & 1;
            symbol >>= 1;
            price += GetPrice(models[start + (int)m], bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    private void Init()
    {
        _state = 0;
        _previousByte = 0;
        Array.Clear(_repDistances);

        _low = 0;
        _range = 0xFFFFFFFF;
        _cacheSize = 1;
        _cache = 0;
        _outputPosition = 0;
        _overflow = false;

        InitProbabilities(_isMatch);
        InitProbabilities(_isRep0Long);
        InitProbabilities(_isRep);
        InitProbabilities(_isRepG0);
        InitProbabilities(_isRepG1);
        InitProbabilities(_isRepG2);
        InitProbabilities(_literals);
        InitProbabilities(_posSlotEncoder);
        InitProbabilities(_posEncoders);
        InitProbabilities(_posAlignEncoder);
        _lenEncoder.Init();
        _repMatchLenEncoder.Init();
        _longestMatchWasFound = false;
        _optimumEndIndex = 0;
        _optimumCurrentIndex = 0;
        _additionalOffset = 0;
    }

    // The range coder.
    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (uint)(_low >> 32) == 1)
        {
            var temp = _cache;
            do
            {
                WriteByte((byte)(temp + (_low >> 32)));
                temp = 0xFF;
            }
            while (--_cacheSize != 0);

            _cache = (byte)((uint)_low >> 24);
        }

        _cacheSize++;
        _low = (uint)_low << 8;
    }

    private void WriteByte(byte value)
    {
        if (_outputPosition < _outputLimit)
        {
            _output[_outputPosition++] = value;
        }
        else
        {
            _overflow = true;
        }
    }

    private void FlushData()
    {
        for (var i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }

    private void EncodeDirectBits(uint value, int bits)
    {
        for (var i = bits - 1; i >= 0; i--)
        {
            _range >>= 1;
            if (((value >> i) & 1) == 1)
            {
                _low += _range;
            }

            if (_range < TopValue)
            {
                _range <<= 8;
                ShiftLow();
            }
        }
    }

    private void EncodeBit(uint[] probabilities, int index, uint symbol)
    {
        var probability = probabilities[index];
        var newBound = (_range >> BitModelTotalBits) * probability;
        if (symbol == 0)
        {
            _range = newBound;
            probabilities[index] = probability + ((BitModelTotal - probability) >> MoveBits);
        }
        else
        {
            _low += newBound;
            _range -= newBound;
            probabilities[index] = probability - (probability >> MoveBits);
        }

        if (_range < TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    private void BitTreeEncode(uint[] models, int start, int levels, uint symbol)
    {
        uint m = 1;
        for (var bitIndex = levels; bitIndex > 0;)
        {
            bitIndex--;
            var bit = (symbol >> bitIndex) & 1;
            EncodeBit(models, start + (int)m, bit);
            m = (m << 1) | bit;
        }
    }

    private void BitTreeReverseEncode(uint[] models, int start, int levels, uint symbol)
    {
        uint m = 1;
        for (var i = 0; i < levels; i++)
        {
            var bit = symbol & 1;
            EncodeBit(models, start + (int)m, bit);
            m = (m << 1) | bit;
            symbol >>= 1;
        }
    }

    // Literals: lc 3, lp 0, so the coder is chosen by the top three bits of the byte before.
    private static int LiteralCoder(byte previousByte) => (previousByte >> (8 - LiteralContextBits)) * 0x300;

    private void LiteralEncode(int coder, byte symbol)
    {
        uint context = 1;
        for (var i = 7; i >= 0; i--)
        {
            var bit = (uint)((symbol >> i) & 1);
            EncodeBit(_literals, coder + (int)context, bit);
            context = (context << 1) | bit;
        }
    }

    private void LiteralEncodeMatched(int coder, byte matchByte, byte symbol)
    {
        uint context = 1;
        var same = true;
        for (var i = 7; i >= 0; i--)
        {
            var bit = (uint)((symbol >> i) & 1);
            var state = context;
            if (same)
            {
                var matchBit = (uint)((matchByte >> i) & 1);
                state += (1 + matchBit) << 8;
                same = matchBit == bit;
            }

            EncodeBit(_literals, coder + (int)state, bit);
            context = (context << 1) | bit;
        }
    }

    private uint LiteralGetPrice(int coder, bool matchMode, byte matchByte, byte symbol)
    {
        uint price = 0;
        uint context = 1;
        var i = 7;
        if (matchMode)
        {
            for (; i >= 0; i--)
            {
                var matchBit = (uint)(matchByte >> i) & 1;
                var bit = (uint)(symbol >> i) & 1;
                price += GetPrice(_literals[coder + (int)(((1 + matchBit) << 8) + context)], bit);
                context = (context << 1) | bit;
                if (matchBit != bit)
                {
                    i--;
                    break;
                }
            }
        }

        for (; i >= 0; i--)
        {
            var bit = (uint)(symbol >> i) & 1;
            price += GetPrice(_literals[coder + (int)context], bit);
            context = (context << 1) | bit;
        }

        return price;
    }

    private void LenEncode(LenPriceTableEncoder encoder, uint symbol, uint posState)
    {
        if (symbol < LowLenSymbols)
        {
            EncodeBit(encoder.Choice, 0, 0);
            BitTreeEncode(encoder.Low, (int)(posState << LowLenBits), LowLenBits, symbol);
        }
        else
        {
            symbol -= LowLenSymbols;
            EncodeBit(encoder.Choice, 0, 1);
            if (symbol < MidLenSymbols)
            {
                EncodeBit(encoder.Choice, 1, 0);
                BitTreeEncode(encoder.Mid, (int)(posState << MidLenBits), MidLenBits, symbol);
            }
            else
            {
                EncodeBit(encoder.Choice, 1, 1);
                BitTreeEncode(encoder.High, 0, HighLenBits, symbol - MidLenSymbols);
            }
        }

        encoder.Encoded(posState);
    }

    private void ReadMatchDistances(out uint lenRes, out uint numDistancePairs)
    {
        lenRes = 0;
        numDistancePairs = _matchFinder.GetMatches(_matchDistances);
        if (numDistancePairs > 0)
        {
            lenRes = _matchDistances[numDistancePairs - 2];
            if (lenRes == FastBytes)
            {
                lenRes += _matchFinder.GetMatchLen((int)lenRes - 1, _matchDistances[numDistancePairs - 1], MatchMaxLen - lenRes);
            }
        }

        _additionalOffset++;
    }

    private void MovePos(uint num)
    {
        if (num > 0)
        {
            _matchFinder.Skip(num);
            _additionalOffset += num;
        }
    }

    private uint GetRepLen1Price(uint state, uint posState)
        => GetPrice0(_isRepG0[state]) + GetPrice0(_isRep0Long[(state << PosStatesBitsMax) + posState]);

    private uint GetPureRepPrice(uint repIndex, uint state, uint posState)
    {
        uint price;
        if (repIndex == 0)
        {
            price = GetPrice0(_isRepG0[state]);
            price += GetPrice1(_isRep0Long[(state << PosStatesBitsMax) + posState]);
        }
        else
        {
            price = GetPrice1(_isRepG0[state]);
            if (repIndex == 1)
            {
                price += GetPrice0(_isRepG1[state]);
            }
            else
            {
                price += GetPrice1(_isRepG1[state]);
                price += GetPrice(_isRepG2[state], repIndex - 2);
            }
        }

        return price;
    }

    private uint GetRepPrice(uint repIndex, uint len, uint state, uint posState)
        => _repMatchLenEncoder.GetPrice(len - MatchMinLen, posState) + GetPureRepPrice(repIndex, state, posState);

    private uint GetPosLenPrice(uint pos, uint len, uint posState)
    {
        uint price;
        var lenToPosState = GetLenToPosState(len);
        if (pos < FullDistances)
        {
            price = _distancesPrices[(lenToPosState * FullDistances) + pos];
        }
        else
        {
            price = _posSlotPrices[(lenToPosState << PosSlotBits) + GetPosSlot2(pos)] + _alignPrices[pos & AlignMask];
        }

        return price + _lenEncoder.GetPrice(len - MatchMinLen, posState);
    }

    private uint Backward(out uint backRes, uint cur)
    {
        _optimumEndIndex = cur;
        var posMem = _optimum[cur].PosPrev;
        var backMem = _optimum[cur].BackPrev;
        do
        {
            if (_optimum[cur].Prev1IsChar)
            {
                _optimum[posMem].MakeAsChar();
                _optimum[posMem].PosPrev = posMem - 1;
                if (_optimum[cur].Prev2)
                {
                    _optimum[posMem - 1].Prev1IsChar = false;
                    _optimum[posMem - 1].PosPrev = _optimum[cur].PosPrev2;
                    _optimum[posMem - 1].BackPrev = _optimum[cur].BackPrev2;
                }
            }

            var posPrev = posMem;
            var backCur = backMem;
            backMem = _optimum[posPrev].BackPrev;
            posMem = _optimum[posPrev].PosPrev;
            _optimum[posPrev].BackPrev = backCur;
            _optimum[posPrev].PosPrev = cur;
            cur = posPrev;
        }
        while (cur > 0);

        backRes = _optimum[0].BackPrev;
        _optimumCurrentIndex = _optimum[0].PosPrev;
        return _optimumCurrentIndex;
    }

    private uint GetOptimum(uint position, out uint backRes)
    {
        if (_optimumEndIndex != _optimumCurrentIndex)
        {
            var lenRes = _optimum[_optimumCurrentIndex].PosPrev - _optimumCurrentIndex;
            backRes = _optimum[_optimumCurrentIndex].BackPrev;
            _optimumCurrentIndex = _optimum[_optimumCurrentIndex].PosPrev;
            return lenRes;
        }

        _optimumCurrentIndex = _optimumEndIndex = 0;

        uint lenMain, numDistancePairs;
        if (!_longestMatchWasFound)
        {
            ReadMatchDistances(out lenMain, out numDistancePairs);
        }
        else
        {
            lenMain = _longestMatchLength;
            numDistancePairs = _numDistancePairs;
            _longestMatchWasFound = false;
        }

        var numAvailableBytes = _matchFinder.GetNumAvailableBytes() + 1;
        if (numAvailableBytes < 2)
        {
            backRes = 0xFFFFFFFF;
            return 1;
        }

        if (numAvailableBytes > MatchMaxLen)
        {
            numAvailableBytes = MatchMaxLen;
        }

        uint repMaxIndex = 0;
        uint i;
        for (i = 0; i < RepDistances; i++)
        {
            _reps[i] = _repDistances[i];
            _repLens[i] = _matchFinder.GetMatchLen(-1, _reps[i], MatchMaxLen);
            if (_repLens[i] > _repLens[repMaxIndex])
            {
                repMaxIndex = i;
            }
        }

        if (_repLens[repMaxIndex] >= FastBytes)
        {
            backRes = repMaxIndex;
            var lenRes = _repLens[repMaxIndex];
            MovePos(lenRes - 1);
            return lenRes;
        }

        if (lenMain >= FastBytes)
        {
            backRes = _matchDistances[numDistancePairs - 1] + RepDistances;
            MovePos(lenMain - 1);
            return lenMain;
        }

        var currentByte = _matchFinder.GetIndexByte(-1);
        var matchByte = _matchFinder.GetIndexByte((int)(0 - _repDistances[0] - 1 - 1));

        if (lenMain < 2 && currentByte != matchByte && _repLens[repMaxIndex] < 2)
        {
            backRes = 0xFFFFFFFF;
            return 1;
        }

        _optimum[0].State = _state;

        var posState = position & PosStateMask;

        _optimum[1].Price = GetPrice0(_isMatch[(_state << PosStatesBitsMax) + posState])
            + LiteralGetPrice(LiteralCoder(_previousByte), !StateIsChar(_state), matchByte, currentByte);
        _optimum[1].MakeAsChar();

        var matchPrice = GetPrice1(_isMatch[(_state << PosStatesBitsMax) + posState]);
        var repMatchPrice = matchPrice + GetPrice1(_isRep[_state]);

        if (matchByte == currentByte)
        {
            var shortRepPrice = repMatchPrice + GetRepLen1Price(_state, posState);
            if (shortRepPrice < _optimum[1].Price)
            {
                _optimum[1].Price = shortRepPrice;
                _optimum[1].MakeAsShortRep();
            }
        }

        var lenEnd = lenMain >= _repLens[repMaxIndex] ? lenMain : _repLens[repMaxIndex];

        if (lenEnd < 2)
        {
            backRes = _optimum[1].BackPrev;
            return 1;
        }

        _optimum[1].PosPrev = 0;

        _optimum[0].Backs0 = _reps[0];
        _optimum[0].Backs1 = _reps[1];
        _optimum[0].Backs2 = _reps[2];
        _optimum[0].Backs3 = _reps[3];

        var len = lenEnd;
        do
        {
            _optimum[len--].Price = InfinityPrice;
        }
        while (len >= 2);

        for (i = 0; i < RepDistances; i++)
        {
            var repLen = _repLens[i];
            if (repLen < 2)
            {
                continue;
            }

            var price = repMatchPrice + GetPureRepPrice(i, _state, posState);
            do
            {
                var curAndLenPrice = price + _repMatchLenEncoder.GetPrice(repLen - 2, posState);
                ref var optimum = ref _optimum[repLen];
                if (curAndLenPrice < optimum.Price)
                {
                    optimum.Price = curAndLenPrice;
                    optimum.PosPrev = 0;
                    optimum.BackPrev = i;
                    optimum.Prev1IsChar = false;
                }
            }
            while (--repLen >= 2);
        }

        var normalMatchPrice = matchPrice + GetPrice0(_isRep[_state]);

        len = _repLens[0] >= 2 ? _repLens[0] + 1 : 2;
        if (len <= lenMain)
        {
            uint offs = 0;
            while (len > _matchDistances[offs])
            {
                offs += 2;
            }

            for (; ; len++)
            {
                var distance = _matchDistances[offs + 1];
                var curAndLenPrice = normalMatchPrice + GetPosLenPrice(distance, len, posState);
                ref var optimum = ref _optimum[len];
                if (curAndLenPrice < optimum.Price)
                {
                    optimum.Price = curAndLenPrice;
                    optimum.PosPrev = 0;
                    optimum.BackPrev = distance + RepDistances;
                    optimum.Prev1IsChar = false;
                }

                if (len == _matchDistances[offs])
                {
                    offs += 2;
                    if (offs == numDistancePairs)
                    {
                        break;
                    }
                }
            }
        }

        uint cur = 0;

        while (true)
        {
            cur++;
            if (cur == lenEnd)
            {
                return Backward(out backRes, cur);
            }

            ReadMatchDistances(out var newLen, out numDistancePairs);
            if (newLen >= FastBytes)
            {
                _numDistancePairs = numDistancePairs;
                _longestMatchLength = newLen;
                _longestMatchWasFound = true;
                return Backward(out backRes, cur);
            }

            position++;
            var posPrev = _optimum[cur].PosPrev;
            uint state;
            if (_optimum[cur].Prev1IsChar)
            {
                posPrev--;
                if (_optimum[cur].Prev2)
                {
                    state = _optimum[_optimum[cur].PosPrev2].State;
                    state = _optimum[cur].BackPrev2 < RepDistances ? StateUpdateRep(state) : StateUpdateMatch(state);
                }
                else
                {
                    state = _optimum[posPrev].State;
                }

                state = StateUpdateChar(state);
            }
            else
            {
                state = _optimum[posPrev].State;
            }

            if (posPrev == cur - 1)
            {
                state = _optimum[cur].IsShortRep() ? StateUpdateShortRep(state) : StateUpdateChar(state);
            }
            else
            {
                uint pos;
                if (_optimum[cur].Prev1IsChar && _optimum[cur].Prev2)
                {
                    posPrev = _optimum[cur].PosPrev2;
                    pos = _optimum[cur].BackPrev2;
                    state = StateUpdateRep(state);
                }
                else
                {
                    pos = _optimum[cur].BackPrev;
                    state = pos < RepDistances ? StateUpdateRep(state) : StateUpdateMatch(state);
                }

                ref var opt = ref _optimum[posPrev];
                if (pos < RepDistances)
                {
                    if (pos == 0)
                    {
                        _reps[0] = opt.Backs0;
                        _reps[1] = opt.Backs1;
                        _reps[2] = opt.Backs2;
                        _reps[3] = opt.Backs3;
                    }
                    else if (pos == 1)
                    {
                        _reps[0] = opt.Backs1;
                        _reps[1] = opt.Backs0;
                        _reps[2] = opt.Backs2;
                        _reps[3] = opt.Backs3;
                    }
                    else if (pos == 2)
                    {
                        _reps[0] = opt.Backs2;
                        _reps[1] = opt.Backs0;
                        _reps[2] = opt.Backs1;
                        _reps[3] = opt.Backs3;
                    }
                    else
                    {
                        _reps[0] = opt.Backs3;
                        _reps[1] = opt.Backs0;
                        _reps[2] = opt.Backs1;
                        _reps[3] = opt.Backs2;
                    }
                }
                else
                {
                    _reps[0] = pos - RepDistances;
                    _reps[1] = opt.Backs0;
                    _reps[2] = opt.Backs1;
                    _reps[3] = opt.Backs2;
                }
            }

            _optimum[cur].State = state;
            _optimum[cur].Backs0 = _reps[0];
            _optimum[cur].Backs1 = _reps[1];
            _optimum[cur].Backs2 = _reps[2];
            _optimum[cur].Backs3 = _reps[3];
            var curPrice = _optimum[cur].Price;

            currentByte = _matchFinder.GetIndexByte(-1);
            matchByte = _matchFinder.GetIndexByte((int)(0 - _reps[0] - 1 - 1));

            posState = position & PosStateMask;

            var curAnd1Price = curPrice
                + GetPrice0(_isMatch[(state << PosStatesBitsMax) + posState])
                + LiteralGetPrice(LiteralCoder(_matchFinder.GetIndexByte(-2)), !StateIsChar(state), matchByte, currentByte);

            ref var nextOptimum = ref _optimum[cur + 1];

            var nextIsChar = false;
            if (curAnd1Price < nextOptimum.Price)
            {
                nextOptimum.Price = curAnd1Price;
                nextOptimum.PosPrev = cur;
                nextOptimum.MakeAsChar();
                nextIsChar = true;
            }

            matchPrice = curPrice + GetPrice1(_isMatch[(state << PosStatesBitsMax) + posState]);
            repMatchPrice = matchPrice + GetPrice1(_isRep[state]);

            if (matchByte == currentByte && !(nextOptimum.PosPrev < cur && nextOptimum.BackPrev == 0))
            {
                var shortRepPrice = repMatchPrice + GetRepLen1Price(state, posState);
                if (shortRepPrice <= nextOptimum.Price)
                {
                    nextOptimum.Price = shortRepPrice;
                    nextOptimum.PosPrev = cur;
                    nextOptimum.MakeAsShortRep();
                    nextIsChar = true;
                }
            }

            var numAvailableBytesFull = _matchFinder.GetNumAvailableBytes() + 1;
            numAvailableBytesFull = Math.Min(Options - 1 - cur, numAvailableBytesFull);
            numAvailableBytes = numAvailableBytesFull;

            if (numAvailableBytes < 2)
            {
                continue;
            }

            if (numAvailableBytes > FastBytes)
            {
                numAvailableBytes = FastBytes;
            }

            if (!nextIsChar && matchByte != currentByte)
            {
                // Try a literal and then rep0.
                var t = Math.Min(numAvailableBytesFull - 1, FastBytes);
                var lenTest2 = _matchFinder.GetMatchLen(0, _reps[0], t);
                if (lenTest2 >= 2)
                {
                    var state2 = StateUpdateChar(state);

                    var posStateNext = (position + 1) & PosStateMask;
                    var nextRepMatchPrice = curAnd1Price
                        + GetPrice1(_isMatch[(state2 << PosStatesBitsMax) + posStateNext])
                        + GetPrice1(_isRep[state2]);
                    var offset = cur + 1 + lenTest2;
                    while (lenEnd < offset)
                    {
                        _optimum[++lenEnd].Price = InfinityPrice;
                    }

                    var curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                    ref var optimum = ref _optimum[offset];
                    if (curAndLenPrice < optimum.Price)
                    {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = cur + 1;
                        optimum.BackPrev = 0;
                        optimum.Prev1IsChar = true;
                        optimum.Prev2 = false;
                    }
                }
            }

            uint startLen = 2;

            for (uint repIndex = 0; repIndex < RepDistances; repIndex++)
            {
                var lenTest = _matchFinder.GetMatchLen(-1, _reps[repIndex], numAvailableBytes);
                if (lenTest < 2)
                {
                    continue;
                }

                var lenTestTemp = lenTest;
                do
                {
                    while (lenEnd < cur + lenTest)
                    {
                        _optimum[++lenEnd].Price = InfinityPrice;
                    }

                    var curAndLenPrice = repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState);
                    ref var optimum = ref _optimum[cur + lenTest];
                    if (curAndLenPrice < optimum.Price)
                    {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = cur;
                        optimum.BackPrev = repIndex;
                        optimum.Prev1IsChar = false;
                    }
                }
                while (--lenTest >= 2);

                lenTest = lenTestTemp;

                if (repIndex == 0)
                {
                    startLen = lenTest + 1;
                }

                if (lenTest < numAvailableBytesFull)
                {
                    // A rep match, a literal, and rep0.
                    var t = Math.Min(numAvailableBytesFull - 1 - lenTest, FastBytes);
                    var lenTest2 = _matchFinder.GetMatchLen((int)lenTest, _reps[repIndex], t);
                    if (lenTest2 >= 2)
                    {
                        var state2 = StateUpdateRep(state);

                        var posStateNext = (position + lenTest) & PosStateMask;
                        var curAndLenCharPrice = repMatchPrice
                            + GetRepPrice(repIndex, lenTest, state, posState)
                            + GetPrice0(_isMatch[(state2 << PosStatesBitsMax) + posStateNext])
                            + LiteralGetPrice(
                                LiteralCoder(_matchFinder.GetIndexByte((int)lenTest - 1 - 1)),
                                true,
                                _matchFinder.GetIndexByte((int)lenTest - 1 - (int)(_reps[repIndex] + 1)),
                                _matchFinder.GetIndexByte((int)lenTest - 1));
                        state2 = StateUpdateChar(state2);
                        posStateNext = (position + lenTest + 1) & PosStateMask;
                        var nextMatchPrice = curAndLenCharPrice + GetPrice1(_isMatch[(state2 << PosStatesBitsMax) + posStateNext]);
                        var nextRepMatchPrice = nextMatchPrice + GetPrice1(_isRep[state2]);

                        var offset = lenTest + 1 + lenTest2;
                        while (lenEnd < cur + offset)
                        {
                            _optimum[++lenEnd].Price = InfinityPrice;
                        }

                        var curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                        ref var optimum = ref _optimum[cur + offset];
                        if (curAndLenPrice < optimum.Price)
                        {
                            optimum.Price = curAndLenPrice;
                            optimum.PosPrev = cur + lenTest + 1;
                            optimum.BackPrev = 0;
                            optimum.Prev1IsChar = true;
                            optimum.Prev2 = true;
                            optimum.PosPrev2 = cur;
                            optimum.BackPrev2 = repIndex;
                        }
                    }
                }
            }

            if (newLen > numAvailableBytes)
            {
                newLen = numAvailableBytes;
                for (numDistancePairs = 0; newLen > _matchDistances[numDistancePairs]; numDistancePairs += 2)
                {
                }

                _matchDistances[numDistancePairs] = newLen;
                numDistancePairs += 2;
            }

            if (newLen >= startLen)
            {
                normalMatchPrice = matchPrice + GetPrice0(_isRep[state]);
                while (lenEnd < cur + newLen)
                {
                    _optimum[++lenEnd].Price = InfinityPrice;
                }

                uint offs = 0;
                while (startLen > _matchDistances[offs])
                {
                    offs += 2;
                }

                for (var lenTest = startLen; ; lenTest++)
                {
                    var curBack = _matchDistances[offs + 1];
                    var curAndLenPrice = normalMatchPrice + GetPosLenPrice(curBack, lenTest, posState);
                    ref var optimum = ref _optimum[cur + lenTest];
                    if (curAndLenPrice < optimum.Price)
                    {
                        optimum.Price = curAndLenPrice;
                        optimum.PosPrev = cur;
                        optimum.BackPrev = curBack + RepDistances;
                        optimum.Prev1IsChar = false;
                    }

                    if (lenTest == _matchDistances[offs])
                    {
                        if (lenTest < numAvailableBytesFull)
                        {
                            // A match, a literal, and rep0.
                            var t = Math.Min(numAvailableBytesFull - 1 - lenTest, FastBytes);
                            var lenTest2 = _matchFinder.GetMatchLen((int)lenTest, curBack, t);
                            if (lenTest2 >= 2)
                            {
                                var state2 = StateUpdateMatch(state);

                                var posStateNext = (position + lenTest) & PosStateMask;
                                var curAndLenCharPrice = curAndLenPrice
                                    + GetPrice0(_isMatch[(state2 << PosStatesBitsMax) + posStateNext])
                                    + LiteralGetPrice(
                                        LiteralCoder(_matchFinder.GetIndexByte((int)lenTest - 1 - 1)),
                                        true,
                                        _matchFinder.GetIndexByte((int)lenTest - (int)(curBack + 1) - 1),
                                        _matchFinder.GetIndexByte((int)lenTest - 1));
                                state2 = StateUpdateChar(state2);
                                posStateNext = (position + lenTest + 1) & PosStateMask;
                                var nextMatchPrice = curAndLenCharPrice + GetPrice1(_isMatch[(state2 << PosStatesBitsMax) + posStateNext]);
                                var nextRepMatchPrice = nextMatchPrice + GetPrice1(_isRep[state2]);

                                var offset = lenTest + 1 + lenTest2;
                                while (lenEnd < cur + offset)
                                {
                                    _optimum[++lenEnd].Price = InfinityPrice;
                                }

                                curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
                                ref var optimum2 = ref _optimum[cur + offset];
                                if (curAndLenPrice < optimum2.Price)
                                {
                                    optimum2.Price = curAndLenPrice;
                                    optimum2.PosPrev = cur + lenTest + 1;
                                    optimum2.BackPrev = 0;
                                    optimum2.Prev1IsChar = true;
                                    optimum2.Prev2 = true;
                                    optimum2.PosPrev2 = cur;
                                    optimum2.BackPrev2 = curBack + RepDistances;
                                }
                            }
                        }

                        offs += 2;
                        if (offs == numDistancePairs)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    private void CodeAll()
    {
        long nowPos = 0;
        if (_matchFinder.GetNumAvailableBytes() == 0)
        {
            FlushData();
            return;
        }

        // The first byte is always a literal.
        ReadMatchDistances(out _, out _);
        EncodeBit(_isMatch, (int)((_state << PosStatesBitsMax) + ((uint)nowPos & PosStateMask)), 0);
        _state = StateUpdateChar(_state);
        var firstByte = _matchFinder.GetIndexByte((int)(0 - _additionalOffset));
        LiteralEncode(LiteralCoder(_previousByte), firstByte);
        _previousByte = firstByte;
        _additionalOffset--;
        nowPos++;

        if (_matchFinder.GetNumAvailableBytes() == 0)
        {
            FlushData();
            return;
        }

        while (!_overflow)
        {
            var len = GetOptimum((uint)nowPos, out var pos);

            var posState = (uint)nowPos & PosStateMask;
            var complexState = (int)((_state << PosStatesBitsMax) + posState);
            if (len == 1 && pos == 0xFFFFFFFF)
            {
                EncodeBit(_isMatch, complexState, 0);
                var curByte = _matchFinder.GetIndexByte((int)(0 - _additionalOffset));
                var coder = LiteralCoder(_previousByte);
                if (!StateIsChar(_state))
                {
                    var matchByte = _matchFinder.GetIndexByte((int)(0 - _repDistances[0] - 1 - _additionalOffset));
                    LiteralEncodeMatched(coder, matchByte, curByte);
                }
                else
                {
                    LiteralEncode(coder, curByte);
                }

                _previousByte = curByte;
                _state = StateUpdateChar(_state);
            }
            else
            {
                EncodeBit(_isMatch, complexState, 1);
                if (pos < RepDistances)
                {
                    EncodeBit(_isRep, (int)_state, 1);
                    if (pos == 0)
                    {
                        EncodeBit(_isRepG0, (int)_state, 0);
                        EncodeBit(_isRep0Long, complexState, len == 1 ? 0u : 1u);
                    }
                    else
                    {
                        EncodeBit(_isRepG0, (int)_state, 1);
                        if (pos == 1)
                        {
                            EncodeBit(_isRepG1, (int)_state, 0);
                        }
                        else
                        {
                            EncodeBit(_isRepG1, (int)_state, 1);
                            EncodeBit(_isRepG2, (int)_state, pos - 2);
                        }
                    }

                    if (len == 1)
                    {
                        _state = StateUpdateShortRep(_state);
                    }
                    else
                    {
                        LenEncode(_repMatchLenEncoder, len - MatchMinLen, posState);
                        _state = StateUpdateRep(_state);
                    }

                    var distance = _repDistances[pos];
                    if (pos != 0)
                    {
                        for (var i = pos; i >= 1; i--)
                        {
                            _repDistances[i] = _repDistances[i - 1];
                        }

                        _repDistances[0] = distance;
                    }
                }
                else
                {
                    EncodeBit(_isRep, (int)_state, 0);
                    _state = StateUpdateMatch(_state);
                    LenEncode(_lenEncoder, len - MatchMinLen, posState);
                    pos -= RepDistances;
                    var posSlot = GetPosSlot(pos);
                    var lenToPosState = GetLenToPosState(len);
                    BitTreeEncode(_posSlotEncoder, (int)(lenToPosState << PosSlotBits), PosSlotBits, posSlot);

                    if (posSlot >= StartPosModelIndex)
                    {
                        var footerBits = (int)((posSlot >> 1) - 1);
                        var baseVal = (2 | (posSlot & 1)) << footerBits;
                        var posReduced = pos - baseVal;

                        if (posSlot < EndPosModelIndex)
                        {
                            BitTreeReverseEncode(_posEncoders, (int)(baseVal - posSlot - 1), footerBits, posReduced);
                        }
                        else
                        {
                            EncodeDirectBits(posReduced >> AlignBits, footerBits - AlignBits);
                            BitTreeReverseEncode(_posAlignEncoder, 0, AlignBits, posReduced & AlignMask);
                            _alignPriceCount++;
                        }
                    }

                    var distance = pos;
                    for (var i = RepDistances - 1; i >= 1; i--)
                    {
                        _repDistances[i] = _repDistances[i - 1];
                    }

                    _repDistances[0] = distance;
                    _matchPriceCount++;
                }

                _previousByte = _matchFinder.GetIndexByte((int)(len - 1 - _additionalOffset));
            }

            _additionalOffset -= len;
            nowPos += len;
            if (_additionalOffset == 0)
            {
                if (_matchPriceCount >= 1 << 7)
                {
                    FillDistancesPrices();
                }

                if (_alignPriceCount >= AlignTableSize)
                {
                    FillAlignPrices();
                }

                if (_matchFinder.GetNumAvailableBytes() == 0)
                {
                    FlushData();
                    return;
                }
            }
        }
    }

    private void FillDistancesPrices()
    {
        for (var i = StartPosModelIndex; i < FullDistances; i++)
        {
            var posSlot = GetPosSlot(i);
            var footerBits = (int)((posSlot >> 1) - 1);
            var baseVal = (2 | (posSlot & 1)) << footerBits;
            _tempPrices[i] = BitTreeReverseGetPrice(_posEncoders, (int)(baseVal - posSlot - 1), footerBits, i - baseVal);
        }

        for (uint lenToPosState = 0; lenToPosState < LenToPosStates; lenToPosState++)
        {
            uint posSlot;
            var start = (int)(lenToPosState << PosSlotBits);
            var st = lenToPosState << PosSlotBits;
            for (posSlot = 0; posSlot < _distTableSize; posSlot++)
            {
                _posSlotPrices[st + posSlot] = BitTreeGetPrice(_posSlotEncoder, start, PosSlotBits, posSlot);
            }

            for (posSlot = EndPosModelIndex; posSlot < _distTableSize; posSlot++)
            {
                _posSlotPrices[st + posSlot] += ((posSlot >> 1) - 1 - AlignBits) << BitPriceShiftBits;
            }

            var st2 = lenToPosState * FullDistances;
            uint i;
            for (i = 0; i < StartPosModelIndex; i++)
            {
                _distancesPrices[st2 + i] = _posSlotPrices[st + i];
            }

            for (; i < FullDistances; i++)
            {
                _distancesPrices[st2 + i] = _posSlotPrices[st + GetPosSlot(i)] + _tempPrices[i];
            }
        }

        _matchPriceCount = 0;
    }

    private void FillAlignPrices()
    {
        for (uint i = 0; i < AlignTableSize; i++)
        {
            _alignPrices[i] = BitTreeReverseGetPrice(_posAlignEncoder, 0, AlignBits, i);
        }

        _alignPriceCount = 0;
    }

    private struct Optimal
    {
        public uint State;
        public bool Prev1IsChar;
        public bool Prev2;
        public uint PosPrev2;
        public uint BackPrev2;
        public uint Price;
        public uint PosPrev;
        public uint BackPrev;
        public uint Backs0;
        public uint Backs1;
        public uint Backs2;
        public uint Backs3;

        public void MakeAsChar()
        {
            BackPrev = 0xFFFFFFFF;
            Prev1IsChar = false;
        }

        public void MakeAsShortRep()
        {
            BackPrev = 0;
            Prev1IsChar = false;
        }

        public readonly bool IsShortRep() => BackPrev == 0;
    }

    /// <summary>A length coder with its price table, for matches or for rep matches.</summary>
    private sealed class LenPriceTableEncoder
    {
        private readonly uint[] _prices = new uint[LenSymbols << PosStatesBitsMax];
        private readonly uint[] _counters = new uint[PosStatesMax];
        private uint _tableSize;

        public uint[] Choice { get; } = new uint[2];

        public uint[] Low { get; } = new uint[PosStatesMax << LowLenBits];

        public uint[] Mid { get; } = new uint[PosStatesMax << MidLenBits];

        public uint[] High { get; } = new uint[1 << HighLenBits];

        public void Init()
        {
            InitProbabilities(Choice);
            InitProbabilities(Low);
            InitProbabilities(Mid);
            InitProbabilities(High);
        }

        public void SetTableSize(uint tableSize) => _tableSize = tableSize;

        public uint GetPrice(uint symbol, uint posState) => _prices[(posState * LenSymbols) + symbol];

        public void UpdateTables(uint posStates)
        {
            for (uint posState = 0; posState < posStates; posState++)
            {
                UpdateTable(posState);
            }
        }

        public void Encoded(uint posState)
        {
            if (--_counters[posState] == 0)
            {
                UpdateTable(posState);
            }
        }

        private void UpdateTable(uint posState)
        {
            SetPrices(posState, _tableSize, posState * LenSymbols);
            _counters[posState] = _tableSize;
        }

        private void SetPrices(uint posState, uint numSymbols, uint st)
        {
            var a0 = GetPrice0(Choice[0]);
            var a1 = GetPrice1(Choice[0]);
            var b0 = a1 + GetPrice0(Choice[1]);
            var b1 = a1 + GetPrice1(Choice[1]);
            uint i;
            for (i = 0; i < LowLenSymbols; i++)
            {
                if (i >= numSymbols)
                {
                    return;
                }

                _prices[st + i] = a0 + BitTreeGetPrice(Low, (int)(posState << LowLenBits), LowLenBits, i);
            }

            for (; i < LowLenSymbols + MidLenSymbols; i++)
            {
                if (i >= numSymbols)
                {
                    return;
                }

                _prices[st + i] = b0 + BitTreeGetPrice(Mid, (int)(posState << MidLenBits), MidLenBits, i - LowLenSymbols);
            }

            for (; i < numSymbols; i++)
            {
                _prices[st + i] = b1 + BitTreeGetPrice(High, 0, HighLenBits, i - LowLenSymbols - MidLenSymbols);
            }
        }
    }

    /// <summary>
    /// The SDK's binary tree match finder (BT4) over one hunk in memory. Positions count from 1, so 0
    /// means an empty slot. The hash slots a hunk filled are cleared before the next, instead of the
    /// whole table, which gives the same matches at a fraction of the work.
    /// </summary>
    private sealed class MatchFinder
    {
        private const uint Hash2Size = 1 << 10;
        private const uint Hash3Size = 1 << 16;
        private const uint Hash3Offset = Hash2Size;
        private const uint FixHashSize = Hash2Size + Hash3Size;
        private const uint StartMaxLen = 1;
        private const uint EmptyHashValue = 0;
        private const uint MinMatchCheck = 4;

        private static readonly uint[] Crc = BuildCrcTable();

        private readonly byte[] _buffer;
        private readonly uint[] _son;
        private readonly uint[] _hash;
        private readonly int[] _filled;
        private readonly uint _hashMask;
        private readonly uint _cyclicBufferSize;
        private readonly uint _matchMaxLen;
        private readonly uint _cutValue;
        private int _filledCount;
        private uint _cyclicBufferPos;
        private uint _pos;
        private uint _streamPos;

        public MatchFinder(uint historySize, uint matchMaxLen)
        {
            _matchMaxLen = matchMaxLen;
            _cutValue = 16 + (matchMaxLen >> 1);
            _cyclicBufferSize = historySize + 1;
            _son = new uint[_cyclicBufferSize * 2];

            var hs = historySize - 1;
            hs |= hs >> 1;
            hs |= hs >> 2;
            hs |= hs >> 4;
            hs |= hs >> 8;
            hs >>= 1;
            hs |= 0xFFFF;
            if (hs > 1 << 24)
            {
                hs >>= 1;
            }

            _hashMask = hs;
            _hash = new uint[hs + 1 + FixHashSize];
            _filled = new int[(historySize + 1) * 3];

            // Room past the end, as the SDK's window has, for the few reads that look one byte beyond.
            _buffer = new byte[historySize + MatchMaxLen + 16];
        }

        public void Init(ReadOnlySpan<byte> source)
        {
            if ((uint)source.Length > _cyclicBufferSize - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(source));
            }

            for (var index = 0; index < _filledCount; index++)
            {
                _hash[_filled[index]] = EmptyHashValue;
            }

            _filledCount = 0;
            source.CopyTo(_buffer);
            _buffer.AsSpan(source.Length).Clear();
            _cyclicBufferPos = 0;
            _pos = 1;
            _streamPos = (uint)source.Length + 1;
        }

        /// <summary>The byte at index from the current position; the buffer starts at position 1.</summary>
        public byte GetIndexByte(int index) => _buffer[(int)_pos - 1 + index];

        public uint GetMatchLen(int index, uint distance, uint limit)
        {
            if ((long)_pos + index + limit > _streamPos)
            {
                limit = (uint)(_streamPos - ((long)_pos + index));
            }

            distance++;
            var pby = (int)_pos - 1 + index;
            uint i;
            for (i = 0; i < limit && _buffer[pby + (int)i] == _buffer[pby + (int)i - (int)distance]; i++)
            {
            }

            return i;
        }

        public uint GetNumAvailableBytes() => _streamPos - _pos;

        public uint GetMatches(uint[] distances)
        {
            uint lenLimit;
            if (_pos + _matchMaxLen <= _streamPos)
            {
                lenLimit = _matchMaxLen;
            }
            else
            {
                lenLimit = _streamPos - _pos;
                if (lenLimit < MinMatchCheck)
                {
                    MovePos();
                    return 0;
                }
            }

            uint offset = 0;
            var matchMinPos = _pos > _cyclicBufferSize ? _pos - _cyclicBufferSize : 0;
            var cur = (int)_pos - 1;
            var maxLen = StartMaxLen;
            var temp = Crc[_buffer[cur]] ^ _buffer[cur + 1];
            var hash2Value = temp & (Hash2Size - 1);
            temp ^= (uint)_buffer[cur + 2] << 8;
            var hash3Value = temp & (Hash3Size - 1);
            var hashValue = (temp ^ (Crc[_buffer[cur + 3]] << 5)) & _hashMask;

            var curMatch = _hash[FixHashSize + hashValue];
            var curMatch2 = _hash[hash2Value];
            var curMatch3 = _hash[Hash3Offset + hash3Value];
            SetHash(hash2Value);
            SetHash(Hash3Offset + hash3Value);
            if (curMatch2 > matchMinPos && _buffer[(int)curMatch2 - 1] == _buffer[cur])
            {
                distances[offset++] = maxLen = 2;
                distances[offset++] = _pos - curMatch2 - 1;
            }

            if (curMatch3 > matchMinPos && _buffer[(int)curMatch3 - 1] == _buffer[cur])
            {
                if (curMatch3 == curMatch2)
                {
                    offset -= 2;
                }

                distances[offset++] = maxLen = 3;
                distances[offset++] = _pos - curMatch3 - 1;
                curMatch2 = curMatch3;
            }

            if (offset != 0 && curMatch2 == curMatch)
            {
                offset -= 2;
                maxLen = StartMaxLen;
            }

            SetHash(FixHashSize + hashValue);

            var ptr0 = (_cyclicBufferPos << 1) + 1;
            var ptr1 = _cyclicBufferPos << 1;

            uint len0 = 0;
            uint len1 = 0;
            var count = _cutValue;

            while (true)
            {
                if (curMatch <= matchMinPos || count-- == 0)
                {
                    _son[ptr0] = _son[ptr1] = EmptyHashValue;
                    break;
                }

                var delta = _pos - curMatch;
                var cyclicPos = (delta <= _cyclicBufferPos ? _cyclicBufferPos - delta : _cyclicBufferPos - delta + _cyclicBufferSize) << 1;

                var pby1 = (int)curMatch - 1;
                var len = Math.Min(len0, len1);
                if (_buffer[pby1 + (int)len] == _buffer[cur + (int)len])
                {
                    while (++len != lenLimit)
                    {
                        if (_buffer[pby1 + (int)len] != _buffer[cur + (int)len])
                        {
                            break;
                        }
                    }

                    if (maxLen < len)
                    {
                        distances[offset++] = maxLen = len;
                        distances[offset++] = delta - 1;
                        if (len == lenLimit)
                        {
                            _son[ptr1] = _son[cyclicPos];
                            _son[ptr0] = _son[cyclicPos + 1];
                            break;
                        }
                    }
                }

                if (_buffer[pby1 + (int)len] < _buffer[cur + (int)len])
                {
                    _son[ptr1] = curMatch;
                    ptr1 = cyclicPos + 1;
                    curMatch = _son[ptr1];
                    len1 = len;
                }
                else
                {
                    _son[ptr0] = curMatch;
                    ptr0 = cyclicPos;
                    curMatch = _son[ptr0];
                    len0 = len;
                }
            }

            MovePos();
            return offset;
        }

        public void Skip(uint num)
        {
            do
            {
                uint lenLimit;
                if (_pos + _matchMaxLen <= _streamPos)
                {
                    lenLimit = _matchMaxLen;
                }
                else
                {
                    lenLimit = _streamPos - _pos;
                    if (lenLimit < MinMatchCheck)
                    {
                        MovePos();
                        continue;
                    }
                }

                var matchMinPos = _pos > _cyclicBufferSize ? _pos - _cyclicBufferSize : 0;
                var cur = (int)_pos - 1;

                var temp = Crc[_buffer[cur]] ^ _buffer[cur + 1];
                var hash2Value = temp & (Hash2Size - 1);
                SetHash(hash2Value);
                temp ^= (uint)_buffer[cur + 2] << 8;
                var hash3Value = temp & (Hash3Size - 1);
                SetHash(Hash3Offset + hash3Value);
                var hashValue = (temp ^ (Crc[_buffer[cur + 3]] << 5)) & _hashMask;

                var curMatch = _hash[FixHashSize + hashValue];
                SetHash(FixHashSize + hashValue);

                var ptr0 = (_cyclicBufferPos << 1) + 1;
                var ptr1 = _cyclicBufferPos << 1;

                uint len0 = 0;
                uint len1 = 0;
                var count = _cutValue;
                while (true)
                {
                    if (curMatch <= matchMinPos || count-- == 0)
                    {
                        _son[ptr0] = _son[ptr1] = EmptyHashValue;
                        break;
                    }

                    var delta = _pos - curMatch;
                    var cyclicPos = (delta <= _cyclicBufferPos ? _cyclicBufferPos - delta : _cyclicBufferPos - delta + _cyclicBufferSize) << 1;

                    var pby1 = (int)curMatch - 1;
                    var len = Math.Min(len0, len1);
                    if (_buffer[pby1 + (int)len] == _buffer[cur + (int)len])
                    {
                        while (++len != lenLimit)
                        {
                            if (_buffer[pby1 + (int)len] != _buffer[cur + (int)len])
                            {
                                break;
                            }
                        }

                        if (len == lenLimit)
                        {
                            _son[ptr1] = _son[cyclicPos];
                            _son[ptr0] = _son[cyclicPos + 1];
                            break;
                        }
                    }

                    if (_buffer[pby1 + (int)len] < _buffer[cur + (int)len])
                    {
                        _son[ptr1] = curMatch;
                        ptr1 = cyclicPos + 1;
                        curMatch = _son[ptr1];
                        len1 = len;
                    }
                    else
                    {
                        _son[ptr0] = curMatch;
                        ptr0 = cyclicPos;
                        curMatch = _son[ptr0];
                        len0 = len;
                    }
                }

                MovePos();
            }
            while (--num != 0);
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var r = i;
                for (var j = 0; j < 8; j++)
                {
                    r = (r & 1) != 0 ? (r >> 1) ^ 0xEDB88320 : r >> 1;
                }

                table[i] = r;
            }

            return table;
        }

        private void SetHash(uint index)
        {
            if (_hash[index] == EmptyHashValue)
            {
                _filled[_filledCount++] = (int)index;
            }

            _hash[index] = _pos;
        }

        private void MovePos()
        {
            if (++_cyclicBufferPos >= _cyclicBufferSize)
            {
                _cyclicBufferPos = 0;
            }

            _pos++;
        }
    }
}
