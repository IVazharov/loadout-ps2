using System.Buffers.Binary;

namespace Loadout.SpeedTest;

/// <summary>
/// The speed test wire format shared with Loadout Bay (<c>ps2/src/bench.c</c>).
/// Every number is little-endian. <c>docs/speed-test.md</c> describes it.
/// </summary>
public static class SpeedTestWire
{
    public const int TcpPort = 45070;

    public const int UdpPort = 45071;

    public const uint TcpMagic = 0x3154424C; // "LBT1"

    public const uint UdpMagic = 0x3155424C; // "LBU1"

    public const uint ReplyMagic = 0x3152424C; // "LBR1"

    public const uint UdpEndSequence = 0xFFFFFFFF;

    public const int TcpHeaderBytes = 16;

    public const int TcpReplyBytes = 24;

    /// <summary>TCP header flag: Loadout Bay writes the data to a test partition on the hard disk.</summary>
    public const uint FlagDisk = 0x1;

    /// <summary>TCP header flag: Loadout Bay keeps the test partition after the run.</summary>
    public const uint FlagKeep = 0x2;

    /// <summary>Disk write sizes Loadout Bay accepts, in KiB. Each divides its 256 KiB block.</summary>
    public static readonly IReadOnlyList<int> WriteUnitsKib = [16, 32, 64, 128, 256];

    /// <summary>
    /// Flags for a disk run. <paramref name="writeUnitKib"/> goes in bits 16-31 and sets the size
    /// of each disk write; 0 keeps Loadout Bay's whole 256 KiB blocks.
    /// </summary>
    public static uint DiskFlags(bool keep, int writeUnitKib = 0)
    {
        if (writeUnitKib != 0 && !WriteUnitsKib.Contains(writeUnitKib))
        {
            throw new ArgumentOutOfRangeException(nameof(writeUnitKib), "The write size must be 16, 32, 64, 128, or 256 KiB.");
        }

        return FlagDisk | (keep ? FlagKeep : 0) | ((uint)writeUnitKib << 16);
    }

    public const int UdpHeaderBytes = 12;

    public const int UdpEndBytes = 16;

    public const int UdpReplyBytes = 32;

    /// <summary>The largest UDP payload that fits a 1500-byte Ethernet frame without fragments.</summary>
    public const int MaxDatagramBytes = 1472;

    public const double BytesPerMegabyte = 1024d * 1024d;

    public static void WriteTcpHeader(Span<byte> header, long totalBytes, uint flags = 0)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, TcpMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)totalBytes);
    }

    /// <summary>Bytes received, microseconds, and a status: 0, or a negative errno from Loadout Bay.</summary>
    public static (ulong Bytes, ulong Microseconds, int Status) ReadTcpReply(ReadOnlySpan<byte> reply)
        => (BinaryPrimitives.ReadUInt64LittleEndian(reply),
            BinaryPrimitives.ReadUInt64LittleEndian(reply[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(reply[16..]));

    public static void WriteUdpHeader(Span<byte> datagram, uint runId, uint sequence)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, UdpMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram[4..], runId);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram[8..], sequence);
    }

    public static void WriteUdpEnd(Span<byte> datagram, uint runId, uint packetsSent)
    {
        WriteUdpHeader(datagram, runId, UdpEndSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram[12..], packetsSent);
    }

    /// <summary>Reads a UDP reply. Null when the bytes are not a reply to <paramref name="runId"/>.</summary>
    public static UdpReply? ReadUdpReply(ReadOnlySpan<byte> reply, uint runId)
    {
        if (reply.Length < UdpReplyBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(reply) != ReplyMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]) != runId)
        {
            return null;
        }

        return new UdpReply(
            BinaryPrimitives.ReadUInt32LittleEndian(reply[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(reply[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(reply[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(reply[24..]));
    }

    public static double MegabytesPerSecond(double bytes, TimeSpan elapsed)
        => elapsed <= TimeSpan.Zero ? 0 : bytes / BytesPerMegabyte / elapsed.TotalSeconds;
}

/// <summary>What Loadout Bay counted for one UDP run. Bytes include the 12-byte packet headers.</summary>
public sealed record UdpReply(uint PacketsReceived, uint PacketsSent, ulong Bytes, ulong Microseconds);
