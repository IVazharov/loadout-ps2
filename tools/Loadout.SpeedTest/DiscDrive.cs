using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Loadout.SpeedTest;

/// <summary>
/// A disc in a PC's optical drive, opened for reading its 2048-byte sectors without admin rights.
/// A proof of concept for installing a game from its original disc.
/// </summary>
public sealed class DiscDrive : IDisposable
{
    public const int SectorSize = 2048;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlCdromGetConfiguration = 0x00024058;
    private const uint IoctlCdromSetSpeed = 0x00024060;
    private const uint IoctlDvdReadStructure = 0x00335140;
    private const uint IoctlScsiPassThrough = 0x0004D004;
    private const uint GenericWrite = 0x40000000;

    private DiscDrive(string name, SafeFileHandle handle)
    {
        Name = name;
        Handle = handle;
    }

    /// <summary>The drive as the user named it, such as E:.</summary>
    public string Name { get; }

    public SafeFileHandle Handle { get; private set; }

    /// <summary>The drive's current MMC profile: 0x08 CD-ROM, 0x10 DVD-ROM, and so on. Null when the drive did not say.</summary>
    public int? Profile { get; private init; }

    /// <summary>The disc's length in bytes, as Windows reports it.</summary>
    public long Length { get; private init; }

    /// <summary>The DVD's physical format, or null for a CD or when the drive did not say.</summary>
    public DvdLayers? Layers { get; private init; }

    /// <summary>True when the disc is a CD by its profile.</summary>
    public bool IsCd => Profile is >= 0x08 and <= 0x0A;

    /// <summary>A drive letter such as E or E: or E:\.</summary>
    public static bool IsDriveName(string text) =>
        text.Length is >= 1 and <= 3 && char.IsAsciiLetter(text[0]) && (text.Length == 1 || text[1] == ':') && (text.Length < 3 || text[2] == '\\');

    public static DiscDrive Open(string driveName)
    {
        var letter = char.ToUpperInvariant(driveName[0]);
        var handle = NativeMethods.CreateFile($@"\\.\{letter}:", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Drive {letter}: could not be opened: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).");
        }

        // Without this, Windows may stop reading where the disc's file system says the volume ends, which on a
        // dual-layer DVD is the end of layer 0.
        _ = NativeMethods.DeviceIoControl(handle, FsctlAllowExtendedDasdIo, null, 0, null, 0, out _, IntPtr.Zero);

