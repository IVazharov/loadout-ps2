namespace Loadout.SpeedTest;

public class SpeedTestClientTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact(Timeout = 20000)]
    public async Task ATcpRunSendsEveryByteAndReportsWhatTheConsoleCounted()
    {
        using var bay = new FakeSpeedTestBay();

        var result = await Task.Run(() => SpeedTestClient.RunTcp("127.0.0.1", bay.TcpPort, 3 * Megabyte));

        Assert.Equal(3 * Megabyte, bay.TcpBytes);
        Assert.Equal((ulong)(3 * Megabyte), result.Ps2Bytes);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Ps2Elapsed);
        Assert.Equal(3d, result.Ps2MegabytesPerSecond, 3);
        Assert.Equal(0u, bay.TcpFlags);
        Assert.Equal(0, result.Status);
    }

    [Fact(Timeout = 20000)]
    public async Task ADiskRunSendsTheDiskAndKeepFlagsAndReportsTheConsoleStatus()
    {
        using var bay = new FakeSpeedTestBay(tcpStatus: -28);

        var result = await Task.Run(() => SpeedTestClient.RunTcp(
            "127.0.0.1",
            bay.TcpPort,
            Megabyte,
            SpeedTestWire.FlagDisk | SpeedTestWire.FlagKeep));

        Assert.Equal(SpeedTestWire.FlagDisk | SpeedTestWire.FlagKeep, bay.TcpFlags);
        Assert.Equal("TCP to HDD", result.Transport);
        Assert.Equal(-28, result.Status);
    }

    [Fact]
    public void TheWriteSizeGoesInTheUpperHalfOfTheFlags()
    {
        Assert.Equal(SpeedTestWire.FlagDisk, SpeedTestWire.DiskFlags(keep: false));
        Assert.Equal(SpeedTestWire.FlagDisk | SpeedTestWire.FlagKeep | (64u << 16), SpeedTestWire.DiskFlags(keep: true, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedTestWire.DiskFlags(keep: false, 48));
    }

    [Fact]
    public void ADiskRunMustBeWholeSectors()
    {
        Assert.Throws<ArgumentException>(() => SpeedTestClient.RunTcp("127.0.0.1", 1, 1000, SpeedTestWire.FlagDisk));
    }

    [Fact(Timeout = 20000)]
    public async Task AUdpRunReportsThePacketsThatArrivedAndTheLoss()
    {
        using var bay = new FakeSpeedTestBay();

        var result = await Task.Run(() => SpeedTestClient.RunUdp("127.0.0.1", bay.UdpPort, Megabyte, megabytesPerSecond: 20));

        var payload = SpeedTestWire.MaxDatagramBytes - SpeedTestWire.UdpHeaderBytes;
        Assert.Equal((uint)((Megabyte + payload - 1) / payload), result.PacketsSent);
        Assert.Equal(bay.UdpPackets, result.PacketsReceived);
        Assert.InRange(result.PacketsReceived, 1u, result.PacketsSent);
        Assert.Equal((double)(result.PacketsSent - result.PacketsReceived) * 100 / result.PacketsSent, result.LossPercent, 6);
    }

    [Fact(Timeout = 20000)]
    public async Task ALostEndPacketIsSentAgainUntilTheConsoleAnswers()
    {
        using var bay = new FakeSpeedTestBay(endPacketsToIgnore: 2);

        var result = await Task.Run(() => SpeedTestClient.RunUdp("127.0.0.1", bay.UdpPort, 64 * 1024, megabytesPerSecond: 0));

        // Three at least, as two go unanswered. A reply slower than the client's wait brings one more, up to its limit of ten.
        Assert.InRange(bay.EndPacketsSeen, 3, 10);
        Assert.Equal(bay.UdpPackets, result.PacketsReceived);
    }

    [Fact]
    public void AReplyForAnotherRunIsIgnored()
    {
        var reply = new byte[SpeedTestWire.UdpReplyBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply, SpeedTestWire.ReplyMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 7);

        Assert.Null(SpeedTestWire.ReadUdpReply(reply, 8));
        Assert.NotNull(SpeedTestWire.ReadUdpReply(reply, 7));
    }
}
