using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Loadout.SpeedTest;

namespace Loadout.Fakes;

/// <summary>
/// Loadout Bay's speed test on loopback: one TCP run and UDP runs, answered the
/// way <c>ps2/src/bench.c</c> answers them.
/// </summary>
public sealed class FakeSpeedTestBay : IDisposable
{
    private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
    private readonly Socket _udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _tcpTask;
    private readonly Task _udpTask;
    private int _endPacketsToIgnore;

    public FakeSpeedTestBay(int endPacketsToIgnore = 0, int tcpStatus = 0)
    {
        _endPacketsToIgnore = endPacketsToIgnore;
        TcpStatus = tcpStatus;
        _tcp.Start();
        _udp.ReceiveBufferSize = 16 * 1024 * 1024;
        _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _tcpTask = Task.Run(ServeTcpAsync);
        _udpTask = Task.Run(ServeUdpAsync);
    }

    public int TcpPort => ((IPEndPoint)_tcp.LocalEndpoint).Port;

    public int UdpPort => ((IPEndPoint)_udp.LocalEndPoint!).Port;

    public long TcpBytes { get; private set; }

    public uint TcpFlags { get; private set; }

    /// <summary>The status the fake reports for a TCP run, as Loadout Bay does for a disk error.</summary>
    public int TcpStatus { get; }

    public uint UdpPackets { get; private set; }

    public int EndPacketsSeen { get; private set; }

    public void Dispose()
    {
        _stop.Cancel();
        _tcp.Stop();
        _udp.Dispose();
        try
        {
            Task.WaitAll([_tcpTask, _udpTask], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Stopping the sockets ends the loops with an error. That is the expected way out.
        }

        _stop.Dispose();
    }

    private async Task ServeTcpAsync()
    {
        using var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
        var stream = client.GetStream();
        var header = new byte[SpeedTestWire.TcpHeaderBytes];
        await stream.ReadExactlyAsync(header, _stop.Token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != SpeedTestWire.TcpMagic)
        {
            return;
        }

        TcpFlags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        var buffer = new byte[64 * 1024];
        long received = 0;
        while (received < total)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, total - received)), _stop.Token);
            if (count == 0)
            {
                break;
            }

            received += count;
        }

        TcpBytes = received;
        var reply = new byte[SpeedTestWire.TcpReplyBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(reply, (ulong)received);
        BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(8), 1_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(16), TcpStatus);
        await stream.WriteAsync(reply, _stop.Token);
    }

    private async Task ServeUdpAsync()
    {
        var buffer = new byte[2048];
        uint run = 0;
        ulong bytes = 0;
        while (!_stop.IsCancellationRequested)
        {
            var result = await _udp.ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
            var datagram = buffer.AsSpan(0, result.ReceivedBytes);
            if (datagram.Length < SpeedTestWire.UdpHeaderBytes
                || BinaryPrimitives.ReadUInt32LittleEndian(datagram) != SpeedTestWire.UdpMagic)
            {
                continue;
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(datagram[4..]);
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]);
            if (sequence == SpeedTestWire.UdpEndSequence)
            {
                EndPacketsSeen++;
                if (_endPacketsToIgnore > 0)
                {
                    _endPacketsToIgnore--;
                    continue;
                }

                var reply = new byte[SpeedTestWire.UdpReplyBytes];
                BinaryPrimitives.WriteUInt32LittleEndian(reply, SpeedTestWire.ReplyMagic);
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), id);
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), UdpPackets);
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(datagram[12..]));
                BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(16), bytes);
                BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(24), 1_000_000);
                await _udp.SendToAsync(reply, result.RemoteEndPoint, _stop.Token);
                continue;
            }

            if (id != run)
            {
                run = id;
                UdpPackets = 0;
                bytes = 0;
            }

            UdpPackets++;
            bytes += (ulong)datagram.Length;
        }
    }
}
