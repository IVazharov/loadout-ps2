using Loadout.Fakes;

namespace Loadout.Bay;

public sealed class BayFileTests
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    [Fact]
    public async Task AFileLongerThanOneChunkIsWrittenAndReadBackWhole()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        var content = new byte[(2 * BayWire.FileChunkMaxBytes) + 1234];
        new Random(7).NextBytes(content);
        var sent = new List<long>();

        await client.WriteFileAsync(BayFolder.Art, "SLUS_202.97_COV.png", content, new SynchronousProgress(sent.Add));

        Assert.Equal(content, await client.ReadFileAsync(BayFolder.Art, "SLUS_202.97_COV.png"));
        Assert.Equal([new BayFile("SLUS_202.97_COV.png", (uint)content.Length)], await client.ListFilesAsync(BayFolder.Art));
        Assert.Empty(await client.ListFilesAsync(BayFolder.Cfg));
        Assert.Equal([BayWire.FileChunkMaxBytes, 2L * BayWire.FileChunkMaxBytes, content.Length], sent);
    }

    [Fact]
    public async Task ATransferCutShortLeavesTheOldFileWhole()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = "$Compatibility=1\r\n"u8.ToArray();
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WriteFileAsync(
            BayFolder.Cfg, "SLUS_202.97.cfg", new byte[BayWire.FileChunkMaxBytes + 1], new SynchronousProgress(_ => stop.Cancel()), stop.Token));

        Assert.Equal("$Compatibility=1\r\n"u8.ToArray(), bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")]);
        Assert.Equal(BayWire.FileChunkMaxBytes, bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg.part")].Length);
    }

    [Fact]
    public async Task AFileIsDeletedAndAMissingOneSaysSo()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")] = [1, 2, 3];
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        await client.DeleteFileAsync(BayFolder.Art, "SLUS_202.97_ICO.png");
        var missing = await Assert.ThrowsAsync<LoadoutBayException>(() => client.DeleteFileAsync(BayFolder.Art, "SLUS_202.97_ICO.png"));

        Assert.Empty(bay.Files);
        Assert.Equal(BayWire.StatusNotFound, missing.Status);
        Assert.Contains("ART/SLUS_202.97_ICO.png", missing.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../conf_hdd.cfg")]
    [InlineData("ART/x.png")]
    [InlineData("name with space.png")]
    public async Task ANameOutsideTheRulesIsRefusedBeforeItIsSent(string name)
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteFileAsync(BayFolder.Art, name, new byte[1]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteFileAsync(BayFolder.Art, name));
        Assert.DoesNotContain(BayWire.CommandFileWrite, bay.Commands);
        Assert.False(BayWire.IsFileName(new string('a', BayWire.FileNameMaxBytes + 1)));
    }

    [Fact]
    public async Task WithoutOplsPartitionTheFileCommandsSaySo()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { HasOplPartition = false };
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.ListFilesAsync(BayFolder.Art));

        Assert.Equal(BayWire.StatusNotFound, error.Status);
        Assert.Contains("no OPL partition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemoryCardIsNeverReplacedByAWriteOnlyAfterADelete()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")] = [1, 2, 3];
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var refused = await Assert.ThrowsAsync<LoadoutBayException>(() => client.WriteFileAsync(BayFolder.Vmc, "SLUS_202.97_0.bin", new byte[10]));
        Assert.Equal(BayWire.StatusExists, refused.Status);
        Assert.Equal([1, 2, 3], bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")]);

        await client.DeleteFileAsync(BayFolder.Vmc, "SLUS_202.97_0.bin");
        await client.WriteFileAsync(BayFolder.Vmc, "SLUS_202.97_0.bin", new byte[10]);
        Assert.Equal(new byte[10], bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")]);
    }

    [Fact]
    public async Task OnlyBinFilesGoInTheMemoryCardFolder()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.WriteFileAsync(BayFolder.Vmc, "SLUS_202.97.cfg", new byte[1]));

        Assert.Equal(BayWire.StatusNotPermitted, error.Status);
        Assert.Empty(bay.Files);
    }

    [Fact]
    public async Task TheBlockRunsOfAFileAreAskedFor()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")] = [1];
        bay.FileRuns[(BayFolder.Vmc, "SLUS_202.97_0.bin")] = 4;
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        Assert.Equal(4, await client.GetFileRunsAsync(BayFolder.Vmc, "SLUS_202.97_0.bin"));
        var missing = await Assert.ThrowsAsync<LoadoutBayException>(() => client.GetFileRunsAsync(BayFolder.Vmc, "SLUS_202.97_1.bin"));
        Assert.Equal(BayWire.StatusNotFound, missing.Status);
    }

    // Progress<T> posts to the thread pool; this one reports in order, on the caller's thread.
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