        var length = new byte[8];
        if (!NativeMethods.DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, length, length.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Drive {letter}: has no readable disc: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).");
        }

        return new DiscDrive($"{letter}:", handle)
        {
            Length = BinaryPrimitives.ReadInt64LittleEndian(length),
            Profile = ReadProfile(handle),
            Layers = ReadLayers(handle),
        };
    }

    /// <summary>Closes the drive and opens it again, asking for its length and layers as a first open does.</summary>
    public void Reopen()
    {
        Handle.Dispose();
        var fresh = Open(Name);
        Handle = fresh.Handle;
    }

    /// <summary>
    /// Asks a DVD drive to read the whole disc at most this many KB/s (SET STREAMING; 1385 KB/s is DVD 1x), or with null
    /// to go back to its own speed. SET CD SPEED, which <see cref="SetReadSpeed"/> sends, is often ignored for a DVD.
    /// </summary>
    public bool SetStreamingSpeed(int? kilobytesPerSecond)
    {
        // CDROM_SET_STREAMING: request type, read size (KB) per read time (ms), write size per write time, start and
        // end LBA, rotation control, then four BOOLEANs: restore defaults, set exact, random access, persistent.
        var request = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(request, 1); // CdromSetStreaming
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), kilobytesPerSecond ?? 0xFFFF);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(12), 0xFFFF);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(16), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(24), (uint)((Length / SectorSize) - 1));
        request[32] = kilobytesPerSecond is null ? (byte)1 : (byte)0;
        return NativeMethods.DeviceIoControl(Handle, IoctlCdromSetSpeed, request, request.Length, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Stops the disc and starts it again (SCSI START STOP UNIT through pass-through, which needs a read-write handle).
    /// Returns null when both worked, else what failed.
    /// </summary>
    public string? RestartSpindle()
    {
        using var device = NativeMethods.CreateFile($@"\\.\{Name}", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            return $"read-write open failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}";
        }

        foreach (var start in new[] { false, true })
        {
            // SCSI_PASS_THROUGH on x64: 56 bytes, then 32 bytes of sense data; no data phase.
            var buffer = new byte[56 + 32];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, 56);
            buffer[6] = 6; // CDB length
            buffer[7] = 32; // sense length
            buffer[8] = 2; // SCSI_IOCTL_DATA_UNSPECIFIED
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 30); // timeout, seconds
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), 56); // sense offset
            buffer[36] = 0x1B; // START STOP UNIT
            buffer[36 + 4] = start ? (byte)1 : (byte)0;
            if (!NativeMethods.DeviceIoControl(device, IoctlScsiPassThrough, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            {
                return $"{(start ? "start" : "stop")} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}";
            }

            if (buffer[2] != 0)
            {
                return $"{(start ? "start" : "stop")} returned SCSI status 0x{buffer[2]:X2}, sense key 0x{buffer[56 + 2] & 0x0F:X}, ASC 0x{buffer[56 + 12]:X2}/0x{buffer[56 + 13]:X2}";
            }
        }

        return null;
    }

    /// <summary>Asks the drive to read at most this many KB/s (0xFFFF = as fast as it can). Returns false when it refused.</summary>
    public bool SetReadSpeed(ushort kilobytesPerSecond)
    {
        var request = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(request, 0); // CdromSetSpeed
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), kilobytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), 0xFFFF);
        return NativeMethods.DeviceIoControl(Handle, IoctlCdromSetSpeed, request, request.Length, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>Reads whole sectors once, with no retry of its own. Returns the error message, or null when they were read.</summary>
    public string? TryRead(long sector, Span<byte> destination)
    {
        try
        {
            var read = 0;
            while (read < destination.Length)
            {
                var n = RandomAccess.Read(Handle, destination[read..], (sector * SectorSize) + read);
                if (n == 0)
                {
                    return "the disc ended";
                }

                read += n;
            }

            return null;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Sends one SCSI command through pass-through on a read-write handle. Data goes in or out of <paramref name="data"/>.
    /// Returns null and the SCSI status and sense key, or the Windows error when the command could not be sent.
    /// </summary>
    public string? Scsi(byte[] cdb, byte[] data, bool dataIn, int timeoutSeconds, out byte status, out byte[] sense)
    {
        status = 0xFF;
        sense = [];
        using var device = NativeMethods.CreateFile($@"\\.\{Name}", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            return $"read-write open failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}";
        }

        // SCSI_PASS_THROUGH on x64 (56 bytes), 32 bytes of sense, then the data.
        const int SenseOffset = 56;
        const int DataOffset = 88;
        var buffer = new byte[DataOffset + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, 56);
        buffer[6] = (byte)cdb.Length;
        buffer[7] = 32;
        buffer[8] = data.Length == 0 ? (byte)2 : dataIn ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), (uint)timeoutSeconds);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), SenseOffset);
        cdb.CopyTo(buffer, 36);
        if (!dataIn)
        {
            data.CopyTo(buffer, DataOffset);
        }

        if (!NativeMethods.DeviceIoControl(device, IoctlScsiPassThrough, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            return Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
        }

        status = buffer[2];
        sense = buffer[SenseOffset..(SenseOffset + 32)];
        if (dataIn)
        {
            buffer.AsSpan(DataOffset, data.Length).CopyTo(data);
        }

        return null;
    }

    public void Dispose() => Handle.Dispose();

    private static int? ReadProfile(SafeFileHandle handle)
    {
        // GET_CONFIGURATION_IOCTL_INPUT: feature 0 (profile list), request type 1 (current), two reserved pointers.
        var input = new byte[8 + (2 * IntPtr.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4), 1);
        var output = new byte[64];
        return NativeMethods.DeviceIoControl(handle, IoctlCdromGetConfiguration, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) && returned >= 8
            ? BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(6))
            : null;
    }

    private static DvdLayers? ReadLayers(SafeFileHandle handle)
    {
        // DVD_READ_STRUCTURE: byte offset 0, format 0 (physical descriptor), every session, layer 0; 24 bytes on x64.
        var input = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 0xFFFFFFFF);
        var output = new byte[2052];
        if (!NativeMethods.DeviceIoControl(handle, IoctlDvdReadStructure, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 4 + 17)
        {
            return null;
        }

        // A 4-byte header, then the descriptor as the drive sent it: bit fields in MMC order, sectors big-endian.
        var d = output.AsSpan(4);
        return new DvdLayers(
            Layers: ((d[2] >> 5) & 3) + 1,
            OppositeTrackPath: ((d[2] >> 4) & 1) == 1,
            StartSector: BinaryPrimitives.ReadUInt32BigEndian(d[4..]) & 0xFFFFFF,
            EndSector: BinaryPrimitives.ReadUInt32BigEndian(d[8..]) & 0xFFFFFF,
            EndLayer0Sector: BinaryPrimitives.ReadUInt32BigEndian(d[12..]) & 0xFFFFFF);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
    }
}

