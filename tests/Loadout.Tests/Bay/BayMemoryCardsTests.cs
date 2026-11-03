using System.Text;
using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayMemoryCardsTests : IDisposable
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private readonly string _folder = Directory.CreateTempSubdirectory("loadout-cards-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ThePlanTellsNewCardsFromCardsThatAreThere()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")] = [1, 2, 3];
        bay.Files[(BayFolder.Cfg, "SLUS_200.43.cfg")] = "$VMC_0=generic\r\n"u8.ToArray();

        var plans = await BayMemoryCards.PlanAsync("127.0.0.1", [Image("SLUS-20297", 1), Image("SLUS-20043", 1), Image("SCUS-97399", 1)], bay.Port);

        Assert.Equal(
            [
                new MemoryCardPlan("SLUS-20297", "SLUS_202.97_0", MemoryCardState.CardThere, null),
                new MemoryCardPlan("SLUS-20043", "SLUS_200.43_0", MemoryCardState.SettingsNameACard, "generic"),
                new MemoryCardPlan("SCUS-97399", "SCUS_973.99_0", MemoryCardState.None, null),
            ],
            plans);
    }

    [Fact]
    public async Task AnInstalledGameGetsANewCardAndItsSettingsNameIt()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = "$Compatibility=5\r\n"u8.ToArray();
        var game = Image("SLUS-20297", 2);
        var plans = await BayMemoryCards.PlanAsync("127.0.0.1", [game], bay.Port);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [game], port: bay.Port, dataPort: bay.DataPort, memoryCards: new MemoryCardRequest(8, plans, ReplaceExisting: false));

        Assert.Equal(8 * 1024 * 1024, bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")].Length);
        Assert.Equal("$Compatibility=5\r\n$VMC_0=SLUS_202.97_0\r\n", Encoding.ASCII.GetString(bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")]));
        Assert.Contains("got a new 8 MB memory card, VMC/SLUS_202.97_0.bin", run.Results[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACardThatIsThereKeepsItsSavesUnlessTheUserReplacesIt()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")] = [1, 2, 3];
        var game = Image("SLUS-20297", 2);
        var plans = await BayMemoryCards.PlanAsync("127.0.0.1", [game], bay.Port);

        var kept = await BayQueueInstaller.RunAsync("127.0.0.1", [game], port: bay.Port, dataPort: bay.DataPort, memoryCards: new MemoryCardRequest(8, plans, ReplaceExisting: false));

        Assert.Equal([1, 2, 3], bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")]);
        Assert.Equal("$VMC_0=SLUS_202.97_0\r\n", Encoding.ASCII.GetString(bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")]));
        Assert.Contains("uses the memory card already on the PS2", kept.Results[0].Message, StringComparison.Ordinal);

        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        var replaced = await BayMemoryCards.GiveCardAsync(client, plans[0], 16, replaceExisting: true, new HashSet<string>(), DateTimeOffset.Now);

        Assert.Equal(16 * 1024 * 1024, bay.Files[(BayFolder.Vmc, "SLUS_202.97_0.bin")].Length);
        Assert.Contains("was replaced with a new, empty 16 MB card", replaced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACardTheSettingsNameIsLeftAlone()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = "$VMC_0=mine\r\n"u8.ToArray();
        var game = Image("SLUS-20297", 2);
        var plans = await BayMemoryCards.PlanAsync("127.0.0.1", [game], bay.Port);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [game], port: bay.Port, dataPort: bay.DataPort, memoryCards: new MemoryCardRequest(8, plans, ReplaceExisting: true));

        Assert.DoesNotContain(bay.Files.Keys, key => key.Folder == BayFolder.Vmc);
        Assert.Equal("$VMC_0=mine\r\n", Encoding.ASCII.GetString(bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")]));
        Assert.Contains("already name a memory card, mine", run.Results[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDiscsOfAGroupShareOneNewCard()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        var discs = new[] { Image("SCES-53286", 2), Image("SCUS-97429", 3) };
        var plans = await BayMemoryCards.PlanAsync("127.0.0.1", discs, bay.Port);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", discs, port: bay.Port, dataPort: bay.DataPort, memoryCards: new MemoryCardRequest(8, plans, ReplaceExisting: false));

        Assert.Single(bay.Files.Keys, key => key.Folder == BayFolder.Vmc);
        Assert.Equal("$VMC_0=XEBP_100.01_0\r\n", Encoding.ASCII.GetString(bay.Files[(BayFolder.Cfg, "SCES_532.86.cfg")]));
        Assert.Equal("$VMC_0=XEBP_100.01_0\r\n", Encoding.ASCII.GetString(bay.Files[(BayFolder.Cfg, "SCUS_974.29.cfg")]));
        Assert.Contains("shares the memory card made for an earlier game", run.Results[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACardOplWouldRefuseAsFragmentedIsDeletedAndTheInstallStands()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.FileRuns[(BayFolder.Vmc, "SLUS_202.97_0.bin")] = BayWire.VmcMaxRuns;
        var game = Image("SLUS-20297", 2);
        var plans = await BayMemoryCards.PlanAsync("127.0.0.1", [game], bay.Port);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [game], port: bay.Port, dataPort: bay.DataPort, memoryCards: new MemoryCardRequest(8, plans, ReplaceExisting: false));

        Assert.Equal(BayInstallOutcome.Installed, run.Results[0].Outcome);
        Assert.DoesNotContain(bay.Files.Keys, key => key.Folder == BayFolder.Vmc);
        Assert.DoesNotContain(bay.Files.Keys, key => key.Folder == BayFolder.Cfg);
        Assert.Contains("Its memory card could not be made: The new card landed in 10 pieces", run.Results[0].Message, StringComparison.Ordinal);
        Assert.NotNull(run.Library);
    }

    private QueuePreviewItem Image(string discId, int sectors)
    {
        var path = Path.Combine(_folder, discId + ".iso");
        File.WriteAllBytes(path, new byte[sectors * 2048]);
        return new QueuePreviewItem
        {
            SourceFile = path,
            DataFile = path,
            DiscId = discId,
            DetectedTitle = discId,
            Layer0Sectors = (uint)sectors,
            HdlDiscType = HdlDiscTypes.Ps2Cd,
            Status = QueueItemStatus.Ready,
        };
    }
}
