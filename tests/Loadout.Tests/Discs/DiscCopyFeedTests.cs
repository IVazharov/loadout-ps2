using Loadout.Bay;
using Loadout.Core;
using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Discs;

public sealed class DiscCopyFeedTests : IDisposable
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loadout-feed-" + Guid.NewGuid().ToString("N"));

    public DiscCopyFeedTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public async Task OnlyAPieceThatJoinsTheRunFromTheFirstMovesTheFeedOn()
    {
        using var feed = new DiscCopyFeed(1000, 0);

        feed.Wrote(200, 100);
        Assert.Equal(0, feed.Through);
        feed.Wrote(0, 128);
        Assert.Equal(128, feed.Through);
        feed.Wrote(100, 50);
        Assert.Equal(150, feed.Through);
        feed.Wrote(0, 10);
        Assert.Equal(150, feed.Through);
        Assert.Equal(150, await feed.WaitForAsync(149, CancellationToken.None));
    }

    [Fact]
    public async Task AWaitEndsWhenItsSectorIsWrittenAndFailsWhenTheFeedBreaks()
    {
        using var feed = new DiscCopyFeed(1000, 0);
        var waiting = feed.WaitForAsync(300, CancellationToken.None);
        feed.Wrote(0, 200);
        Assert.False(waiting.IsCompleted);
        feed.Wrote(200, 128);
        Assert.Equal(328, await waiting);

        var next = feed.WaitForAsync(500, CancellationToken.None);
        feed.Break();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.True(feed.Broken.IsCancellationRequested);

        // Broken, even sectors already in the file are refused: the install is being cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.WaitForAsync(10, CancellationToken.None));
    }

    [Fact]
    public void AFeedStartsAtTheCopysFirstGapAndOnlyACopyWithNothingMissingCanBeFollowed()
    {
        var map = DiscCopyMap.New(1000, "test");
        map.Mark(0, 300, DiscSectorState.ReadNormally);

        Assert.True(DiscCopyFeed.CanFollow(map));
        using (var feed = DiscCopyFeed.For(map))
        {
            Assert.Equal(300, feed.Through);
        }

        map.Mark(300, 700, DiscSectorState.ReadNormally);
        using (var whole = DiscCopyFeed.For(map))
        {
            Assert.Equal(1000, whole.Through);
        }

        map.Mark(500, 10, DiscSectorState.Failed);
        Assert.False(DiscCopyFeed.CanFollow(map));
    }

    [Fact]
    public async Task AnInstallFollowingACleanCopyGetsEverySectorAsTheCopyWritesIt()
    {
        const long sectors = 3000;
        var clock = new FakeDiscClock();
        using var drive = new FakeDiscDrive(sectors, clock);
        var path = Path.Combine(_folder, "game.iso");
        var map = DiscCopyMap.New(sectors, "test");
        using var feed = DiscCopyFeed.For(map);
        await using var follow = new DiscCopyFeedStream(feed, path, DiscSectors.Size);
        var reading = ReadAllAsync(follow);

        await using (var image = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
        {
            var copier = new DiscCopier(drive, image, map, _ => { }, clock: clock) { Feed = feed };
            Assert.Equal(DiscCopyOutcome.Complete, await copier.RunAsync(CancellationToken.None));
        }

        Assert.Equal(drive.Expected(0, (int)sectors), await reading);
        Assert.False(feed.IsBroken);
        Assert.Equal(sectors, feed.Through);
        Assert.Equal(sectors * DiscSectors.Size, follow.Length);
    }

    [Fact]
    public async Task AFailedReadBreaksTheFeedWhileTheCopyGoesOn()
    {
        const long sectors = 3000;
        var clock = new FakeDiscClock();
        using var drive = new FakeDiscDrive(sectors, clock);
        drive.Weak[1500] = 1;
        var path = Path.Combine(_folder, "weak.iso");
        var map = DiscCopyMap.New(sectors, "test");
        using var feed = DiscCopyFeed.For(map);
        await using var follow = new DiscCopyFeedStream(feed, path, DiscSectors.Size);
        var reading = ReadAllAsync(follow);

        await using (var image = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
        {
            var copier = new DiscCopier(drive, image, map, _ => { }, clock: clock) { Feed = feed };
            Assert.Equal(DiscCopyOutcome.Complete, await copier.RunAsync(CancellationToken.None));
        }

        Assert.True(feed.IsBroken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(map.IsComplete);
    }

    [Fact]
    public async Task StoppingTheCopyBreaksTheFeed()
    {
        var clock = new FakeDiscClock();
        using var drive = new FakeDiscDrive(5000, clock);
        using var stop = new CancellationTokenSource();
        drive.AfterRead = reads =>
        {
            if (reads == 10)
            {
                stop.Cancel();
            }
        };
        var map = DiscCopyMap.New(5000, "test");
        using var feed = DiscCopyFeed.For(map);
        using var image = new MemoryStream();
        var copier = new DiscCopier(drive, image, map, _ => { }, clock: clock) { Feed = feed };

        Assert.Equal(DiscCopyOutcome.Stopped, await copier.RunAsync(stop.Token));

        Assert.True(feed.IsBroken);
        Assert.Equal(map.ReadSectors, feed.Through);
    }

    [Fact]
    public async Task AFinishedCopyCanBeRenamedUnderAnInstallThatHasItOpenAndOneNotStartedYetIsStopped()
    {
        var bytes = new byte[40 * DiscSectors.Size];
        new Random(40).NextBytes(bytes);
        var path = Path.Combine(_folder, "Provisional [SLES-50219].iso");
        await File.WriteAllBytesAsync(path, bytes);

        using var reading = new DiscCopyFeed(40, 40);
        await using var open = new DiscCopyFeedStream(reading, path, DiscSectors.Size);
        var first = new byte[DiscSectors.Size];
        Assert.Equal(first.Length, await open.ReadAsync(first));
        Assert.False(reading.CloseUnopened());
        File.Move(path, Path.Combine(_folder, "Redump Name (Europe).iso"));
        var rest = await ReadAllAsync(open);
        Assert.Equal(bytes, first.Concat(rest).ToArray());
        Assert.False(reading.IsBroken);

        using var waiting = new DiscCopyFeed(40, 40);
        await using var late = new DiscCopyFeedStream(waiting, path, DiscSectors.Size);
        Assert.True(waiting.CloseUnopened());
        Assert.True(waiting.IsBroken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.ReadAsync(first).AsTask());
    }

    [Fact]
    public async Task ARawCdCopyIsFollowedAsItsUserData()
    {
        // 170 sectors: several batches of raw reads, and reads that end inside a sector.
        var user = DiscImageBuilder.Build("Cd Game", "BOOT2 = cdrom0:\\SCES_500.01;1\n", extraSectors: 150);
        new Random(170).NextBytes(user.AsSpan(20 * 2048));
        var raw = DiscImageBuilder.WrapMode2(user);
        var path = Path.Combine(_folder, "cd.bin");
        await File.WriteAllBytesAsync(path, raw);
        var sectors = raw.Length / 2352;
        using var feed = new DiscCopyFeed(sectors, 0);
        await using var follow = new DiscCopyFeedStream(feed, path, 2352);
        var reading = ReadAllAsync(follow);

        feed.Wrote(0, 7);
        feed.Wrote(7, sectors - 7);

        Assert.Equal(user, await reading);
    }

    [Fact]
    public async Task TheRowForADiscBeingCopiedIsTheOneTheInstallPageMakesFromTheFinishedCopy()
    {
        var dvd = DiscImageBuilder.Build("Dvd Game", "BOOT2 = cdrom0:\\SLES_538.86;1\nVER = 1.00\n", extraSectors: 40);
        var dvdFiles = new DiscBackupFiles(_folder, "Dvd Game [SLES-53886]");
        await File.WriteAllBytesAsync(dvdFiles.Image, dvd);
        var cd = DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Cd Game", "BOOT2 = cdrom0:\\SCES_500.01;1\n", extraSectors: 25));
        var cdFiles = new DiscBackupFiles(_folder, "Cd Game [SCES-50001]", IsCd: true);
        await File.WriteAllBytesAsync(cdFiles.Image, cd);
        cdFiles.WriteCue(2);

        foreach (var (files, media, sectors) in new[] { (dvdFiles, DiscMedia.Dvd, dvd.Length / 2048L), (cdFiles, DiscMedia.Cd, cd.Length / 2352L) })
        {
            var scan = await DiscScanner.ScanAsync([new ScanInput(DiscBackupInstall.ScanPath(files), ScanDepth.File)], null, new DiscScanOptions());
            var scanned = Assert.Single(scan.Items);
            Assert.True(DiscId.TryParse(scanned.DiscId, out var id));

            var following = DiscBackupInstall.FollowingCopy(files, scanned.DetectedTitle, id, media, sectors, layers: 1);

            Assert.NotNull(following);
            Assert.Equal(QueueItemStatus.Ready, scanned.Status);
            Assert.Equal(BayQueueInstaller.RequestFor(scanned), BayQueueInstaller.RequestFor(following));
            Assert.Equal(scanned.Media, following.Media);
            Assert.Equal(scanned.InstallBytes, following.InstallBytes);
        }

        Assert.True(DiscId.TryParse("SLES-53886", out var two));
        Assert.Null(DiscBackupInstall.FollowingCopy(dvdFiles, "Two layers", two, DiscMedia.Dvd, 4_000_000, layers: 2));
    }

    [Fact]
    public async Task TheQueueInstallsFromAGivenStreamInsteadOfTheFile()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        var path = Path.Combine(_folder, "not-there-yet.iso");
        var item = new QueuePreviewItem
        {
            SourceFile = path,
            DataFile = path,
            DiscId = "SLUS-20043",
            DetectedTitle = "Followed",
            Layer0Sectors = 3,
            HdlDiscType = HdlDiscTypes.Ps2Dvd,
            Status = QueueItemStatus.Ready,
        };
        var bytes = new byte[3 * 2048];
        new Random(3).NextBytes(bytes);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [item], port: bay.Port, dataPort: bay.DataPort, open: _ => new MemoryStream(bytes));

        Assert.Equal(BayInstallOutcome.Installed, Assert.Single(run.Results).Outcome);
        Assert.Equal(bytes, bay.Installed);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await Task.Yield();
        using var all = new MemoryStream();
        var buffer = new byte[100_000];
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            all.Write(buffer, 0, count);
        }

        return all.ToArray();
    }
}
