namespace Loadout.Images;

/// <summary>
/// Sequential 2048-byte user data from an ISO or a 2352-byte MODE1/MODE2 image, or from a chd, read
/// as the iso or bin chdman extracts from it. One sector is buffered. The file is not loaded into memory.
/// </summary>
public sealed class IsoUserDataStream : Stream
{
    public const int UserSectorSize = 2048;

    private static ReadOnlySpan<byte> Sync => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private readonly Stream _file;
    private readonly int _stride;
    private readonly int _dataOffset;
    private readonly byte[] _raw;
    private readonly byte[] _user = new byte[UserSectorSize];
    private int _pending;
    private int _pendingOffset;
    private long _position;
    private bool _disposed;

    private IsoUserDataStream(Stream file, int stride, int dataOffset)
    {
        _file = file;
        _stride = stride;
        _dataOffset = dataOffset;
        _raw = new byte[stride];
        Length = file.Length / stride * UserSectorSize;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public static IsoUserDataStream Open(string path)
    {
        Stream file = ChdImageStream.IsChd(path)
            ? ChdImageStream.Open(path)
            : new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var (stride, offset) = Detect(file);
            file.Position = 0;
            return new IsoUserDataStream(file, stride, offset);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateRead(buffer, offset, count);
        var filled = 0;
        while (filled < count)
        {
            if (_pendingOffset >= _pending && !LoadSector())
            {
                break;
            }

            var take = Math.Min(count - filled, _pending - _pendingOffset);
            Buffer.BlockCopy(_user, _pendingOffset, buffer, offset + filled, take);
            _pendingOffset += take;
            filled += take;
            _position += take;
        }

        return filled;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            if (_pendingOffset >= _pending && !await LoadSectorAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            var take = Math.Min(buffer.Length - filled, _pending - _pendingOffset);
            _user.AsMemory(_pendingOffset, take).CopyTo(buffer[filled..]);
            _pendingOffset += take;
            filled += take;
            _position += take;
        }

        return filled;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

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
            _file.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool LoadSector()
    {
        var filled = 0;
        while (filled < _stride)
        {
            var count = _file.Read(_raw, filled, _stride - filled);
            if (count == 0)
            {
                return false;
            }

            filled += count;
        }

        PublishSector();
        return true;
    }

    private async ValueTask<bool> LoadSectorAsync(CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < _stride)
        {
            var count = await _file.ReadAsync(_raw.AsMemory(read, _stride - read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        PublishSector();
        return true;
    }

    private void PublishSector()
    {
        Buffer.BlockCopy(_raw, _dataOffset, _user, 0, UserSectorSize);
        _pending = UserSectorSize;
        _pendingOffset = 0;
    }

    private static (int Stride, int Offset) Detect(Stream file)
    {
        var probe = new byte[16];
        var count = file.Read(probe, 0, probe.Length);
        if (count == probe.Length && probe.AsSpan(0, 12).SequenceEqual(Sync))
        {
            if (probe[15] == 0x01)
            {
                return (2352, 16);
            }

            if (probe[15] == 0x02)
            {
                return (2352, 24);
            }
        }

        return (UserSectorSize, 0);
    }

    private static void ValidateRead(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("The read extends past the buffer.", nameof(count));
        }
    }
}
