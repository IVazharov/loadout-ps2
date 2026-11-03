using System.Buffers.Binary;
using System.Text;
using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayInstallTests
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private static readonly BayInstallRequest MoonWars =
        new("SLUS-20043", "SLUS_200.43", "Moon Wars - Super Rocket Racing", 10, 0, HdlDiscTypes.Ps2Cd);

    [Fact]
    public async Task AGameIsSentWholeAndThenEnded()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        var image = Sectors(10);
        var reports = new List<BayInstallStatus>();

        var start = await client.InstallAsync(MoonWars, new MemoryStream(image), new Recorder(reports), bay.DataPort);

        Assert.Equal("PP.SLUS-20043.HDL.FAKE", start.PartitionName);
        Assert.Equal(image, bay.Installed);
        Assert.Equal(BayWire.CommandInstallEnd, bay.Commands[^1]);
        Assert.Equal(new BayInstallStatus(20480, 20480, 20480), reports[^1]);
    }

    [Fact]
    public async Task InstallBeginCarriesTheGameAsLoadoutBayReadsIt()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        await client.InstallAsync(MoonWars with { Layer1Sectors = 5 }, new MemoryStream(Sectors(15)), dataPort: bay.DataPort);

        var request = bay.InstallRequests.Single();
        Assert.Equal("SLUS-20043", Text(request, 0, BayWire.DiscIdBytes));
        Assert.Equal("SLUS_200.43", Text(request, 16, BayWire.StartupBytes));
        Assert.Equal("Moon Wars - Super Rocket Racing", Text(request, 76, BayWire.TitleBytes));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(236)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(240)));
        Assert.Equal([HdlDiscTypes.Ps2Cd, 0, 0x40, 4], request[244..248]);
    }

    [Fact]
    public void ALongTitleIsCutBetweenCharacters()
    {
        var title = new string('a', BayWire.TitleBytes - 1) + "é";

        var request = BayWire.WriteInstallBegin(MoonWars with { Title = title });

        Assert.Equal(new string('a', BayWire.TitleBytes - 1), Text(request, 76, BayWire.TitleBytes));
    }

    [Fact]
    public async Task ARefusedInstallSendsNoData()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { InstallBeginStatus = BayWire.StatusExists };
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(
            () => client.InstallAsync(MoonWars, new MemoryStream(Sectors(10)), dataPort: bay.DataPort));

        Assert.Equal(BayWire.StatusExists, error.Status);
        Assert.DoesNotContain(BayWire.CommandInstallEnd, bay.Commands);
    }

    [Fact]
    public async Task AnImageShorterThanItsSectorsIsEndedButNotInstalled()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(
            () => client.InstallAsync(MoonWars, new MemoryStream(Sectors(7)), dataPort: bay.DataPort));

        Assert.Equal(BayWire.StatusBrokenPipe, error.Status);
        Assert.Contains("image ended", error.Message, StringComparison.Ordinal);
        Assert.Equal(BayWire.CommandInstallEnd, bay.Commands[^1]);
    }

    [Fact]
    public async Task ACancelledInstallIsStillEndedSoItGetsNoHeader()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        using var cancel = new CancellationTokenSource();
        var image = new CancellingStream(Sectors(10), 4 * 2048, cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.InstallAsync(MoonWars, image, dataPort: bay.DataPort, cancellationToken: cancel.Token));

        Assert.Equal(BayWire.CommandInstallEnd, bay.Commands[^1]);
        Assert.True(bay.Installed.Length < 10 * 2048);
        // The control connection is still in step after the cancelled install.
        Assert.Equal(Disk, await client.GetFreeSpaceAsync());
    }

    [Fact]
    public async Task ACheckedInstallSendsAChecksumPerMiBAfterTheData()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        // Three MiB and five sectors, so the last checksum covers a part MiB.
        var image = Sectors(3 * 512 + 5);
        var reports = new List<BayInstallStatus>();

        await client.InstallAsync(MoonWars with { Layer0Sectors = 3 * 512 + 5, CheckCopy = true }, new MemoryStream(image), new Recorder(reports), bay.DataPort);

        Assert.Equal(1, bay.Checked);
        Assert.Equal(BayWire.InstallFlagCheck, BinaryPrimitives.ReadUInt32LittleEndian(bay.InstallRequests.Single().AsSpan(248)));
        Assert.Contains(reports, report => report.Checking);
    }

    [Fact]
    public async Task ACopyThatDiffersOnThePs2IsNotInstalledAndSaysWhere()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { CorruptByteAt = (2 * 1024 * 1024) + 100 };
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(
            () => client.InstallAsync(MoonWars with { Layer0Sectors = 3 * 512, CheckCopy = true }, new MemoryStream(Sectors(3 * 512)), dataPort: bay.DataPort));

        Assert.Equal(BayWire.StatusCopyDiffers, error.Status);
        Assert.Contains("at 2 MiB", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, bay.Checked);
    }

    [Fact]
    public async Task WithoutTheCheckNoChecksumsAreSent()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { CorruptByteAt = 100 };
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        await client.InstallAsync(MoonWars, new MemoryStream(Sectors(10)), dataPort: bay.DataPort);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bay.InstallRequests.Single().AsSpan(248)));
        Assert.Equal(0, bay.Checked);
    }

    [Fact]
    public void TheChecksumIsCrc32C()
    {
        Assert.Equal(0xE3069283u, Crc32C.Of("123456789"u8));
        var data = Sectors(3);
        Assert.Equal(Crc32C.Of(data), Crc32C.Finish(Crc32C.Update(Crc32C.Update(Crc32C.Start, data.AsSpan(0, 1000)), data.AsSpan(1000))));
    }

    private static byte[] Sectors(int count)
    {
        var bytes = new byte[count * 2048];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    private static string Text(byte[] payload, int offset, int length)
    {
        var field = payload.AsSpan(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? field[..end] : field);
    }

    // Reports on the calling thread, so the list is complete when the install returns.
    private sealed class Recorder(List<BayInstallStatus> reports) : IProgress<BayInstallStatus>
    {
        public void Report(BayInstallStatus value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    // Hands out its bytes, and cancels once a given number has been read.
    private sealed class CancellingStream(byte[] bytes, int cancelAfter, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= cancelAfter)
            {
                await cancel.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, cancelAfter - Math.Min(Position, cancelAfter) + 2048)], cancellationToken);
        }
    }
}
