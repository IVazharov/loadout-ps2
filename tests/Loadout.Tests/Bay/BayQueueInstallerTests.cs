using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayQueueInstallerTests : IDisposable
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private readonly string _folder = Directory.CreateTempSubdirectory("loadout-queue-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ReadyGamesAreInstalledGamesOnThePs2AreSkippedAndIncompleteInstallsReplaced()
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
            [BayInstallOutcome.Installed, BayInstallOutcome.AlreadyOnPs2, BayInstallOutcome.Installed, BayInstallOutcome.NotReady],
            run.Results.Select(result => result.Outcome));
        Assert.Equal(File.ReadAllBytes(queue[2].DataFile!), bay.Installed);
        Assert.Equal(2, bay.InstallRequests.Count);
        Assert.NotNull(run.Library);
        Assert.DoesNotContain(bay.Games, game => game.Partition == "PP.SLUS-20035.HDL.Mythic_s_Keep_");
    }

    [Fact]
    public async Task AFailedCheckIsRemovedFromThePs2AndSaysSo()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { CorruptByteAt = 5 };

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [Image("SLUS-20043", "One", 2)], checkCopies: true, port: bay.Port, dataPort: bay.DataPort);

        var result = Assert.Single(run.Results);
        Assert.Equal(BayInstallOutcome.CheckFailed, result.Outcome);
        Assert.False(result.PartialCopyLeft);
        Assert.EndsWith("Its partial copy was removed from the PS2.", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, bay.RemovedInstalls);
    }

    [Fact]
    public async Task APartialCopyLoadoutBayCouldNotRemoveIsReported()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { CorruptByteAt = 5, RemovesFailedInstalls = false };

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [Image("SLUS-20043", "One", 2)], checkCopies: true, port: bay.Port, dataPort: bay.DataPort);

        var result = Assert.Single(run.Results);
        Assert.True(result.PartialCopyLeft);
        Assert.Contains("could not be removed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageThatCannotBeReadMidwaySaysSoRatherThanBlamingTheNetwork()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        var image = Image("SLUS-20043", "Damaged", 4);
        await using var failing = new FailingStream(File.ReadAllBytes(image.DataFile!), failAfter: 2048);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var ex = await Assert.ThrowsAsync<LoadoutBayException>(() => client.InstallAsync(BayQueueInstaller.RequestFor(image)!, failing, dataPort: bay.DataPort));

        Assert.StartsWith("Its image could not be read, so the game was not installed. The chd is damaged.", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("Its partial copy was removed from the PS2.", ex.Message, StringComparison.Ordinal);
        Assert.NotEqual(0, ex.Status);
    }

    // Gives its bytes, then fails the way a damaged chd does.
    private sealed class FailingStream(byte[] bytes, int failAfter) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter)
            {
                throw new IOException("The chd is damaged.");
            }

            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, failAfter - Position)], cancellationToken);
        }
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
    public async Task EachResultIsReportedAsItsGameFinishes()
    {
        using var bay = new FakeLoadoutBay(disk: Disk with { FreeSectors = 10 * 4 });
        var finished = new List<(BayInstallOutcome Outcome, int InstallsSoFar)>();

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1",
            [Image("SLUS-20043", "Too big", 11), Image("SLUS-20044", "Fits", 2), Image("SLUS-20045", "Fits too", 2)],
            port: bay.Port,
            dataPort: bay.DataPort,
            finished: new SynchronousProgress<BayQueueResult>(result => finished.Add((result.Outcome, bay.InstallRequests.Count))));

        Assert.Equal(
            [(BayInstallOutcome.DoesNotFit, 0), (BayInstallOutcome.Installed, 1), (BayInstallOutcome.Installed, 2)],
            finished);
        Assert.Equal(run.Results.Select(result => result.Outcome), finished.Select(result => result.Outcome));
        Assert.StartsWith("PP.SLUS-20044", run.Results[1].PartitionName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LosingLoadoutBayWhenReadingTheListBackKeepsTheResults()
    {
        // The first list is read before the games, the second after them.
        using var bay = new FakeLoadoutBay(disk: Disk) { HangUpOn = (BayWire.CommandListGames, 2) };

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "One", 2), Image("SLUS-20044", "Two", 2)], port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal([BayInstallOutcome.Installed, BayInstallOutcome.Installed], run.Results.Select(result => result.Outcome));
        Assert.Null(run.Library);
        Assert.False(run.LostConnection);
    }

    [Fact]
    public async Task LosingLoadoutBayAfterAFailedCheckKeepsThatResultAsTheNews()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { CorruptByteAt = 0, HangUpOn = (BayWire.CommandListGames, 2) };

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "One", 2), Image("SLUS-20044", "Two", 2)], checkCopies: true, port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal([BayInstallOutcome.CheckFailed, BayInstallOutcome.CheckFailed], run.Results.Select(result => result.Outcome));
        Assert.Null(run.Library);
        Assert.False(run.LostConnection);
    }

    [Fact]
    public async Task LosingLoadoutBayDuringAGameIsALostConnection()
    {
        // The second game's install is the one that loses the connection.
        using var bay = new FakeLoadoutBay(disk: Disk) { HangUpOn = (BayWire.CommandInstallBegin, 2) };

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "One", 2), Image("SLUS-20044", "Two", 2)], port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal([BayInstallOutcome.Installed, BayInstallOutcome.Failed], run.Results.Select(result => result.Outcome));
        Assert.Null(run.Library);
        Assert.True(run.LostConnection);
    }

    [Fact]
    public async Task LosingLoadoutBayWhenReadingTheFreeSpaceBackStopsTheQueueAndKeepsTheResults()
    {
        // The first free space is read before the games, the second after the first game.
        using var bay = new FakeLoadoutBay(disk: Disk) { HangUpOn = (BayWire.CommandFreeSpace, 2) };

        var run = await BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "One", 2), Image("SLUS-20044", "Two", 2)], port: bay.Port, dataPort: bay.DataPort);

        Assert.Equal([BayInstallOutcome.Installed, BayInstallOutcome.NotRun], run.Results.Select(result => result.Outcome));
        Assert.Null(run.Library);
        Assert.True(run.LostConnection);
    }

    [Fact]
    public async Task LosingLoadoutBayBeforeAnyGameStillFails()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { HangUpOn = (BayWire.CommandListGames, 1) };

        await Assert.ThrowsAsync<LoadoutBayException>(() => BayQueueInstaller.RunAsync(
            "127.0.0.1", [Image("SLUS-20043", "One", 2)], port: bay.Port, dataPort: bay.DataPort));
        Assert.Empty(bay.InstallRequests);
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

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
