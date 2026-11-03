using System.Diagnostics;
using System.Net.Sockets;

namespace Loadout.SpeedTest;

/// <summary>What the PC measured and what Loadout Bay measured for one run.</summary>
public sealed record SpeedTestResult(
    string Transport,
    long BytesSent,
    TimeSpan PcElapsed,
    ulong Ps2Bytes,
    TimeSpan Ps2Elapsed,
    uint PacketsSent = 0,
    uint PacketsReceived = 0,
    int Status = 0)
{
    public double PcMegabytesPerSecond => SpeedTestWire.MegabytesPerSecond(BytesSent, PcElapsed);

    public double Ps2MegabytesPerSecond => SpeedTestWire.MegabytesPerSecond(Ps2Bytes, Ps2Elapsed);

    public double LossPercent => PacketsSent == 0 ? 0 : 100d * (PacketsSent - Math.Min(PacketsSent, PacketsReceived)) / PacketsSent;
}

public static class SpeedTestClient
{
    private const int TcpWriteBytes = 1024 * 1024;

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends <paramref name="totalBytes"/> over TCP and reads Loadout Bay's count, time, and status.
    /// With <see cref="SpeedTestWire.FlagDisk"/>, Loadout Bay writes the data to its hard disk.
    /// </summary>
    public static SpeedTestResult RunTcp(
        string host,
        int port,
        long totalBytes,
        uint flags = 0,
        Action<long>? progress = null,
        int seed = DefaultSeed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        if ((flags & SpeedTestWire.FlagDisk) != 0 && totalBytes % 512 != 0)
        {
            throw new ArgumentException("A disk run must be a whole number of 512-byte sectors.", nameof(totalBytes));
        }

        using var client = new TcpClient { NoDelay = true, SendBufferSize = 4 * TcpWriteBytes };
        client.Connect(host, port);
        client.ReceiveTimeout = (int)ReplyTimeout.TotalMilliseconds;
        var stream = client.GetStream();

        var header = new byte[SpeedTestWire.TcpHeaderBytes];
        SpeedTestWire.WriteTcpHeader(header, totalBytes, flags);
        stream.Write(header);

        var block = TcpBlock(seed);
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long sent = 0;
        while (sent < totalBytes)
        {
            var count = (int)Math.Min(block.Length, totalBytes - sent);
            stream.Write(block, 0, count);
            sent += count;
            if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
            {
                lastReport = clock.Elapsed;
                progress?.Invoke(sent);
            }
        }

        var reply = new byte[SpeedTestWire.TcpReplyBytes];
        stream.ReadExactly(reply);
        clock.Stop();
        var (bytes, microseconds, status) = SpeedTestWire.ReadTcpReply(reply);
        var transport = (flags & SpeedTestWire.FlagDisk) != 0 ? "TCP to HDD" : "TCP";
        return new SpeedTestResult(transport, totalBytes, clock.Elapsed, bytes, FromMicroseconds(microseconds), Status: status);
    }

    /// <summary>
    /// Sends <paramref name="totalBytes"/> of UDP payload at <paramref name="megabytesPerSecond"/>
    /// (0 sends as fast as the PC can), then asks Loadout Bay what arrived.
    /// </summary>
    public static SpeedTestResult RunUdp(
        string host,
        int port,
        long totalBytes,
        double megabytesPerSecond,
        int datagramBytes = SpeedTestWire.MaxDatagramBytes,
        Action<long>? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(megabytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(datagramBytes, SpeedTestWire.UdpEndBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(datagramBytes, SpeedTestWire.MaxDatagramBytes);

        using var udp = new UdpClient();
        udp.Client.SendBufferSize = 4 * 1024 * 1024;
        udp.Connect(host, port);
        var runId = (uint)Random.Shared.Next(1, int.MaxValue);
        var datagram = Filler(datagramBytes);
        var payload = datagramBytes - SpeedTestWire.UdpHeaderBytes;
        var packets = (uint)((totalBytes + payload - 1) / payload);
        var bytesPerSecond = megabytesPerSecond * SpeedTestWire.BytesPerMegabyte;

        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long sent = 0;
        for (uint sequence = 0; sequence < packets; sequence++)
        {
            SpeedTestWire.WriteUdpHeader(datagram, runId, sequence);
            udp.Client.Send(datagram);
            sent += datagramBytes;
            if (bytesPerSecond > 0)
            {
                Pace(clock, sent / bytesPerSecond);
            }

            if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
            {
                lastReport = clock.Elapsed;
                progress?.Invoke(sent);
            }
        }

        clock.Stop();
        var reply = AskForUdpReply(udp, runId, packets);
        return new SpeedTestResult(
            "UDP",
            sent,
            clock.Elapsed,
            reply.Bytes,
            FromMicroseconds(reply.Microseconds),
            packets,
            reply.PacketsReceived);
    }

    // The end packet can be lost like any other, so it is sent until a reply comes.
    private static UdpReply AskForUdpReply(UdpClient udp, uint runId, uint packets)
    {
        var end = new byte[SpeedTestWire.UdpEndBytes];
        SpeedTestWire.WriteUdpEnd(end, runId, packets);
        udp.Client.ReceiveTimeout = 1000;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            udp.Client.Send(end);
            try
            {
                var buffer = new byte[64];
                var count = udp.Client.Receive(buffer);
                var reply = SpeedTestWire.ReadUdpReply(buffer.AsSpan(0, count), runId);
                if (reply is not null)
                {
                    return reply;
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
            {
            }
        }

        throw new TimeoutException("Loadout Bay did not answer the end of the UDP run.");
    }

    // Waits until the clock reaches the time the bytes sent so far should take.
    private static void Pace(Stopwatch clock, double dueSeconds)
    {
        while (clock.Elapsed.TotalSeconds < dueSeconds)
        {
            if (dueSeconds - clock.Elapsed.TotalSeconds > 0.002)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private static TimeSpan FromMicroseconds(ulong microseconds) => TimeSpan.FromMicroseconds(microseconds);

    public const int DefaultSeed = 1;

    /// <summary>
    /// The block a TCP run repeats until the total is sent. A different seed gives
    /// different bytes, so a disk check cannot pass on data an earlier run left behind.
    /// </summary>
    public static byte[] TcpBlock(int seed = DefaultSeed) => Filler(TcpWriteBytes, seed);

    // Bytes that do not compress, in case anything on the path compresses.
    private static byte[] Filler(int length, int seed = DefaultSeed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
