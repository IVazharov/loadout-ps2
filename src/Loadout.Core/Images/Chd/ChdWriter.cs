using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Loadout.Images;

/// <summary>
/// Writes a version 5 chd as chdman's createdvd and createcd do, so chdman, PCSX2 and Loadout read it
/// alike. The layout follows MAME's chd.cpp and chdman.cpp (BSD-3-Clause; see ThirdParty/MameChd): the
/// header, one metadata entry, the hunks in order, then the compressed map. A hunk equal to an earlier
/// one is kept as a reference to it. The header's two SHA-1s are chdman's: one of the data, and one of
/// that and the metadata.
/// </summary>
public static class ChdWriter
{
    private const int HeaderBytes = 124;
    private const int MapEntryBytes = 12;
    private const int DvdHunkBytes = 4096;
    private const int DvdUnitBytes = 2048;
    private const int CdFramesPerHunk = 8;
    private const int CdTrackPadding = 4;
    private const int BatchBytes = 4 * 1024 * 1024;
    private const byte MetadataChecksum = 0x01;

    private const byte CompressionNone = 4;
    private const byte CompressionSelf = 5;
    private const byte CompressionRleSmall = 7;
    private const byte CompressionRleLarge = 8;
    private const byte CompressionSelf0 = 9;
    private const byte CompressionSelf1 = 10;

    /// <summary>Writes a DVD's iso (2048-byte sectors) as the chd destination, replacing any file there.</summary>
    public static Task<long> WriteDvdAsync(string image, string destination, IProgress<long>? progress, CancellationToken cancellationToken)
        => RunAsync(() => Write(Source.Dvd(image), destination, progress, Threads(), cancellationToken), cancellationToken);

    /// <summary>
    /// Writes a one-track CD's raw bin (2352-byte sectors) as the chd destination, replacing any file
    /// there. trackType is chdman's name for the cue's type: MODE2_RAW for MODE2/2352, MODE1_RAW for MODE1/2352.
    /// </summary>
    public static Task<long> WriteCdAsync(string image, string trackType, string destination, IProgress<long>? progress, CancellationToken cancellationToken)
        => RunAsync(() => Write(Source.Cd(image, trackType), destination, progress, Threads(), cancellationToken), cancellationToken);

    internal static long WriteDvd(string image, string destination, int threads, CancellationToken cancellationToken)
        => Write(Source.Dvd(image), destination, null, threads, cancellationToken);

    internal static long WriteCd(string image, string trackType, string destination, int threads, CancellationToken cancellationToken)
        => Write(Source.Cd(image, trackType), destination, null, threads, cancellationToken);

    /// <summary>chdman's overall SHA-1: the data's SHA-1, then each checksummed metadata entry's tag and SHA-1, sorted.</summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The chd format is defined with SHA-1: it names data, it protects nothing.")]
    internal static byte[] OverallSha1(ReadOnlySpan<byte> rawSha1, IEnumerable<(string Tag, byte[] Data)> metadata)
    {
        var entries = metadata
            .Select(entry => Encoding.Latin1.GetBytes(entry.Tag).Concat(SHA1.HashData(entry.Data)).ToArray())
            .OrderBy(entry => entry, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ToList();
        var all = new byte[rawSha1.Length + (entries.Count * 24)];
        rawSha1.CopyTo(all);
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].CopyTo(all, rawSha1.Length + (index * 24));
        }

