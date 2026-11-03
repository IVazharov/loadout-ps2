using System.Net;
using System.Net.Sockets;
using Loadout.Fakes;

namespace Loadout.Bay;

public sealed class LoadoutBayClientTests
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    [Fact]
    public async Task TheGreetingNamesTheVersionAndTheDisk()
    {
        using var bay = new FakeLoadoutBay(version: "0.2.0", disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        Assert.Equal(BayWire.ProtocolVersion, client.Hello.ProtocolVersion);
        Assert.Equal("0.2.0", client.Hello.Version);
        Assert.True(client.Hello.HasHardDisk);
    }

    [Fact]
    public async Task FreeSpaceComesInSectorsAndBytes()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var space = await client.GetFreeSpaceAsync();

        Assert.Equal(Disk, space);
        Assert.Equal(0x04000000UL * 512, space.FreeBytes);
        Assert.Equal([BayWire.CommandHello, BayWire.CommandFreeSpace], bay.Commands);
    }

    [Fact]
    public async Task APs2WithoutAHardDiskSaysSo()
    {
        using var bay = new FakeLoadoutBay(disk: null);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        Assert.False(client.Hello.HasHardDisk);
        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.GetFreeSpaceAsync());
        Assert.Equal(BayWire.StatusNoDevice, error.Status);
        Assert.Equal("Loadout Bay found no usable hard disk.", error.Message);
    }

    [Fact]
    public async Task AnotherProtocolVersionIsRefusedByName()
    {
        using var bay = new FakeLoadoutBay(protocolVersion: BayWire.ProtocolVersion + 1, version: "0.9.0");

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port));

        Assert.Equal(BayWire.StatusProtocolMismatch, error.Status);
        Assert.Contains($"Loadout Bay 0.9.0 speaks protocol {BayWire.ProtocolVersion + 1}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyThatIsNotLoadoutBaysIsRefused()
    {
        using var bay = new FakeLoadoutBay(replyMagic: 0x12345678);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port));

        Assert.Equal("The reply did not come from Loadout Bay.", error.Message);
    }

    [Fact]
    public async Task NobodyListeningIsReportedAsUnreachable()
    {
        // A port that was free a moment ago, so nothing answers on it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => LoadoutBayClient.ConnectAsync("127.0.0.1", port));

        Assert.StartsWith("Loadout Bay could not be reached at 127.0.0.1, port " + port, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("..", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStatusHasASentence()
    {
        Assert.Equal("Loadout Bay failed with error -999.", BayWire.DescribeStatus(-999));
        Assert.EndsWith(".", BayWire.DescribeStatus(BayWire.StatusIo), StringComparison.Ordinal);
    }
}
