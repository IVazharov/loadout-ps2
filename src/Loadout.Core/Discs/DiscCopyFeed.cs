namespace Loadout.Discs;

/// <summary>
/// How far a copy under way holds the disc's sectors in order from the first, for an install on the PS2 that follows it.
/// The copier says what it wrote once it is in the file, and the install waits for the sectors it needs. When the copier
/// fails a read, or stops, the sectors after it won't come in order for a long time: the feed breaks, and its
/// <see cref="Broken"/> token cancels the install.
/// </summary>
public sealed class DiscCopyFeed : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _broken = new();
    private long _through;
    private bool _opened;
    private bool _closed;
    private bool _disposed;
    private TaskCompletionSource _moved = NewSignal();

    /// <param name="sectors">The disc's sectors.</param>
    /// <param name="through">How many sectors from the first are in the file already.</param>
    public DiscCopyFeed(long sectors, long through)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectors);
        ArgumentOutOfRangeException.ThrowIfNegative(through);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(through, sectors);
        Sectors = sectors;
        _through = through;
    }

    public long Sectors { get; }

    /// <summary>How many sectors from the first are in the file.</summary>
    public long Through
    {
        get
        {
            lock (_gate)
            {
                return _through;
            }
        }
    }

    public bool IsBroken => _broken.IsCancellationRequested;

    /// <summary>Cancelled when the copy can no longer give the sectors in order.</summary>
    public CancellationToken Broken => _broken.Token;

    /// <summary>A follow install is possible when nothing the copy tried is missing: what it has runs from the first sector.</summary>
    public static bool CanFollow(DiscCopyMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.MissingSectors == 0;
    }

    /// <summary>A feed for a copy about to go on from this map.</summary>
    public static DiscCopyFeed For(DiscCopyMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var gap = map.Ranges.FirstOrDefault(r => !r.State.IsRead());
        return new DiscCopyFeed(map.Sectors, gap.Count > 0 ? gap.Start : map.Sectors);
    }

    /// <summary>Sectors written and flushed to the file. Only a piece that joins the run from the first moves it on.</summary>
    public void Wrote(long sector, long count)
    {
        TaskCompletionSource moved;
        lock (_gate)
        {
            if (sector > _through || sector + count <= _through)
            {
                return;
            }

            _through = Math.Min(Sectors, sector + count);
            moved = _moved;
            _moved = NewSignal();
        }

        moved.TrySetResult();
    }

    /// <summary>
    /// The copy is complete and its file is about to be renamed: true when no install opened the file yet, and then none
    /// will (the feed breaks, and the finished copy is installed instead). False when one has it open: it reads on from
    /// its open file, which can be renamed under it.
    /// </summary>
    public bool CloseUnopened()
    {
        lock (_gate)
        {
            if (_opened)
            {
                return false;
            }

            _closed = true;
        }

        Break();
        return true;
    }

    /// <summary>For the stream, before it opens the file: false once <see cref="CloseUnopened"/> closed the feed.</summary>
    internal bool TryOpen()
    {
        lock (_gate)
        {
            _opened = !_closed;
            return _opened;
        }
    }

    /// <summary>The copy failed a read or stopped: the install can't follow it any more. Safe after <see cref="Dispose"/>.</summary>
    public void Break()
    {
        TaskCompletionSource moved;
        lock (_gate)
        {
            if (_disposed || _broken.IsCancellationRequested)
            {
                return;
            }

            moved = _moved;
        }

        // Outside the lock: cancelling runs the install's own cancellation at once.
        try
        {
            _broken.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The install ended and let the feed go meanwhile.
        }

        moved.TrySetResult();
    }

    /// <summary>
    /// Waits until <paramref name="sector"/> is in the file, and returns how many sectors from the first are. Once the feed
    /// is broken every wait fails, even for sectors the later passes have filled in since: the install is being cancelled.
    /// </summary>
    /// <exception cref="OperationCanceledException">The feed broke, or the wait was cancelled.</exception>
    public async Task<long> WaitForAsync(long sector, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_broken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The copy can't be followed in order any more.");
            }

            Task moved;
            lock (_gate)
            {
                if (_through > sector)
                {
                    return _through;
                }

                moved = _moved.Task;
            }

            await moved.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Lets the feed go once its install has ended; the copier may still tell it things, which it then ignores.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _broken.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The 2048-byte sectors of a copy under way, from the first, for an install that follows it: each read waits until the
/// copy has the sectors. A CD copy is raw, so each sector's data is taken from inside its 2352 bytes.
/// </summary>
public sealed class DiscCopyFeedStream : Stream
{
    private const int User = DiscSectors.Size;

    // Raw CD sectors read in one go.
    private const int RawBatch = 64;

    private readonly DiscCopyFeed _feed;
    private readonly string _image;
    private readonly int _stride;
    private readonly byte[] _raw;
    private FileStream? _file;
    private long _position;

    /// <param name="image">The copy's file, which the copier has open for writing; opened at the first read, once it holds sectors.</param>
    /// <param name="stride">Its sector size: 2048, or 2352 for a raw CD copy.</param>
    public DiscCopyFeedStream(DiscCopyFeed feed, string image, int stride)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrEmpty(image);
        if (stride is not (User or 2352))
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "A copy's sectors are 2048 or 2352 bytes.");
        }

        _feed = feed;
        _image = image;
        _stride = stride;
        _raw = stride == User ? [] : new byte[RawBatch * stride];
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _feed.Sectors * User;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _position >= Length)
        {
            return 0;
        }

        var sector = _position / User;
        var through = await _feed.WaitForAsync(sector, cancellationToken).ConfigureAwait(false);
        var available = (through * User) - _position;
        var count = (int)Math.Min(buffer.Length, available);
        if (_file is null && !_feed.TryOpen())
        {
            throw new OperationCanceledException("The copy finished before the install opened it.");
        }

        // Unbuffered: a buffer would read ahead past what the copy has written, and keep that. Shared for deleting too,
        // so the finished copy can be renamed while this reads on.
        var file = _file ??= new FileStream(_image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.Asynchronous);
        if (_stride == User)
        {
            file.Position = _position;
            await file.ReadExactlyAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Raw sectors: the data follows the sync and header (mode 1) or the subheader as well (mode 2).
            var filled = 0;
            while (filled < count)
            {
                var first = (_position + filled) / User;
                var sectors = (int)Math.Min(RawBatch, ((_position + count - 1) / User) - first + 1);
                file.Position = first * _stride;
                await file.ReadExactlyAsync(_raw.AsMemory(0, sectors * _stride), cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < sectors && filled < count; i++)
                {
                    var raw = _raw.AsMemory(i * _stride, _stride);
                    var at = (int)((_position + filled) % User);
                    var take = Math.Min(count - filled, User - at);
                    raw.Slice((raw.Span[15] == 1 ? 16 : 24) + at, take).CopyTo(buffer[filled..]);
                    filled += take;
                }
            }
        }

        _position += count;
        return count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file?.Dispose();
        }

        base.Dispose(disposing);
    }
}