/// <summary>A DVD's physical format. Sector numbers are physical (data starts at 0x30000).</summary>
public sealed record DvdLayers(int Layers, bool OppositeTrackPath, uint StartSector, uint EndSector, uint EndLayer0Sector)
{
    /// <summary>The sectors on layer 0 of a two-layer disc.</summary>
    public long Layer0Sectors => (long)EndLayer0Sector - StartSector + 1;
}

/// <summary>How a <see cref="DiscDriveStream"/> fared: slow reads, retries and sectors it could not read.</summary>
public sealed class DiscReadLog
{
    public int Retries { get; set; }

    public int SlowReads { get; set; }

    public TimeSpan SlowestRead { get; set; }

    public List<long> BadSectors { get; } = [];

    /// <summary>Called with one line per event worth seeing: a slow read, a failed read, a retry.</summary>
    public Action<string>? Event { get; set; }
}

/// <summary>
/// The disc's 2048-byte sectors as a seekable stream. A failed read is retried, then retried sector by sector; a
/// sector that still fails stops the read, or with <see cref="ZeroFill"/> reads as zeros and is listed.
/// </summary>
public sealed class DiscDriveStream : Stream
{
    private const int WindowBytes = 256 * 1024;
    private const int ChunkAttempts = 3;
    private const int SectorAttempts = 5;
    private static readonly TimeSpan SlowRead = TimeSpan.FromSeconds(1);

    private readonly DiscDrive _drive;
    private readonly byte[] _window = GC.AllocateUninitializedArray<byte>(WindowBytes, pinned: true);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _windowStart = -1;
    private int _windowLength;
    private long _position;

    public DiscDriveStream(DiscDrive drive, long length)
    {
        _drive = drive;
        Length = length;
    }

    public DiscReadLog Log { get; } = new();

    public bool ZeroFill { get; init; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            if (_windowStart < 0 || _position < _windowStart || _position >= _windowStart + _windowLength)
            {
                Fill(_position / DiscDrive.SectorSize * DiscDrive.SectorSize);
            }

            var from = (int)(_position - _windowStart);
            var n = Math.Min(buffer.Length, _windowLength - from);
            _window.AsSpan(from, n).CopyTo(buffer);
            buffer = buffer[n..];
            _position += n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Fill(long start)
    {
        var length = (int)Math.Min(WindowBytes, Length - start);
        _windowStart = -1;
        for (var attempt = 1; attempt <= ChunkAttempts; attempt++)
        {
            if (TryRead(start, _window.AsSpan(0, length), out var error))
            {
                _windowStart = start;
                _windowLength = length;
                return;
            }

            Log.Retries++;
            Log.Event?.Invoke($"read of {length / DiscDrive.SectorSize} sectors at sector {start / DiscDrive.SectorSize} failed (try {attempt}): {error}");
        }

        // Find the sectors that fail, one at a time.
        for (var at = 0; at < length; at += DiscDrive.SectorSize)
        {
            var sector = (start + at) / DiscDrive.SectorSize;
            var read = false;
            string? error = null;
            for (var attempt = 1; attempt <= SectorAttempts && !read; attempt++)
            {
                read = TryRead(start + at, _window.AsSpan(at, DiscDrive.SectorSize), out error);
                if (!read)
                {
                    Log.Retries++;
                }
            }

            if (read)
            {
                continue;
            }

            if (!ZeroFill)
            {
                throw new IOException($"Sector {sector} of the disc in {_drive.Name} could not be read after {SectorAttempts} tries: {error}");
            }

            _window.AsSpan(at, DiscDrive.SectorSize).Clear();
            Log.BadSectors.Add(sector);
            Log.Event?.Invoke($"sector {sector} is unreadable; zeros in its place: {error}");
        }

        _windowStart = start;
        _windowLength = length;
    }

    private bool TryRead(long offset, Span<byte> destination, out string? error)
    {
        var started = _clock.Elapsed;
        try
        {
            var read = 0;
            while (read < destination.Length)
            {
                var n = RandomAccess.Read(_drive.Handle, destination[read..], offset + read);
                if (n == 0)
                {
                    throw new IOException($"the disc ended at byte {offset + read}");
                }

                read += n;
            }

            error = null;
            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            var took = _clock.Elapsed - started;
            if (took >= SlowRead)
            {
                Log.SlowReads++;
                Log.Event?.Invoke(string.Create(CultureInfo.InvariantCulture, $"slow read: {destination.Length / DiscDrive.SectorSize} sectors at sector {offset / DiscDrive.SectorSize} took {took.TotalSeconds:0.0} s"));
            }

            if (took > Log.SlowestRead)
            {
                Log.SlowestRead = took;
            }
        }
    }
}
