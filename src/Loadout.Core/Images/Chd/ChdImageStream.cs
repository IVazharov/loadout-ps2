namespace Loadout.Images;

/// <summary>
/// A chd read as the file chdman's extractdvd or extractcd writes from it, byte for byte, with nothing
/// written to the disk: a DVD's iso, or a CD's bin, which holds each track's frames without their
/// subcode or padding, audio little-endian. Seekable. Hunks are decoded as they are needed; while the
/// reads go in order, each batch is twice the last, up to about 16 MB, and the next one is decoded
/// ahead, side by side. One read at a time, as with any stream.
/// </summary>
internal sealed class ChdImageStream : Stream
{
    private readonly ChdFile _chd;
    private readonly Segment[] _segments;
    private readonly CancellationTokenSource _stop = new();
    private Batch? _current;
    private Task<Batch>? _ahead;
    private byte[]? _spare;
    private int _run = 1;
    private long _position;
    private bool _disposed;

    private ChdImageStream(ChdFile chd, IReadOnlyList<ChdTrack> tracks)
    {
        _chd = chd;
        Tracks = tracks;
        if (tracks.Count == 0)
        {
            _segments = [];
            Length = chd.LogicalBytes;
            return;
        }

        if (chd.HunkBytes % ChdCdCodec.FrameBytes != 0)
        {
            throw new ChdException("This chd is damaged: its hunks do not hold whole CD frames.");
        }

        // Each track's frames, in the bin one after another, and in the chd with padding after each track.
        _segments = new Segment[tracks.Count];
        long binStart = 0;
        long frame = 0;
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            _segments[index] = new Segment(binStart, (long)track.Frames * track.DataBytes, frame * ChdCdCodec.FrameBytes, track.DataBytes, track.Type == "AUDIO");
            binStart += _segments[index].Length;
            frame += track.Frames + track.ExtraFrames;
        }

        if (frame > chd.LogicalBytes / ChdCdCodec.FrameBytes)
        {
            throw new ChdException("This chd is damaged: it holds less data than its track list says.");
        }

