using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Loadout.Bay;

public sealed class BayDiscoveryTests
{
    [Fact]
    public void TheProbeCarriesTheMagicAndTheProtocol()
    {
        var probe = BayDiscovery.Probe();

        Assert.Equal(BayDiscovery.ProbeBytes, probe.Length);
        Assert.Equal("LBD1", Encoding.ASCII.GetString(probe, 0, 4));
        Assert.Equal(BayWire.ProtocolVersion, BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(4)));
    }

    [Fact]
    public void AnAnswerNamesTheVersionAndTheDisk()
    {
        var from = IPAddress.Parse("10.0.0.55");

        var bay = BayDiscovery.ReadAnswer(Answer("0.9.0", disk: true), from);

        Assert.Equal(new FoundBay(from, "0.9.0", BayWire.ProtocolVersion, HasHardDisk: true), bay);
    }

    [Fact]
    public void OtherDatagramsAreNoAnswer()
    {
        var from = IPAddress.Parse("10.0.0.55");
        var wrongMagic = Answer("0.9.0", disk: false);
        wrongMagic[0] ^= 0xFF;

        Assert.Null(BayDiscovery.ReadAnswer(wrongMagic, from));
        Assert.Null(BayDiscovery.ReadAnswer(Answer("0.9.0", disk: false).AsSpan(1), from));
        Assert.Null(BayDiscovery.ReadAnswer(BayDiscovery.Probe(), from));
    }

    [Theory]
    [InlineData("192.168.1.20", 24, "192.168.1.255")]
    [InlineData("10.0.0.103", 23, "10.0.1.255")]
    [InlineData("172.16.5.9", 16, "172.16.255.255")]
    public void TheBroadcastAddressSetsTheHostBits(string address, int prefix, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), BayDiscovery.Broadcast(new LocalNetwork(IPAddress.Parse(address), prefix)));
    }

    [Fact]
    public void ASweepCoversTheSubnetWithoutItsNetworkAndBroadcastAddresses()
    {
        var hosts = BayDiscovery.SweepHosts(new LocalNetwork(IPAddress.Parse("10.0.0.103"), 23)).ToList();

        Assert.Equal(510, hosts.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("10.0.1.254"), hosts[^1]);
    }

    [Fact]
    public void ALargeSubnetIsSweptInTheSlash24AroundThePc()
    {
        var hosts = BayDiscovery.SweepHosts(new LocalNetwork(IPAddress.Parse("172.16.32.1"), 20)).ToList();

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("172.16.32.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("172.16.32.254"), hosts[^1]);
    }

    [Theory]
    [InlineData(30, 2)]
    [InlineData(31, 0)]
    [InlineData(32, 0)]
    public void ATinySubnetHasFewOrNoHosts(int prefix, int expected)
    {
        Assert.Equal(expected, BayDiscovery.SweepHosts(new LocalNetwork(IPAddress.Parse("192.168.9.5"), prefix)).Count());
    }

    [Fact]
    public void TheProbeGoesToTheSubnetAndToEveryone()
    {
        var local = IPAddress.Parse("172.16.77.1");

        var targets = BayDiscovery.ProbeTargets([new LocalNetwork(local, 24)]);

        Assert.Equal([(local, IPAddress.Parse("172.16.77.255")), (local, IPAddress.Broadcast)], targets);
    }

    [Fact]
    public async Task AProbeCollectsTheAnswer()
    {
        using var bay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)bay.Client.LocalEndPoint!).Port;
        var answering = AnswerOnceAsync(bay);

        var found = await BayDiscovery.ProbeAsync([(IPAddress.Loopback, IPAddress.Loopback)], port, TimeSpan.FromSeconds(1));

        await answering;
        var only = Assert.Single(found);
        Assert.Equal(IPAddress.Loopback, only.Address);
        Assert.Equal("0.9.0", only.Version);
    }

    [Fact]
    public async Task AProbeWithNoBayFindsNothing()
    {
        using var nobody = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)nobody.Client.LocalEndPoint!).Port;
        nobody.Close();

        var found = await BayDiscovery.ProbeAsync([(IPAddress.Loopback, IPAddress.Loopback)], port, TimeSpan.FromMilliseconds(500));

        Assert.Empty(found);
    }

    [Fact]
    public void ASweepRangeNamesItsSubnetAndWhetherItWasCut()
    {
        var whole = BayDiscovery.SweepRangeOf(new LocalNetwork(IPAddress.Parse("10.0.0.103"), 23));
        var cut = BayDiscovery.SweepRangeOf(new LocalNetwork(IPAddress.Parse("172.16.32.9"), 20));

        Assert.Equal(new SweepRange(IPAddress.Parse("10.0.0.0"), 23, IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.1.254"), false), whole);
        Assert.Equal(new SweepRange(IPAddress.Parse("172.16.32.0"), 24, IPAddress.Parse("172.16.32.1"), IPAddress.Parse("172.16.32.254"), true), cut);
        Assert.Null(BayDiscovery.SweepRangeOf(new LocalNetwork(IPAddress.Parse("192.168.9.5"), 31)));
    }

    [Fact]
    public async Task ASweepReportsItsStartAndItsEnd()
    {
        // 127.0.0.0/29 without the PC's own 127.0.0.5: five addresses, where nothing listens.
        var reports = new List<SweepProgress>();
        var progress = new Recorder(reports);

        var found = await BayDiscovery.SweepAsync([new LocalNetwork(IPAddress.Parse("127.0.0.5"), 29)], progress);

        Assert.Empty(found);
        Assert.Equal(new SweepProgress(0, 5), reports[0]);
        Assert.Equal(new SweepProgress(5, 5), reports[^1]);
    }

    [Fact]
    public async Task ACancelledSweepStops()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BayDiscovery.SweepAsync([new LocalNetwork(IPAddress.Parse("127.0.0.5"), 29)], null, cancel.Token));
    }

    // Reports on the caller's thread, unlike Progress<T>, so the list is complete when the sweep ends.
    private sealed class Recorder(List<SweepProgress> reports) : IProgress<SweepProgress>
    {
        public void Report(SweepProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    private static async Task AnswerOnceAsync(UdpClient bay)
    {
        var probe = await bay.ReceiveAsync();
        Assert.Equal(BayDiscovery.Probe(), probe.Buffer);
        await bay.SendAsync(Answer("0.9.0", disk: true), probe.RemoteEndPoint);
    }

    private static byte[] Answer(string version, bool disk)
    {
        var answer = new byte[BayDiscovery.AnswerBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(answer, BayDiscovery.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(4), BayWire.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(8), disk ? 1u : 0u);
        Encoding.UTF8.GetBytes(version).CopyTo(answer, 12);
        return answer;
    }
}
