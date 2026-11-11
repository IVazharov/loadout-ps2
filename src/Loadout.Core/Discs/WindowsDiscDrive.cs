using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Loadout.Discs;

/// <summary>
/// A disc in a PC's optical drive, opened for reading its 2048-byte sectors. It needs no admin rights: Windows lets any
/// user read a disc's sectors through the drive's volume (\\.\E:).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiscDrive : IDiscReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlCdromGetConfiguration = 0x00024058;
    private const uint IoctlDvdReadStructure = 0x00335140;
    private const uint IoctlStorageEjectMedia = 0x002D4808;
    private const uint GenericWrite = 0x40000000;
    private const uint IoctlScsiPassThrough = 0x0004D004;

    // SCSI_PASS_THROUGH on x64 is 56 bytes; 32 bytes of sense follow, then the data.
    private const int SenseOffset = 56;
    private const int DataOffset = 88;

    // A buffered pass-through moves at most 64 KB: 27 raw CD sectors per READ CD.
    private const int RawSectorsPerCommand = 27;

    private readonly SafeFileHandle _handle;
    private SafeFileHandle? _passThrough;
    private byte[]? _passThroughBuffer;

    private WindowsDiscDrive(string name, SafeFileHandle handle)
    {
        Name = name;
        _handle = handle;
    }

    public string Name { get; }

    public long Sectors { get; private init; }

    public DiscMedia Media { get; private init; }

    public int Layers { get; private init; }

    public long Layer0Sectors { get; private init; }

    public int CopySectorSize => Media == DiscMedia.Cd ? RawCdSectorSize : DiscSectors.Size;

    public bool TryReadForCopy(long sector, Span<byte> destination, out string? failure) =>
        Media == DiscMedia.Cd ? TryReadRawCd(sector, destination, out failure) : TryRead(sector, destination, out failure);

    /// <summary>The drive's current MMC profile (0x08 CD-ROM, 0x10 DVD-ROM, ...), or null when it did not say.</summary>
    public int? Profile { get; private init; }

    /// <summary>A drive letter such as E, E: or E:\.</summary>
    public static bool IsDriveName(string text) =>
        text.Length is >= 1 and <= 3 && char.IsAsciiLetter(text[0]) && (text.Length == 1 || text[1] == ':') && (text.Length < 3 || text[2] == '\\');

    /// <summary>Opens the disc in a drive. Throws <see cref="IOException"/> when the drive has no readable disc.</summary>
    public static WindowsDiscDrive Open(string driveName)
    {
        if (!IsDriveName(driveName))
        {
            throw new ArgumentException($"'{driveName}' is not a drive letter.", nameof(driveName));
        }

        var letter = char.ToUpperInvariant(driveName[0]);
        var handle = NativeMethods.CreateFile($@"\\.\{letter}:", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Drive {letter}: could not be opened: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).");
        }

        try
        {
            // Without this, Windows may stop reading where the disc's file system says the volume ends, which on a
            // two-layer DVD is the end of layer 0.
            _ = NativeMethods.DeviceIoControl(handle, FsctlAllowExtendedDasdIo, null, 0, null, 0, out _, IntPtr.Zero);
            var length = new byte[8];
            if (!NativeMethods.DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, length, length.Length, out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException($"Drive {letter}: has no readable disc: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).");
            }

            var profile = ReadProfile(handle);
            var (layers, layer0) = ReadLayers(handle);
            return new WindowsDiscDrive($"{letter}:", handle)
            {
                Sectors = BinaryPrimitives.ReadInt64LittleEndian(length) / DiscSectors.Size,
                Profile = profile,
                Media = profile switch
                {
                    >= 0x08 and <= 0x0A => DiscMedia.Cd,
                    >= 0x10 and <= 0x2F => DiscMedia.Dvd,
                    _ => DiscMedia.Unknown,
                },
                Layers = layers,
                Layer0Sectors = layer0,
            };
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool TryRead(long sector, Span<byte> destination, out string? failure)
    {
        try
        {
            var read = 0;
            while (read < destination.Length)
            {
                var n = RandomAccess.Read(_handle, destination[read..], (sector * DiscSectors.Size) + read);
                if (n == 0)
                {
                    failure = "the disc ended";
                    return false;
                }

                read += n;
            }

            failure = null;
            return true;
        }
        catch (IOException ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>A CD sector as it is on the disc: sync, header, subheader, data and error correction.</summary>
    public const int RawCdSectorSize = 2352;

    public void Dispose()
    {
        _passThrough?.Dispose();
        _handle.Dispose();
    }

    /// <summary>
    /// Reads whole CD sectors raw, 2352 bytes each, as Redump keeps a CD in its .bin, with READ CD through SCSI
    /// pass-through (which needs the drive opened for writing too; Windows allows that without admin rights). No retry of
    /// its own. Returns false with the drive's answer when any of them could not be read.
    /// </summary>
    public bool TryReadRawCd(long sector, Span<byte> destination, out string? failure)
    {
        if (_passThrough is null)
        {
            var handle = NativeMethods.CreateFile($@"\\.\{Name}", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                failure = $"the drive could not be opened for raw reads: {Marshal.GetPInvokeErrorMessage(error)} (error {error})";
                return false;
            }

            _passThrough = handle;
            _passThroughBuffer = new byte[DataOffset + (RawSectorsPerCommand * RawCdSectorSize)];
        }

        var buffer = _passThroughBuffer!;
        var total = destination.Length / RawCdSectorSize;
        for (var done = 0; done < total; done += RawSectorsPerCommand)
        {
            var count = Math.Min(RawSectorsPerCommand, total - done);
            var bytes = count * RawCdSectorSize;
            Array.Clear(buffer, 0, DataOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, SenseOffset);
            buffer[6] = 12; // CDB length
            buffer[7] = 32; // sense length
            buffer[8] = 1; // SCSI_IOCTL_DATA_IN
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 60); // timeout, seconds
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), DataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), SenseOffset);
            var cdb = buffer.AsSpan(36, 12);
            cdb[0] = 0xBE; // READ CD, any sector type
            BinaryPrimitives.WriteUInt32BigEndian(cdb[2..], (uint)(sector + done));
            cdb[6] = (byte)(count >> 16);
            cdb[7] = (byte)(count >> 8);
            cdb[8] = (byte)count;
            cdb[9] = 0xF8; // sync, all headers, user data, EDC/ECC: 2352 bytes
            if (!NativeMethods.DeviceIoControl(_passThrough, IoctlScsiPassThrough, buffer, DataOffset, buffer, DataOffset + bytes, out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                failure = $"{Marshal.GetPInvokeErrorMessage(error)} (error {error})";
                return false;
            }

            if (buffer[2] != 0)
            {
                failure = $"the drive answered SCSI status 0x{buffer[2]:X2}, sense {buffer[SenseOffset + 2] & 0x0F:X}/{buffer[SenseOffset + 12]:X2}/{buffer[SenseOffset + 13]:X2}";
                return false;
            }

            buffer.AsSpan(DataOffset, bytes).CopyTo(destination[(done * RawCdSectorSize)..]);
        }

        failure = null;
        return true;
    }

    /// <summary>Opens the drive's tray. Returns false when the drive would not (no tray, or the disc is in use).</summary>
    public static bool TryEject(string driveName)
    {
        if (!IsDriveName(driveName))
        {
            return false;
        }

        using var handle = NativeMethods.CreateFile($@"\\.\{char.ToUpperInvariant(driveName[0])}:", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return !handle.IsInvalid && NativeMethods.DeviceIoControl(handle, IoctlStorageEjectMedia, null, 0, null, 0, out _, IntPtr.Zero);
    }

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

    private static (int Layers, long Layer0Sectors) ReadLayers(SafeFileHandle handle)
    {
        // DVD_READ_STRUCTURE: byte offset 0, format 0 (physical descriptor), every session, layer 0; 24 bytes on x64.
        var input = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 0xFFFFFFFF);
        var output = new byte[2052];
        if (!NativeMethods.DeviceIoControl(handle, IoctlDvdReadStructure, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 4 + 17)
        {
            return (0, 0);
        }

        // A 4-byte header, then the descriptor as the drive sent it: bit fields in MMC order, sectors big-endian and
        // physical (data starts at 0x30000).
        var d = output.AsSpan(4);
        var layers = ((d[2] >> 5) & 3) + 1;
        var start = BinaryPrimitives.ReadUInt32BigEndian(d[4..]) & 0xFFFFFF;
        var endLayer0 = BinaryPrimitives.ReadUInt32BigEndian(d[12..]) & 0xFFFFFF;
        return (layers, layers > 1 ? (long)endLayer0 - start + 1 : 0);
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