        return SHA1.HashData(all);
    }

    /// <summary>
    /// Half the logical processors: about one a core. On a laptop measured for it (6 performance and 8
    /// efficiency cores, 20 threads) 12 threads wrote 23 MB/s and 19 wrote 22, so more only heats it.
    /// </summary>
    private static int Threads() => Math.Max(1, Environment.ProcessorCount / 2);

    private static Task<long> RunAsync(Func<long> write, CancellationToken cancellationToken)
        => Task.Factory.StartNew(write, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static long Write(Source source, string destination, IProgress<long>? progress, int threads, CancellationToken cancellationToken)
    {
        using (source)
        {
            var written = false;
            try
            {
                var length = WriteFile(source, destination, progress, threads, cancellationToken);
                written = true;
                return length;
            }
            finally
            {
                if (!written)
                {
                    try
                    {
                        File.Delete(destination);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The chd format is defined with SHA-1: it names data, it protects nothing.")]
    private static long WriteFile(Source source, string destination, IProgress<long>? progress, int threads, CancellationToken cancellationToken)
    {
        var hunkBytes = source.HunkBytes;
        var hunkCount = (uint)((source.LogicalBytes + hunkBytes - 1) / hunkBytes);
        var batchHunks = (int)Math.Min(hunkCount, (uint)Math.Max(1, BatchBytes / hunkBytes));
        var raw = new byte[batchHunks * hunkBytes];
        var compressed = new byte[batchHunks][];
        for (var index = 0; index < batchHunks; index++)
        {
            compressed[index] = new byte[hunkBytes];
        }

        var codecs = new int[batchHunks];
        var lengths = new int[batchHunks];
        var crcs = new ushort[batchHunks];
        var keys = new HunkKey[batchHunks];
        var map = new byte[hunkCount * MapEntryBytes];
        var seen = new Dictionary<HunkKey, uint>();
        var encoders = new ChdHunkEncoder[threads];
        using var disposeEncoders = new EncoderSet(encoders);
        for (var index = 0; index < threads; index++)
        {
            encoders[index] = new ChdHunkEncoder(source.IsCd, hunkBytes);
        }

        using var rawSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var workers = new Workers(threads);

        output.Write(new byte[HeaderBytes]);
        var metadataOffset = output.Position;
        var metadataHead = new byte[16];
        Encoding.Latin1.GetBytes(source.MetadataTag, metadataHead);
        metadataHead[4] = MetadataChecksum;
        metadataHead[5] = (byte)(source.Metadata.Length >> 16);
        metadataHead[6] = (byte)(source.Metadata.Length >> 8);
        metadataHead[7] = (byte)source.Metadata.Length;
        output.Write(metadataHead);
        output.Write(source.Metadata);
        var firstOffset = output.Position;

        for (uint first = 0; first < hunkCount; first += (uint)batchHunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min((uint)batchHunks, hunkCount - first);
            source.Read(first, count, raw);
            var logicalInBatch = (int)Math.Min((long)count * hunkBytes, source.LogicalBytes - ((long)first * hunkBytes));
            rawSha1.AppendData(raw, 0, logicalInBatch);

            var next = -1;
            workers.Run(worker =>
            {
                Span<byte> digest = stackalloc byte[20];
                int index;
                while ((index = Interlocked.Increment(ref next)) < count)
                {
                    var hunk = raw.AsSpan(index * hunkBytes, hunkBytes);
                    SHA1.HashData(hunk, digest);
                    keys[index] = new HunkKey(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
                    crcs[index] = ChdFile.Crc16(hunk);
                    (codecs[index], lengths[index]) = encoders[worker].Encode(hunk, compressed[index]);
                }
            });

            for (var index = 0; index < count; index++)
            {
                var hunk = first + (uint)index;
                var entry = map.AsSpan((int)hunk * MapEntryBytes, MapEntryBytes);
                if (seen.TryGetValue(keys[index], out var earlier))
                {
                    entry[0] = CompressionSelf;
                    WriteUInt48(entry[4..], earlier);
                    continue;
                }

                seen.Add(keys[index], hunk);
                var offset = output.Position;
                output.Write(compressed[index], 0, lengths[index]);
                entry[0] = codecs[index] == ChdHunkEncoder.Uncompressed ? CompressionNone : (byte)codecs[index];
                entry[1] = (byte)(lengths[index] >> 16);
                entry[2] = (byte)(lengths[index] >> 8);
                entry[3] = (byte)lengths[index];
                WriteUInt48(entry[4..], offset);
                BinaryPrimitives.WriteUInt16BigEndian(entry[10..], crcs[index]);
            }

            progress?.Report(source.SourceBytesRead(first + (uint)count));
        }

        var mapOffset = output.Position;
        output.Write(EncodeMap(map, hunkCount, firstOffset));

        var dataSha1 = rawSha1.GetHashAndReset();
        var header = new byte[HeaderBytes];
        "MComprHD"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 5);
        for (var index = 0; index < source.Compressors.Length; index++)
        {
            Encoding.Latin1.GetBytes(source.Compressors[index], header.AsSpan(16 + (index * 4)));
        }

        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), (ulong)source.LogicalBytes);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), (ulong)mapOffset);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), (ulong)metadataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), (uint)source.UnitBytes);
        dataSha1.CopyTo(header, 64);
        OverallSha1(dataSha1, [(source.MetadataTag, source.Metadata)]).CopyTo(header, 84);
        var length = output.Position;
        output.Position = 0;
        output.Write(header);
        output.Flush(flushToDisk: true);
        return length;
    }

    private static void WriteUInt48(Span<byte> destination, long value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(value >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[2..], (uint)value);
    }

    private static long ReadUInt48(ReadOnlySpan<byte> source)
        => ((long)BinaryPrimitives.ReadUInt16BigEndian(source) << 32) | BinaryPrimitives.ReadUInt32BigEndian(source[2..]);

    private static int BitsFor(long value)
    {
        var bits = 0;
        while (value != 0)
        {
            value >>= 1;
            bits++;
        }

        return bits;
    }

    /// <summary>
    /// The map as MAME's compress_v5_map writes it: each hunk's kind, run-length coded and in Huffman
    /// codes, then each hunk's length, CRC or reference in as few bits as the largest needs.
    /// </summary>
    private static byte[] EncodeMap(byte[] map, uint hunkCount, long firstOffset)
    {
        var count = (int)hunkCount;
        var kinds = new byte[count];
        long lastSelf = 0;
        long maxSelf = 0;
        long maxLength = 0;
        for (var hunk = 0; hunk < count; hunk++)
        {
            var entry = map.AsSpan(hunk * MapEntryBytes, MapEntryBytes);
            var kind = entry[0];
            if (kind == CompressionSelf)
            {
                var reference = ReadUInt48(entry[4..]);
                if (reference == lastSelf)
                {
                    kind = CompressionSelf0;
                }
                else if (reference == lastSelf + 1)
                {
                    kind = CompressionSelf1;
                }
                else
                {
                    maxSelf = Math.Max(maxSelf, reference);
                }

                lastSelf = reference;
            }
            else if (kind < CompressionNone)
            {
                maxLength = Math.Max(maxLength, (entry[1] << 16) | (entry[2] << 8) | entry[3]);
            }

            kinds[hunk] = kind;
        }

        // The kinds, run-length coded as ChdFile reads them: a repeat goes on with the last kind given.
        var symbols = new List<byte>(count);
        byte last = 0;
        var position = 0;
        while (position < count)
        {
            var kind = kinds[position];
            var run = 1;
            while (position + run < count && kinds[position + run] == kind)
            {
                run++;
            }

            position += run;
            if (kind != last)
            {
                symbols.Add(kind);
                last = kind;
                run--;
            }

            while (run > 0)
            {
                if (run < 3)
                {
                    symbols.Add(kind);
                    run--;
                }
                else if (run <= 3 + 15)
                {
                    symbols.Add(CompressionRleSmall);
                    symbols.Add((byte)(run - 3));
                    run = 0;
                }
                else
                {
                    var take = Math.Min(run, 3 + 16 + 255);
                    symbols.Add(CompressionRleLarge);
                    symbols.Add((byte)((take - 3 - 16) >> 4));
                    symbols.Add((byte)((take - 3 - 16) & 15));
                    run -= take;
                }
            }
        }

        var tree = new ChdHuffmanEncoder(16, 8);
        foreach (var symbol in symbols)
        {
            tree.Count(symbol);
        }

        tree.ComputeTree();

        var lengthBits = BitsFor(maxLength);
        var selfBits = BitsFor(maxSelf);
        var body = new byte[(count * 8) + 1024];
        var bits = new ChdBitWriter();
        bits.Reset(body, 0, body.Length);
        tree.ExportTreeRle(bits);
        foreach (var symbol in symbols)
        {
            tree.Encode(bits, symbol);
        }

        for (var hunk = 0; hunk < count; hunk++)
        {
            var entry = map.AsSpan(hunk * MapEntryBytes, MapEntryBytes);
            switch (kinds[hunk])
            {
                case < CompressionNone:
                    bits.Write((uint)((entry[1] << 16) | (entry[2] << 8) | entry[3]), lengthBits);
                    bits.Write(BinaryPrimitives.ReadUInt16BigEndian(entry[10..]), 16);
                    break;
                case CompressionNone:
                    bits.Write(BinaryPrimitives.ReadUInt16BigEndian(entry[10..]), 16);
                    break;
                case CompressionSelf:
                    bits.Write((uint)ReadUInt48(entry[4..]), selfBits);
                    break;
                default:
                    break;
            }
        }

        var bodyLength = bits.Flush();
        if (bits.Overflow)
        {
            throw new InvalidOperationException("The chd map did not fit its buffer.");
        }

        // The CRC is of the entries as ChdFile rebuilds them, which is how they are kept here: a hunk
        // stored as it is has the hunk's size as its length, a reference has no length and no CRC.
        var result = new byte[16 + bodyLength];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)bodyLength);
        WriteUInt48(result.AsSpan(4), firstOffset);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), ChdFile.Crc16(map));
        result[12] = (byte)lengthBits;
        result[13] = (byte)selfBits;
        result[14] = 0;
        body.AsSpan(0, bodyLength).CopyTo(result.AsSpan(16));
        return result;
    }

    private readonly record struct HunkKey(ulong First, ulong Second);

    private sealed class EncoderSet(ChdHunkEncoder[] encoders) : IDisposable
    {
        public void Dispose()
        {
            foreach (var encoder in encoders)
            {
                encoder?.Dispose();
            }
        }
    }

    /// <summary>The image being written: its bytes as the chd's hunks, and what the header and metadata say of it.</summary>
    private sealed class Source : IDisposable
    {
        private readonly SafeFileHandle _file;
        private readonly long _imageBytes;
        private readonly long _frames;
        private byte[] _sectors = [];

        private Source(SafeFileHandle file, bool cd, long imageBytes, long frames, string metadataTag, byte[] metadata)
        {
            _file = file;
            IsCd = cd;
            _imageBytes = imageBytes;
            _frames = frames;
            MetadataTag = metadataTag;
            Metadata = metadata;
            if (cd)
            {
                HunkBytes = ChdCdCodec.FrameBytes * CdFramesPerHunk;
                UnitBytes = ChdCdCodec.FrameBytes;
                Compressors = ChdHunkEncoder.CdCompressors;
                var padded = (frames + CdTrackPadding - 1) / CdTrackPadding * CdTrackPadding;
                LogicalBytes = padded * ChdCdCodec.FrameBytes;
            }
            else
            {
                HunkBytes = DvdHunkBytes;
                UnitBytes = DvdUnitBytes;
                Compressors = ChdHunkEncoder.DvdCompressors;
                LogicalBytes = imageBytes;
            }
        }

        public bool IsCd { get; }

        public int HunkBytes { get; }

        public int UnitBytes { get; }

        public long LogicalBytes { get; }

        public string[] Compressors { get; }

        public string MetadataTag { get; }

        public byte[] Metadata { get; }

        public static Source Dvd(string image)
        {
            var file = File.OpenHandle(image, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            var length = RandomAccess.GetLength(file);
            if (length == 0)
            {
                file.Dispose();
                throw new InvalidDataException("The image is empty.");
            }

            // chdman's createdvd: a "DVD " entry holding an empty string.
            return new Source(file, cd: false, length, 0, "DVD ", [0]);
        }

        public static Source Cd(string image, string trackType)
        {
            var file = File.OpenHandle(image, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            var length = RandomAccess.GetLength(file);
            if (length == 0 || length % ChdCdCodec.SectorBytes != 0)
            {
                file.Dispose();
                throw new InvalidDataException("The image is not whole 2352-byte CD sectors.");
            }

            var frames = length / ChdCdCodec.SectorBytes;

            // chdman's createcd for a one-track sheet, as in the chds it makes of PS2 CDs.
            var text = "TRACK:1 TYPE:" + trackType + " SUBTYPE:NONE FRAMES:" + frames + " PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0\0";
            return new Source(file, cd: true, length, frames, "CHT2", Encoding.ASCII.GetBytes(text));
        }

        /// <summary>How far into the image the hunks before end reach, for progress.</summary>
        public long SourceBytesRead(uint end)
            => IsCd
                ? Math.Min(_frames, (long)end * CdFramesPerHunk) * ChdCdCodec.SectorBytes
                : Math.Min(_imageBytes, (long)end * HunkBytes);

        /// <summary>Reads count whole hunks from first into buffer; past the image's end they are zeros.</summary>
        public void Read(uint first, int count, byte[] buffer)
        {
            var bytes = count * HunkBytes;
            if (!IsCd)
            {
                var offset = (long)first * HunkBytes;
                var available = (int)Math.Max(0, Math.Min(bytes, _imageBytes - offset));
                ReadExact(offset, buffer.AsSpan(0, available));
                buffer.AsSpan(available, bytes - available).Clear();
                return;
            }

            // Each frame is a 2352-byte sector from the bin and 96 bytes of subcode, which a bin has none of.
            buffer.AsSpan(0, bytes).Clear();
            var firstFrame = (long)first * CdFramesPerHunk;
            var frames = (int)Math.Max(0, Math.Min(count * CdFramesPerHunk, _frames - firstFrame));
            if (frames == 0)
            {
                return;
            }

            if (_sectors.Length < frames * ChdCdCodec.SectorBytes)
            {
                _sectors = new byte[frames * ChdCdCodec.SectorBytes];
            }

            ReadExact(firstFrame * ChdCdCodec.SectorBytes, _sectors.AsSpan(0, frames * ChdCdCodec.SectorBytes));
            for (var frame = 0; frame < frames; frame++)
            {
                _sectors.AsSpan(frame * ChdCdCodec.SectorBytes, ChdCdCodec.SectorBytes).CopyTo(buffer.AsSpan(frame * ChdCdCodec.FrameBytes));
            }
        }

        public void Dispose() => _file.Dispose();

        private void ReadExact(long offset, Span<byte> destination)
        {
            var total = 0;
            while (total < destination.Length)
            {
                var read = RandomAccess.Read(_file, destination[total..], offset + total);
                if (read == 0)
                {
                    throw new EndOfStreamException("The image ended early: it may have changed while it was being compressed.");
                }

                total += read;
            }
        }
    }

    /// <summary>
    /// Threads of their own, below normal priority, so a long compression leaves the thread pool and
    /// the rest of the computer free: a disc copy can go on beside it.
    /// </summary>
    private sealed class Workers : IDisposable
    {
        private readonly Thread[] _threads;
        private readonly SemaphoreSlim _start = new(0);
        private readonly CountdownEvent _done;
        private Action<int>? _work;
        private ExceptionDispatchInfo? _error;
        private volatile bool _stopping;

        public Workers(int count)
        {
            _done = new CountdownEvent(count);
            _threads = new Thread[count];
            for (var index = 0; index < count; index++)
            {
                var worker = index;
                _threads[index] = new Thread(() => Loop(worker))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "Loadout chd writer " + index,
                };
                _threads[index].Start();
            }
        }

        public void Run(Action<int> work)
        {
            _work = work;
            _error = null;
            _done.Reset(_threads.Length);
            _start.Release(_threads.Length);
            _done.Wait();
            _error?.Throw();
        }

        public void Dispose()
        {
            _stopping = true;
            _start.Release(_threads.Length);
            foreach (var thread in _threads)
            {
                thread.Join();
            }

            _start.Dispose();
            _done.Dispose();
        }

        private void Loop(int worker)
        {
            while (true)
            {
                _start.Wait();
                if (_stopping)
                {
                    return;
                }

                try
                {
                    _work!(worker);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ExceptionDispatchInfo.Capture(ex), null);
                }
                finally
                {
                    _done.Signal();
                }
            }
        }
    }
}