        Length = binStart;
    }

    /// <summary>A CD's tracks, or none for a DVD.</summary>
    public IReadOnlyList<ChdTrack> Tracks { get; }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <summary>True for a path that names a chd, which is read through this stream.</summary>
    public static bool IsChd(string path) => path.EndsWith(".chd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a CD or DVD chd. Throws <see cref="ChdException"/> for any other.</summary>
    public static ChdImageStream Open(string path)
    {
        var chd = ChdFile.Open(path);
        try
        {
            return Open(chd);
        }
        catch
        {
            chd.Dispose();
            throw;
        }
    }

    /// <summary>Reads an open CD or DVD chd, which the stream then owns.</summary>
    public static ChdImageStream Open(ChdFile chd) => chd.Kind switch
    {
        ChdImageKind.Dvd => new ChdImageStream(chd, []),
        ChdImageKind.Cd => new ChdImageStream(chd, ChdTracks.Read(chd)),
        ChdImageKind.Unsupported => throw new ChdException("The chd's metadata says it is not a PS2 CD or DVD."),
        _ => throw new ChdException("The chd's metadata does not say it is a CD or a DVD."),
    };

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var filled = 0;
        while (filled < buffer.Length && _position < Length)
        {
            var (offset, take, audio) = Locate(_position, buffer.Length - filled);
            var batch = Covering(offset) ?? Load(Hunk(offset));
            filled += Copy(batch, offset, take, audio, buffer[filled..]);
        }

        return filled;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var filled = 0;
        while (filled < buffer.Length && _position < Length)
        {
            var (offset, take, audio) = Locate(_position, buffer.Length - filled);
            var batch = Covering(offset);
            if (batch == null)
            {
                // The batch decoded ahead is waited for without holding a thread; Load then takes it,
                // or reports why it failed.
                if (_ahead is { IsCompleted: false } pending)
                {
                    try
                    {
                        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }

                batch = Load(Hunk(offset));
            }

            filled += Copy(batch, offset, take, audio, buffer.Span[filled..]);
        }

        return filled;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            // The batch being decoded ahead reads the chd until it stops.
            _stop.Cancel();
            try
            {
                _ahead?.Wait();
            }
            catch (AggregateException)
            {
            }

            _chd.Dispose();
            _stop.Dispose();
        }

        base.Dispose(disposing);
    }

    private uint Hunk(long offset) => (uint)(offset / _chd.HunkBytes);

    /// <summary>Where the byte at position is in the chd, and how many from there are in order in the image.</summary>
    private (long Offset, int Take, bool Audio) Locate(long position, int wanted)
    {
        if (_segments.Length == 0)
        {
            return (position, (int)Math.Min(wanted, Length - position), false);
        }

        var index = Array.FindLastIndex(_segments, segment => segment.Start <= position);
        var found = _segments[index];
        var within = position - found.Start;
        var inFrame = (int)(within % found.DataBytes);
        var offset = found.ChdOffset + (within / found.DataBytes * ChdCdCodec.FrameBytes) + inFrame;
        return (offset, Math.Min(wanted, found.DataBytes - inFrame), found.Audio);
    }

    private Batch? Covering(long offset) => _current is { } batch && offset >= batch.Start && offset < batch.End ? batch : null;

    private int Copy(Batch batch, long offset, int take, bool audio, Span<byte> destination)
    {
        var start = (int)(offset - batch.Start);
        take = (int)Math.Min(take, batch.End - offset);
        if (audio)
        {
            // A chd keeps audio big-endian and a bin little-endian. A frame starts at an even place
            // and never crosses a hunk, so each byte's pair is in the batch.
            for (var index = 0; index < take; index++)
            {
                destination[index] = batch.Data[(start + index) ^ 1];
            }
        }
        else
        {
            batch.Data.AsSpan(start, take).CopyTo(destination);
        }

        _position += take;
        return take;
    }

    /// <summary>Makes the batch holding this hunk the current one: the one decoded ahead, or a new one.</summary>
    private Batch Load(uint hunk)
    {
        var inOrder = _current != null && hunk == _current.First + (uint)_current.Count;
        if (_ahead is { } pending)
        {
            _ahead = null;
            var ahead = pending.GetAwaiter().GetResult();
            if (hunk >= ahead.First && hunk < ahead.First + (uint)ahead.Count)
            {
                return Advance(ahead, true);
            }

            _spare = ahead.Data;
        }

        _run = inOrder ? Math.Min(_run * 2, _chd.BatchHunks) : 1;
        var count = (int)Math.Min((uint)_run, _chd.HunkCount - hunk);
        return Advance(Decode(hunk, count, Rent(count), CancellationToken.None), inOrder);
    }

    private Batch Advance(Batch batch, bool inOrder)
    {
        if (_current != null)
        {
            _spare = _current.Data;
        }

        _current = batch;
        var next = batch.First + (uint)batch.Count;
        if (inOrder && next < _chd.HunkCount)
        {
            _run = Math.Min(_run * 2, _chd.BatchHunks);
            var count = (int)Math.Min((uint)_run, _chd.HunkCount - next);
            var buffer = Rent(count);
            var stop = _stop.Token;
            _ahead = Task.Run(() => Decode(next, count, buffer, stop), stop);
        }

        return batch;
    }

    private Batch Decode(uint first, int count, byte[] buffer, CancellationToken cancellationToken)
    {
        _chd.DecodeHunks(first, count, buffer, cancellationToken);
        return new Batch(first, count, buffer, (long)first * _chd.HunkBytes, Math.Min(((long)first + count) * _chd.HunkBytes, _chd.LogicalBytes));
    }

    private byte[] Rent(int count)
    {
        var bytes = count * _chd.HunkBytes;
        if (_spare is { } spare && spare.Length >= bytes)
        {
            _spare = null;
            return spare;
        }

        return new byte[bytes];
    }

    /// <summary>Hunks decoded together. End is where the image's data in them ends.</summary>
    private sealed record Batch(uint First, int Count, byte[] Data, long Start, long End);

    /// <summary>A track's place in the bin, and where its first frame is in the chd.</summary>
    private readonly record struct Segment(long Start, long Length, long ChdOffset, int DataBytes, bool Audio);
}
