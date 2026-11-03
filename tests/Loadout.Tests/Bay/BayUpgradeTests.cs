using Loadout.Fakes;

namespace Loadout.Bay;

public sealed class BayUpgradeTests
{
    private static readonly byte[] Elf = [0x7F, (byte)'E', (byte)'L', (byte)'F', .. new byte[(2 * BayWire.FileChunkMaxBytes) + 99]];

    public BayUpgradeTests()
    {
        BayUpgrade.FirstWait = TimeSpan.Zero;
        BayUpgrade.RetryInterval = TimeSpan.FromMilliseconds(20);
    }

    [Fact]
    public async Task TheNewCopyIsStartedAndSavesItselfOnceItAnswersWithTheTag()
    {
        using var bay = new FakeLoadoutBay();
        var stages = new List<BayUpgradeStage>();

        var result = await BayUpgrade.RunAsync("127.0.0.1", Elf, save: true, new SynchronousProgress<BayUpgradeStage>(stages.Add), bay.Port);

        Assert.Equal([Elf], bay.StartedElfs);
        Assert.Equal(Elf, bay.SavedElf);
        Assert.Matches("^[0-9a-f]{16}$", bay.RunTag);
        Assert.Equal(new BayUpgradeResult("0.1.0", Saved: true), result);
        Assert.Equal([BayUpgradeStage.Sending, BayUpgradeStage.Starting, BayUpgradeStage.WaitingForRestart, BayUpgradeStage.Saving], stages);
    }

    [Fact]
    public async Task WithoutSavingNothingIsWritten()
    {
        using var bay = new FakeLoadoutBay();

        var result = await BayUpgrade.RunAsync("127.0.0.1", Elf, save: false, port: bay.Port);

        Assert.Single(bay.StartedElfs);
        Assert.Null(bay.SavedElf);
        Assert.False(result.Saved);
    }

    [Fact]
    public async Task TheGreetingNamesTheTagTheCopyWasStartedWith()
    {
        using var bay = new FakeLoadoutBay { RunTag = "0123456789abcdef" };

        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        Assert.Equal("0123456789abcdef", client.Hello.RunTag);
    }

    [Fact]
    public async Task ALoadoutBayOfAnotherProtocolVersionStillTakesAndStartsTheElf()
    {
        using var bay = new FakeLoadoutBay(protocolVersion: BayWire.ProtocolVersion + 1);
        await Assert.ThrowsAsync<LoadoutBayException>(() => LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port));

        using (var client = await LoadoutBayClient.ConnectForUpgradeAsync("127.0.0.1", bay.Port))
        {
            Assert.Equal(BayWire.ProtocolVersion + 1, client.Hello.ProtocolVersion);
            await client.SendElfAsync(Elf);
            await client.RunElfAsync("feed");
        }

        Assert.Equal([Elf], bay.StartedElfs);
        Assert.Equal("feed", bay.RunTag);
    }

    [Fact]
    public async Task ALoadoutBayBeforeProtocol5IsToldApart()
    {
        using var bay = new FakeLoadoutBay(protocolVersion: 4);
        using var client = await LoadoutBayClient.ConnectForUpgradeAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.SendElfAsync(Elf));

        Assert.Contains("older than protocol 5", error.Message);
        Assert.Empty(bay.StartedElfs);
    }

    [Fact]
    public async Task AFileThatIsNotAnElfIsNotStarted()
    {
        using var bay = new FakeLoadoutBay();
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        await client.SendElfAsync(new byte[100]);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.RunElfAsync("feed"));

        Assert.Equal(BayWire.StatusNotExecutable, error.Status);
        Assert.Empty(bay.StartedElfs);
    }

    [Fact]
    public async Task ACopyNotStartedFromAUsbDriveRunsButIsNotSaved()
    {
        using var bay = new FakeLoadoutBay { StartedFromUsb = false };

        var result = await BayUpgrade.RunAsync("127.0.0.1", Elf, save: true, port: bay.Port);

        Assert.False(result.Saved);
        Assert.Single(bay.StartedElfs);
        Assert.Null(bay.SavedElf);

        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        await client.SendElfAsync(Elf);
        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.SaveElfAsync());
        Assert.Contains("not started from a USB drive", error.Message);
    }

    [Fact]
    public async Task ACopyThatNeverAnswersWithTheTagEndsInATimeout()
    {
        using var bay = new FakeLoadoutBay { NewCopyNeverStarts = true };

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => BayUpgrade.RunAsync(
            "127.0.0.1", Elf, save: true, port: bay.Port, restartTimeout: TimeSpan.FromMilliseconds(300)));

        Assert.Contains("did not answer within", error.Message);
        Assert.Contains("is not the one just sent", error.Message);
        Assert.Null(bay.SavedElf);
    }

    [Fact]
    public async Task OnTheHardDiskTheElfAndItsTitleConfigGoInTheAppFolder()
    {
        using var bay = new FakeLoadoutBay();
        bay.Files[(BayFolder.App, "loadout-bay.elf")] = [1, 2, 3];

        await BayUpgrade.PutOnHardDiskAsync("127.0.0.1", Elf, port: bay.Port);

        Assert.Equal(Elf, bay.Files[(BayFolder.App, "loadout-bay.elf")]);
        Assert.Equal("title=Loadout Bay for PS2\nboot=loadout-bay.elf\n", System.Text.Encoding.ASCII.GetString(bay.Files[(BayFolder.App, "title.cfg")]));
        Assert.Equal(2, bay.Files.Keys.Count(key => key.Folder == BayFolder.App));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
