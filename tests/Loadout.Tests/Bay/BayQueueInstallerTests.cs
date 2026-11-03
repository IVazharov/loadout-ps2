using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayQueueInstallerTests : IDisposable
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private readonly string _folder = Directory.CreateTempSubdirectory("loadout-queue-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ReadyGamesAreInstalledAndGamesOnThePs2AreSkipped()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games:
        [
            ("PP.SLUS-20675.HDL.Mythic_s_Keep_", BayGameTests.Synthetic("Mythic's Keep - Deep Crossing 2", "SLUS_206.75")),
            ("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[HdlGameHeader.Bytes]),
        ]);
        var starWars = Image("SLUS-20043", "Moon Wars - Super Rocket Racing", 12);
        var queue = new[]
        {
            starWars,
            Image("SLUS-20675", "Mythic's Keep - Deep Crossing 2", 4),
            Image("SLUS-20035", "Mythic's Keep - Deep Crossing", 4),
            Image("SLES-50003", "Needs a chd conversion", 4) with { Status = QueueItemStatus.NeedsConversion },
        };

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", queue, port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal(
            [BayInstallOutcome.Installed, BayInstallOutcome.AlreadyOnPs2, BayInstallOutcome.IncompleteOnPs2, BayInstallOutcome.NotReady],
            run.Results.Select(result => result.Outcome));
        Assert.Equal(File.ReadAllBytes(starWars.DataFile!), bay.Installed);
        Assert.Single(bay.InstallRequests);
        Assert.NotNull(run.Library);
        Assert.Contains("Remove it in Installed Games", run.Results[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGameLargerThanTheFreeSpaceIsSkippedWithoutBeingSent()
    {
        using var bay = new FakeLoadoutBay(disk: Disk with { FreeSectors = 10 * 4 });

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "Too big", 11), Image("SLUS-20044", "Fits", 10)], port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal([BayInstallOutcome.DoesNotFit, BayInstallOutcome.Installed], run.Results.Select(result => result.Outcome));
        Assert.Single(bay.InstallRequests);
    }

    [Fact]
    public async Task ARefusalFromLoadoutBayMovesOnToTheNextGame()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { InstallBeginStatus = BayWire.StatusNoSpace };

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "One", 2), Image("SLUS-20044", "Two", 2)], port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal([BayInstallOutcome.DoesNotFit, BayInstallOutcome.DoesNotFit], run.Results.Select(result => result.Outcome));
        Assert.Equal(2, bay.InstallRequests.Count);
        Assert.NotNull(run.Library);
    }

    [Fact]
    public void OnlyAReadyImageWithADiscIdMakesARequest()
    {
        var ready = Image("SLUS-20043", "Moon Wars - Super Rocket Racing", 3);

        var request = BayQueueInstaller.RequestFor(ready);

        Assert.Equal(new BayInstallRequest("SLUS-20043", "SLUS_200.43", "Moon Wars - Super Rocket Racing", 3, 0, HdlDiscTypes.Ps2Cd), request);
        Assert.Null(BayQueueInstaller.RequestFor(ready with { Status = QueueItemStatus.Duplicate }));
        Assert.Null(BayQueueInstaller.RequestFor(ready with { DiscId = null }));
    }

    // A headerless image of random sectors, queued as Ready.
    private QueuePreviewItem Image(string discId, string title, int sectors)
    {
        var path = Path.Combine(_folder, discId + ".iso");
        var bytes = new byte[sectors * 2048];
        new Random(sectors).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return new QueuePreviewItem
        {
            SourceFile = path,
            DataFile = path,
            DiscId = discId,
            DetectedTitle = title,
            Layer0Sectors = (uint)sectors,
            HdlDiscType = HdlDiscTypes.Ps2Cd,
            Status = QueueItemStatus.Ready,
        };
    }
}
