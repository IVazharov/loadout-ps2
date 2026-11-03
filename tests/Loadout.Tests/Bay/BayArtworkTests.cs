using Loadout.Artwork;
using Loadout.Core;
using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayArtworkTests : IDisposable
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private readonly string _root = Directory.CreateTempSubdirectory("loadout-art-").FullName;

    public void Dispose() => TempDirectory.Delete(_root);

    [Fact]
    public void OnlyFilesThePs2LacksArePlannedUnlessReplacing()
    {
        var cache = new ArtworkCache(_root);
        var rtcw = Game("SLUS-20297", "Return to Castle Ravensworth");
        var gow = Game("SCUS-97399", "Son of Fire");
        cache.Store(rtcw.DiscId, OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));
        cache.Store(rtcw.DiscId, OplArtKind.Screenshot1, TinyImages.Png((4, 5, 6), (7, 8, 9)));

        var plan = BayArtwork.Plan([rtcw, gow], cache, new HashSet<string> { "SLUS_202.97_COV.png" }, replaceExisting: false);
        var replace = BayArtwork.Plan([rtcw, gow], cache, new HashSet<string> { "SLUS_202.97_COV.png" }, replaceExisting: true);

        Assert.Equal(["SLUS_202.97_SCR.png", "SLUS_202.97_SCR1.png"], plan.ToSend.Select(file => file.RemoteName));
        Assert.Equal(1, plan.AlreadyOnPs2);
        Assert.Equal([gow], plan.WithoutArt);
        Assert.Equal(["SLUS_202.97_COV.png", "SLUS_202.97_SCR.png", "SLUS_202.97_SCR1.png"], replace.ToSend.Select(file => file.RemoteName));
    }

    [Fact]
    public async Task SendingWritesEachPlannedFileAndReportsBytes()
    {
        var cache = new ArtworkCache(_root);
        var rtcw = Game("SLUS-20297", "Return to Castle Ravensworth");
        var cover = TinyImages.Png((1, 2, 3), (4, 5, 6));
        cache.Store(rtcw.DiscId, OplArtKind.Cover, cover);
        cache.Store(rtcw.DiscId, OplArtKind.Icon, TinyImages.Png((7, 8, 9), (1, 2, 3)));
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")] = [1];
        var reports = new List<ArtworkSendProgress>();

        var result = await BayArtwork.SendAsync("127.0.0.1", [rtcw], cache, replaceExisting: false, new Recorder(reports), bay.Port);

        Assert.Equal(["SLUS_202.97_COV.png"], result.Sent.Select(file => file.RemoteName));
        Assert.Empty(result.Failed);
        Assert.Equal(cover, bay.Files[(BayFolder.Art, "SLUS_202.97_COV.png")]);
        Assert.Equal([1], bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")]);
        Assert.Equal(new ArtworkSendProgress(1, 1, cover.Length, cover.Length, ""), reports[^1]);
    }

    [Fact]
    public async Task WithoutOplsPartitionNothingIsSent()
    {
        var cache = new ArtworkCache(_root);
        var rtcw = Game("SLUS-20297", "Return to Castle Ravensworth");
        cache.Store(rtcw.DiscId, OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));
        using var bay = new FakeLoadoutBay(disk: Disk) { HasOplPartition = false };

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => BayArtwork.SendAsync("127.0.0.1", [rtcw], cache, replaceExisting: false, port: bay.Port));

        Assert.Equal(BayWire.StatusNotFound, error.Status);
        Assert.DoesNotContain(BayWire.CommandFileWrite, bay.Commands);
    }

    [Fact]
    public async Task AnInstalledGameGetsItsArtworkAndKeepsWhatThePs2Has()
    {
        var cache = new ArtworkCache(_root);
        var game = Image("SLUS-20297");
        var cover = TinyImages.Png((1, 2, 3), (4, 5, 6));
        cache.Store(Game("SLUS-20297", "").DiscId, OplArtKind.Cover, cover);
        cache.Store(Game("SLUS-20297", "").DiscId, OplArtKind.Icon, TinyImages.Png((7, 8, 9), (1, 2, 3)));
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")] = [1];

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [game], port: bay.Port, dataPort: bay.DataPort, artwork: cache);

        var result = Assert.Single(run.Results);
        Assert.Equal(BayInstallOutcome.Installed, result.Outcome);
        Assert.False(result.ArtworkMissing);
        Assert.Equal(cover, bay.Files[(BayFolder.Art, "SLUS_202.97_COV.png")]);
        Assert.Equal([1], bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")]);
        Assert.Contains("1 of 1 files, 1 already on the PS2", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGameWithoutArtworkStillInstallsAndSaysSo()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [Image("SLUS-20297")], port: bay.Port, dataPort: bay.DataPort, artwork: new ArtworkCache(_root));

        var result = Assert.Single(run.Results);
        Assert.Equal(BayInstallOutcome.Installed, result.Outcome);
        Assert.True(result.ArtworkMissing);
        Assert.Contains("No artwork could be downloaded", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BayWire.CommandFileWrite, bay.Commands);
    }

    [Fact]
    public async Task ArtworkThePs2RefusesDoesNotUndoTheInstallOrStopTheQueue()
    {
        var cache = new ArtworkCache(_root);
        cache.Store(Game("SLUS-20297", "").DiscId, OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));
        using var bay = new FakeLoadoutBay(disk: Disk) { HasOplPartition = false };

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [Image("SLUS-20297"), Image("SLUS-20043")], port: bay.Port, dataPort: bay.DataPort, artwork: cache);

        Assert.Equal([BayInstallOutcome.Installed, BayInstallOutcome.Installed], run.Results.Select(result => result.Outcome));
        Assert.All(run.Results, result => Assert.True(result.ArtworkMissing));
        Assert.Contains("artwork could not be sent", run.Results[0].Message, StringComparison.Ordinal);
        Assert.NotNull(run.Library);
    }

    [Fact]
    public async Task WithoutTheOptionAnInstallSendsNoArtwork()
    {
        var cache = new ArtworkCache(_root);
        cache.Store(Game("SLUS-20297", "").DiscId, OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));
        using var bay = new FakeLoadoutBay(disk: Disk);

        var run = await BayQueueInstaller.RunAsync("127.0.0.1", [Image("SLUS-20297")], port: bay.Port, dataPort: bay.DataPort);

        Assert.False(Assert.Single(run.Results).ArtworkMissing);
        Assert.DoesNotContain(BayWire.CommandFileWrite, bay.Commands);
    }

    private QueuePreviewItem Image(string discId)
    {
        var path = Path.Combine(_root, discId + ".iso");
        File.WriteAllBytes(path, new byte[2 * 2048]);
        return new QueuePreviewItem
        {
            SourceFile = path,
            DataFile = path,
            DiscId = discId,
            DetectedTitle = "Game " + discId,
            Layer0Sectors = 2,
            HdlDiscType = HdlDiscTypes.Ps2Dvd,
            Status = QueueItemStatus.Ready,
        };
    }

    [Fact]
    public async Task ACoverThePcLacksIsReadFromThePs2AndStored()
    {
        var cache = new ArtworkCache(_root);
        var cover = TinyImages.Png((1, 2, 3), (4, 5, 6));
        var game = Game("SLUS-20297", "").DiscId;
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_COV.png")] = cover;
        bay.Files[(BayFolder.Art, "SLUS_202.97_BG.png")] = TinyImages.Png((7, 8, 9), (1, 2, 3));
        bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")] = TinyImages.Png((9, 9, 9), (1, 1, 1));
        var reports = new List<CoverReadProgress>();

        var read = await BayArtwork.ReadCoversAsync("127.0.0.1", [game, game], cache, new Reports(reports), bay.Port);

        Assert.Equal(1, read);
        Assert.Equal(cover, cache.TryGet(game, OplArtKind.Cover)?.Content);
        Assert.Null(cache.TryGet(game, OplArtKind.Background));
        Assert.Null(cache.TryGet(game, OplArtKind.Icon));
        Assert.Equal([new CoverReadProgress(1, 1, "SLUS-20297")], reports);
    }

    [Fact]
    public async Task AJpegCoverOrAnIconServesWhenThereIsNoPngCover()
    {
        var cache = new ArtworkCache(_root);
        var jpeg = Game("SLUS-20297", "").DiscId;
        var icon = Game("SCUS-97399", "").DiscId;
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_COV.jpg")] = TinyImages.Jpeg;
        bay.Files[(BayFolder.Art, "SCUS_973.99_ICO.png")] = TinyImages.Png((1, 2, 3), (4, 5, 6));

        var read = await BayArtwork.ReadCoversAsync("127.0.0.1", [jpeg, icon], cache, port: bay.Port);

        Assert.Equal(2, read);
        Assert.Equal(ArtworkFormat.Jpeg, cache.TryGet(jpeg, OplArtKind.Cover)?.Format);
        Assert.NotNull(cache.CoverOrIconPath(icon.Canonical));
    }

    [Fact]
    public async Task NothingConnectsWhenEveryGameHasAPicture()
    {
        var cache = new ArtworkCache(_root);
        var game = Game("SLUS-20297", "").DiscId;
        cache.Store(game, OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));
        using var bay = new FakeLoadoutBay(disk: Disk);

        var read = await BayArtwork.ReadCoversAsync("127.0.0.1", [game], cache, port: bay.Port);

        Assert.Equal(0, read);
        Assert.Empty(bay.Commands);
    }

    [Fact]
    public async Task AFileThatIsNotAnImageOrAGameWithoutArtIsPassedOver()
    {
        var cache = new ArtworkCache(_root);
        var broken = Game("SLUS-20297", "").DiscId;
        var none = Game("SCUS-97399", "").DiscId;
        var good = Game("SLUS-20035", "").DiscId;
        var icon = TinyImages.Png((1, 2, 3), (4, 5, 6));
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_COV.png")] = [1, 2, 3];
        bay.Files[(BayFolder.Art, "SLUS_202.97_ICO.png")] = icon;
        bay.Files[(BayFolder.Art, "SLUS_200.35_COV.png")] = TinyImages.Png((7, 8, 9), (1, 2, 3));
        var reports = new List<CoverReadProgress>();

        var read = await BayArtwork.ReadCoversAsync("127.0.0.1", [broken, none, good], cache, new Reports(reports), bay.Port);

        Assert.Equal(2, read);
        Assert.Null(cache.TryGet(broken, OplArtKind.Cover));
        Assert.Equal(icon, cache.TryGet(broken, OplArtKind.Icon)?.Content);
        Assert.Null(cache.CoverOrIconPath(none.Canonical));
        Assert.NotNull(cache.TryGet(good, OplArtKind.Cover));
        Assert.Equal(
            [new CoverReadProgress(1, 3, "SLUS-20297"), new CoverReadProgress(2, 3, null), new CoverReadProgress(3, 3, "SLUS-20035")],
            reports);
    }

    [Fact]
    public async Task APartitionNameLoadoutBayWontUseIsSaidAndNothingIsSent()
    {
        var cache = new ArtworkCache(_root);
        var rtcw = Game("SLUS-20297", "Return to Castle Ravensworth");
        cache.Store(rtcw.DiscId, OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));
        using var bay = new FakeLoadoutBay(disk: Disk) { BadOplPartitionName = true };

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => BayArtwork.SendAsync("127.0.0.1", [rtcw], cache, replaceExisting: false, port: bay.Port));

        Assert.Equal(BayWire.StatusBadOplPartition, error.Status);
        Assert.Contains("conf_hdd.cfg", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BayWire.CommandFileWrite, bay.Commands);
    }

    [Fact]
    public async Task WithoutOplsPartitionReadingCoversSaysSo()
    {
        var cache = new ArtworkCache(_root);
        using var bay = new FakeLoadoutBay(disk: Disk) { HasOplPartition = false };

        var error = await Assert.ThrowsAsync<LoadoutBayException>(
            () => BayArtwork.ReadCoversAsync("127.0.0.1", [Game("SLUS-20297", "").DiscId], cache, port: bay.Port));

        Assert.Equal(BayWire.StatusNotFound, error.Status);
    }

    private sealed class Reports(List<CoverReadProgress> reports) : IProgress<CoverReadProgress>
    {
        public void Report(CoverReadProgress value) => reports.Add(value);
    }

    private static ArtworkGame Game(string discId, string title) => new(DiscId.TryParse(discId, out var id) ? id : default, title);

    private sealed class Recorder(List<ArtworkSendProgress> reports) : IProgress<ArtworkSendProgress>
    {
        public void Report(ArtworkSendProgress value) => reports.Add(value);
    }
}
