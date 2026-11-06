using System.Text;
using Loadout.Bay;
using Loadout.Core;

namespace Loadout.Images;

public class DiscScannerTests
{
    [Fact]
    public async Task DiscIdComesFromSystemCnfBytes()
    {
        using var tree = new TempTree();
        const string boot = "BOOT2 = cdrom0:\\SLES_538.86;1\nVER = 1.00\n";
        var path = Path.Combine(tree.Root, "plain.iso");
        var image = DiscImageBuilder.Build("Real Title", boot);
        File.WriteAllBytes(path, image);

        var text = Encoding.ASCII.GetString(image);
        Assert.Contains("BOOT2 = cdrom0:\\SLES_538.86;1", text);
        Assert.DoesNotContain("SLES", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);
        Assert.Equal("SLES-53886", item.DiscId);
        Assert.Equal("SLES", item.Region);
        Assert.Equal("Real Title", item.DetectedTitle);
        Assert.False(item.TitleIsFilenameFallback);
        Assert.Equal(QueueItemStatus.Ready, item.Status);
    }

    [Fact]
    public async Task FilenameIsNotADiscId()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "SLES_538.86.iso");
        File.WriteAllBytes(path, DiscImageBuilder.Build("Some Game", systemCnf: null));

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Null(item.DiscId);
        Assert.Null(item.Region);
        Assert.False(item.TitleIsFilenameFallback);
        Assert.Equal("Some Game", item.DetectedTitle);
        Assert.Equal(QueueItemStatus.NeedsMetadataReview, item.Status);
        Assert.Contains("Unidentified", item.Detail);

        var blank = WriteIso(tree, "SLES_111.11.iso", "   ", cnf: null);
        var fallback = Assert.Single((await ScanAsync(tree, new ScanInput(blank, ScanDepth.File))).Items);
        Assert.Null(fallback.DiscId);
        Assert.True(fallback.TitleIsFilenameFallback);
        Assert.Equal("SLES_111.11", fallback.DetectedTitle);
    }

    [Fact]
    public async Task UnknownIdIsNotInvented()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "game.iso");
        File.WriteAllBytes(path, DiscImageBuilder.Build("Game", "BOOT2 = cdrom0:\\NOT_A_DISC;1\n"));

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Null(item.DiscId);
        Assert.NotEqual("NOT_-A_DI", item.DiscId);
        Assert.Equal(QueueItemStatus.NeedsMetadataReview, item.Status);
    }

    [Fact]
    public async Task RegionIsNotTakenFromTheTitle()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "europe.iso");
        var image = DiscImageBuilder.Build(
            "SLES_538.86 USA",
            "BOOT2 = cdrom0:\\SLUS_200.01;1\n");
        File.WriteAllBytes(path, image);
        Assert.Contains("SLES_538.86", Encoding.ASCII.GetString(image));

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Equal("SLUS-20001", item.DiscId);
        Assert.Equal("SLUS", item.Region);
        Assert.Equal("SLES_538.86 USA", item.DetectedTitle);
        Assert.NotEqual("SLES-53886", item.DiscId);
    }

    [Fact]
    public async Task NormalisedFormsMatchAndAnotherRegionDoesNot()
    {
        using var tree = new TempTree();
        var underscore = WriteIso(tree, "a-underscore.iso", "Game", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var hyphen = WriteIso(tree, "b-hyphen.iso", "Game", "BOOT2 = cdrom0:\\SLES-53886;1\n");
        var ntsc = WriteIso(tree, "c-ntsc.iso", "European Edition", "BOOT2 = cdrom0:\\SLUS_538.86;1\n");

        var items = (await ScanAsync(
            tree,
            new ScanInput(underscore, ScanDepth.File),
            new ScanInput(hyphen, ScanDepth.File),
            new ScanInput(ntsc, ScanDepth.File))).Items;

        var pal = items.Where(item => item.DiscId == "SLES-53886").ToList();
        Assert.Equal(2, pal.Count);
        Assert.Equal(1, pal.Count(item => item.Status == QueueItemStatus.Ready));
        Assert.Equal(1, pal.Count(item => item.Status == QueueItemStatus.Duplicate && item.DuplicateStatus == DuplicateMatch.InQueue));

        var other = Assert.Single(items, item => item.SourceFile == ntsc);
        Assert.Equal("SLUS-53886", other.DiscId);
        Assert.Equal("SLUS", other.Region);
        Assert.Equal(QueueItemStatus.Ready, other.Status);
        Assert.Equal("European Edition", other.DetectedTitle);
    }

    [Fact]
    public async Task BinAndCueCollapseToOneEntry()
    {
        using var tree = new TempTree();
        var bin = Path.Combine(tree.Root, "not-an-id.bin");
        var cue = Path.Combine(tree.Root, "not-an-id.cue");
        File.WriteAllBytes(bin, DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Bin Game", "BOOT2 = cdrom0:\\SCES_500.01;1\n")));
        await File.WriteAllTextAsync(cue, "FILE \"not-an-id.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");

        var selected = await ScanAsync(tree, new ScanInput(bin, ScanDepth.File), new ScanInput(cue, ScanDepth.File));
        var fromFiles = Assert.Single(selected.Items);
        Assert.Equal(Path.GetFullPath(cue), fromFiles.SourceFile);
        Assert.Equal(Path.GetFullPath(bin), fromFiles.DataFile);
        Assert.Equal("SCES-50001", fromFiles.DiscId);
        Assert.Equal(MediaKind.Cd, fromFiles.Media);
        Assert.Equal(QueueItemStatus.Ready, fromFiles.Status);

        var fromDirectory = await ScanAsync(tree, new ScanInput(tree.Root, ScanDepth.Directory));
        Assert.Single(fromDirectory.Items);
    }

    [Fact]
    public async Task UnsupportedMultitrackIsReportedAndNotStripped()
    {
        using var tree = new TempTree();
        var bin = Path.Combine(tree.Root, "multi.bin");
        var cue = Path.Combine(tree.Root, "multi.cue");
        var data = DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Multi", "BOOT2 = cdrom0:\\SLES_538.86;1\n"));
        File.WriteAllBytes(bin, DiscImageBuilder.AppendRawSectors(data, 2));
        const string cueText = "FILE \"multi.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 01 00:10:00\n";
        await File.WriteAllTextAsync(cue, cueText);
        var before = await File.ReadAllBytesAsync(bin);
        var namesBefore = Directory.GetFiles(tree.Root).Order(StringComparer.Ordinal).ToArray();

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(tree.Root, ScanDepth.Directory))).Items);

        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Equal(MediaKind.CdMultiTrack, item.Media);
        Assert.Contains("Unsupported", item.Detail);
        Assert.Contains("not removed", item.Detail);
        Assert.Equal(before, await File.ReadAllBytesAsync(bin));
        Assert.Equal(cueText, await File.ReadAllTextAsync(cue));
        Assert.Contains("AUDIO", await File.ReadAllTextAsync(cue));
        Assert.Equal(before.LongLength, item.ConvertedSize);
        Assert.Equal(namesBefore, Directory.GetFiles(tree.Root).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SeparateTrackFilesAreNotMerged()
    {
        using var tree = new TempTree();
        var data = Path.Combine(tree.Root, "data.bin");
        var audio = Path.Combine(tree.Root, "audio.bin");
        var cue = Path.Combine(tree.Root, "split.cue");
        await File.WriteAllBytesAsync(data, DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Split", "BOOT2 = cdrom0:\\SLES_538.86;1\n")));
        await File.WriteAllBytesAsync(audio, new byte[2352]);
        await File.WriteAllTextAsync(
            cue,
            "FILE \"data.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\nFILE \"audio.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");
        var audioBefore = await File.ReadAllBytesAsync(audio);

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(tree.Root, ScanDepth.Directory))).Items);

        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Contains("not merged", item.Detail);
        Assert.Equal(audioBefore, await File.ReadAllBytesAsync(audio));
        Assert.Equal(2, Directory.GetFiles(tree.Root, "*.bin").Length);
    }

    [Fact]
    public async Task Dvd9KeepsTwoLayerCounts()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "dual.iso", "Dual", "BOOT2 = cdrom0:\\SLES_538.86;1\n", layer1Sectors: 8);
        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);
        var fileSectors = (uint)(new FileInfo(path).Length / 2048);

        Assert.Equal(MediaKind.Dvd9, item.Media);
        Assert.Equal(DiscImageBuilder.BaseSectors, item.Layer0Sectors);
        Assert.Equal(8u, item.Layer1Sectors);
        Assert.Equal(fileSectors, item.Layer0Sectors + item.Layer1Sectors);
        Assert.NotEqual(fileSectors, item.Layer0Sectors);
        Assert.Equal(HdlDiscTypes.Ps2Dvd, item.HdlDiscType);
        Assert.Equal(QueueItemStatus.Ready, item.Status);
    }

    [Fact]
    public async Task TrailingDataIsNotASecondDvdLayer()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "padded.iso", "Pad", "BOOT2 = cdrom0:\\SLES_538.86;1\n", extraSectors: 2);
        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Equal(MediaKind.Dvd5, item.Media);
        Assert.Equal(0u, item.Layer1Sectors);
        Assert.Equal((uint)(new FileInfo(path).Length / 2048), item.Layer0Sectors);
        Assert.Equal(HdlDiscTypes.Ps2Dvd, item.HdlDiscType);
    }

    [Fact]
    public async Task MediaKindComesFromStructureNotTheExtension()
    {
        using var tree = new TempTree();
        var cdNamedIso = Path.Combine(tree.Root, "disc.iso");
        var dvdNamedBin = Path.Combine(tree.Root, "disc.bin");
        await File.WriteAllBytesAsync(cdNamedIso, DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Cd", "BOOT2 = cdrom0:\\SLES_538.86;1\n")));
        await File.WriteAllBytesAsync(dvdNamedBin, DiscImageBuilder.Build("Dvd", "BOOT2 = cdrom0:\\SLUS_200.02;1\n"));

        var items = (await ScanAsync(
            tree,
            new ScanInput(cdNamedIso, ScanDepth.File),
            new ScanInput(dvdNamedBin, ScanDepth.File))).Items;

        Assert.Equal(MediaKind.Cd, Assert.Single(items, item => item.SourceFile == cdNamedIso).Media);
        Assert.Equal(MediaKind.Dvd5, Assert.Single(items, item => item.SourceFile == dvdNamedBin).Media);
    }

    [Fact]
    public async Task MultiDiscComesFromTheVolumeDescriptor()
    {
        using var tree = new TempTree();
        var structural = WriteIso(tree, "game.iso", "Set", "BOOT2 = cdrom0:\\SLES_538.86;1\n", volumeSetSize: 2, volumeSequence: 2);
        var named = WriteIso(tree, "Game (Disc 2).iso", "Single", "BOOT2 = cdrom0:\\SLUS_200.02;1\n");
        var items = (await ScanAsync(tree, new ScanInput(structural, ScanDepth.File), new ScanInput(named, ScanDepth.File))).Items;

        var fromStructure = Assert.Single(items, item => item.SourceFile == structural);
        Assert.Equal(2, fromStructure.VolumeSetSize);
        Assert.Equal(2, fromStructure.VolumeSequence);
        Assert.Contains("disc 2 of 2", fromStructure.MediaLabel);

        var fromName = Assert.Single(items, item => item.SourceFile == named);
        Assert.Equal(1, fromName.VolumeSetSize);
        Assert.DoesNotContain("disc", fromName.MediaLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptImageDoesNotInventAnId()
    {
        using var tree = new TempTree();
        var path = Path.Combine(tree.Root, "SLES_538.86.iso");
        await File.WriteAllBytesAsync(path, new byte[32]);

        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Null(item.DiscId);
    }

    [Fact]
    public async Task AmbiguousBoot2DoesNotPickAnId()
    {
        using var tree = new TempTree();
        var path = WriteIso(
            tree,
            "ambiguous.iso",
            "Ambiguous",
            "BOOT2 = cdrom0:\\SLES_538.86;1\nBOOT2 = cdrom0:\\SLUS_200.01;1\n");
        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Null(item.DiscId);
        Assert.Equal("Ambiguous", item.DetectedTitle);
        Assert.False(item.TitleIsFilenameFallback);
        Assert.Equal(QueueItemStatus.NeedsMetadataReview, item.Status);
        Assert.Contains("Ambiguous", item.Detail);
    }

    [Fact]
    public async Task TheTitleIsTheVolumeTitle()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "long-name.iso", "Hello World Extra", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Equal("Hello World Extra", item.DetectedTitle);
        Assert.True(item.DetectedTitle.Length <= 80);
        Assert.False(item.TitleIsFilenameFallback);
        Assert.Equal(InstallationState.NotInstalled, item.InstallationStatus);
    }

    [Fact]
    public async Task BlankVolumeLabelUsesTheDiscIdNotTheFilename()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "not-the-title.iso", "   ", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var item = Assert.Single((await ScanAsync(tree, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Equal("SLES-53886", item.DetectedTitle);
        Assert.False(item.TitleIsFilenameFallback);
    }

    [Fact]
    public async Task DuplicateUsesTheInstalledIndexWithoutAConnection()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "installed.iso", "Installed", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var index = new InstalledGameIndex();
        Assert.Equal(AddGameResult.Added, index.TryAdd("SLES-53886", "already.iso"));

        var item = Assert.Single((await ScanAsync(tree, index, new ScanInput(path, ScanDepth.File))).Items);

        Assert.Equal(QueueItemStatus.Duplicate, item.Status);
        Assert.Equal(DuplicateMatch.InLibrary, item.DuplicateStatus);
        Assert.Equal(InstallationState.Installed, item.InstallationStatus);
    }

    [Fact]
    public async Task AGameThisQueueInstalledReadsAsInstalledUntilItLeavesThePs2()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "installed.iso", "Installed", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var queued = Assert.Single((await ScanAsync(tree, new InstalledGameIndex(), new ScanInput(path, ScanDepth.File))).Items) with { InstalledFromQueue = true };
        var index = new InstalledGameIndex();
        Assert.Equal(AddGameResult.Added, index.TryAdd("SLES-53886", "installed.iso"));

        var there = Assert.Single(DiscScanner.ApplyDuplicates([queued], index));
        Assert.Equal(QueueItemStatus.Installed, there.Status);
        Assert.Equal(DuplicateMatch.None, there.DuplicateStatus);
        Assert.Equal(InstallationState.Installed, there.InstallationStatus);

        var removed = Assert.Single(DiscScanner.ApplyDuplicates([queued], new InstalledGameIndex()));
        Assert.Equal(QueueItemStatus.Ready, removed.Status);
    }

    [Fact]
    public async Task ADiscWithAnIncompleteInstallOnThePs2StaysReadyAndSaysInstallingReplacesIt()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "incomplete.iso", "Incomplete", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var queued = Assert.Single((await ScanAsync(tree, new InstalledGameIndex(), new ScanInput(path, ScanDepth.File))).Items);
        var incomplete = new InstalledGameIndex();
        Assert.Equal(AddGameResult.Added, incomplete.TryAdd("SLES-53886", null));

        var row = Assert.Single(DiscScanner.ApplyDuplicates([queued], new InstalledGameIndex(), incomplete));
        Assert.Equal(QueueItemStatus.Ready, row.Status);
        Assert.Equal("Incomplete", row.InstallationLabel);
        Assert.Equal(QueuePreviewItem.IncompleteDetail, row.FriendlyDetail);
        Assert.NotNull(BayQueueInstaller.RequestFor(row));

        var removed = Assert.Single(DiscScanner.ApplyDuplicates([queued], new InstalledGameIndex(), new InstalledGameIndex()));
        Assert.Equal(QueueItemStatus.Ready, removed.Status);
        Assert.Equal("Not installed", removed.InstallationLabel);
        Assert.Empty(removed.FriendlyDetail);
    }

    [Fact]
    public async Task AWholeCopyOnThePs2CountsOverAnIncompleteOne()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "both.iso", "Both", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var queued = Assert.Single((await ScanAsync(tree, new InstalledGameIndex(), new ScanInput(path, ScanDepth.File))).Items);
        var installed = new InstalledGameIndex();
        var incomplete = new InstalledGameIndex();
        installed.TryAdd("SLES-53886", "both.iso");
        incomplete.TryAdd("SLES-53886", null);

        var row = Assert.Single(DiscScanner.ApplyDuplicates([queued], installed, incomplete));

        Assert.Equal(QueueItemStatus.Duplicate, row.Status);
        Assert.Equal(InstallationState.Installed, row.InstallationStatus);
    }

    [Fact]
    public async Task AGameBeingInstalledReadsAsInstallingAndNoLongerAsReady()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "installing.iso", "Installing", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var queued = Assert.Single((await ScanAsync(tree, new InstalledGameIndex(), new ScanInput(path, ScanDepth.File))).Items) with { Installing = true };

        var row = Assert.Single(DiscScanner.ApplyDuplicates([queued], new InstalledGameIndex()));

        Assert.Equal(QueueItemStatus.Installing, row.Status);
        Assert.Equal("Installing", row.StatusLabel);
        Assert.Null(BayQueueInstaller.RequestFor(row));
    }

    [Fact]
    public async Task AnImageAlreadyInTheQueueIsNotReadAgain()
    {
        using var tree = new TempTree();
        var queued = WriteIso(tree, "queued.iso", "Queued", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var fresh = WriteIso(tree, "fresh.iso", "Fresh", "BOOT2 = cdrom0:\\SLES_100.01;1\n");

        var result = await DiscScanner.ScanAsync(
            [new ScanInput(tree.Root, ScanDepth.Directory)],
            null,
            new DiscScanOptions { Queued = new HashSet<string>([queued], StringComparer.OrdinalIgnoreCase) });

        Assert.Equal(fresh, Assert.Single(result.Items).SourceFile);
        Assert.Equal([queued], result.AlreadyQueued);
    }

    [Fact]
    public async Task ADiscInTheQueueOrOnThePs2IsLeftOut()
    {
        using var tree = new TempTree();
        var queued = (await ScanAsync(tree, new ScanInput(WriteIso(tree, "a.iso", "A", "BOOT2 = cdrom0:\\SLES_538.86;1\n"), ScanDepth.File))).Items;
        var added = (await ScanAsync(
            tree,
            new ScanInput(WriteIso(tree, "b-same-disc.iso", "B", "BOOT2 = cdrom0:\\SLES-53886;1\n"), ScanDepth.File),
            new ScanInput(WriteIso(tree, "c-installed.iso", "C", "BOOT2 = cdrom0:\\SLUS_200.43;1\n"), ScanDepth.File),
            new ScanInput(WriteIso(tree, "d-new.iso", "D", "BOOT2 = cdrom0:\\SLUS_100.01;1\n"), ScanDepth.File),
            new ScanInput(WriteIso(tree, "e-new-again.iso", "E", "BOOT2 = cdrom0:\\SLUS_100.01;1\n"), ScanDepth.File))).Items;
        var installed = new InstalledGameIndex();
        installed.TryAdd("SLUS-20043", "PP.SLUS-20043.HDL.Game");

        var (kept, leftOut) = DiscScanner.WithoutDuplicates(queued, added, installed);

        Assert.Equal(["SLUS-10001"], kept.Select(item => item.DiscId));
        Assert.Equal(QueueItemStatus.Ready, Assert.Single(kept).Status);
        Assert.Equal(
            [("SLES-53886", DuplicateMatch.InQueue), ("SLUS-20043", DuplicateMatch.InLibrary), ("SLUS-10001", DuplicateMatch.InQueue)],
            leftOut.Select(item => (item.DiscId, item.DuplicateStatus)));
    }

    [Fact]
    public async Task SelectionCanBeFilesOrDirectories()
    {
        using var tree = new TempTree();
        var top = WriteIso(tree, "top.iso", "Top", "BOOT2 = cdrom0:\\SLES_100.01;1\n");
        var nestedDir = Path.Combine(tree.Root, "nested");
        Directory.CreateDirectory(nestedDir);
        var nested = Path.Combine(nestedDir, "nested.iso");
        await File.WriteAllBytesAsync(nested, DiscImageBuilder.Build("Nested", "BOOT2 = cdrom0:\\SLES_100.02;1\n"));
        await File.WriteAllTextAsync(Path.Combine(tree.Root, "notes.txt"), "ignore");
        var other = WriteIso(tree, "other.iso", "Other", "BOOT2 = cdrom0:\\SLES_100.03;1\n");

        var several = await ScanAsync(tree, new ScanInput(top, ScanDepth.File), new ScanInput(other, ScanDepth.File));
        Assert.Equal(2, several.Items.Count);

        var shallow = await ScanAsync(tree, new ScanInput(tree.Root, ScanDepth.Directory));
        Assert.Equal(2, shallow.Items.Count);
        Assert.DoesNotContain(shallow.Items, item => item.SourceFile == nested);

        var deep = await ScanAsync(tree, new ScanInput(tree.Root, ScanDepth.DirectoryRecursive));
        Assert.Equal(3, deep.Items.Count);
        Assert.Contains(deep.Items, item => item.SourceFile == nested);
        Assert.DoesNotContain(deep.Items, item => item.SourceFile.EndsWith(".txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressReportsTheFileBeingScanned()
    {
        using var tree = new TempTree();
        var path = WriteIso(tree, "one.iso", "One", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var seen = new ListProgress();

        await DiscScanner.ScanAsync([new ScanInput(path, ScanDepth.File)], null, new DiscScanOptions(), seen);

        Assert.Contains(seen.Reports, report => report.CurrentPath == path && report.Total == 1);
    }

    [Fact]
    public async Task ChdKindFollowsMetadataNotTheFilename()
    {
        using var tree = new TempTree();
        var dvdNamed = WriteDvdChd(tree, "audio.cd.chd", "Dvd", "BOOT2 = cdrom0:\\SLUS_200.01;1\n");
        var cdNamed = WriteCdChd(tree, "game.iso.chd");

        var result = await ScanChdAsync(tree, dvdNamed, cdNamed);

        var dvd = Assert.Single(result.Items, item => item.SourceFile == dvdNamed);
        Assert.Equal(dvdNamed, dvd.DataFile);
        Assert.Equal("SLUS-20001", dvd.DiscId);
        var cd = Assert.Single(result.Items, item => item.SourceFile == cdNamed);
        Assert.Equal(cdNamed, cd.DataFile);
        Assert.Equal("SLES-53886", cd.DiscId);
    }

    [Fact]
    public async Task AChdIsReadInPlaceWithNothingWritten()
    {
        using var tree = new TempTree();
        var dvd = WriteDvdChd(tree, "dvd.chd", "Dvd", "BOOT2 = cdrom0:\\SLUS_200.01;1\n");
        var cd = WriteCdChd(tree, "cd.chd");
        var before = Directory.GetFiles(tree.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var result = await ScanChdAsync(tree, dvd, cd);

        Assert.All(result.Items, item => Assert.Equal(QueueItemStatus.Ready, item.Status));
        var after = Directory.GetFiles(tree.Root, "*", SearchOption.AllDirectories);
        Assert.Equal(before.Keys.Order(), after.Order());
        Assert.All(after, path => Assert.Equal(before[path], File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task ACdChdIsReadThroughTheSheetChdmanWouldWrite()
    {
        using var tree = new TempTree();
        var chd = WriteCdChd(tree, "game.chd");

        var item = Assert.Single((await ScanChdAsync(tree, chd)).Items);

        Assert.Equal(QueueItemStatus.Ready, item.Status);
        Assert.Equal("SLES-53886", item.DiscId);
        Assert.Equal(chd, item.DataFile);

        // An install reads the 2048-byte user data of each sector, as from the bin chdman would write.
        using var sectors = IsoUserDataStream.Open(chd);
        var first = new byte[IsoUserDataStream.UserSectorSize * 17];
        sectors.ReadExactly(first);
        Assert.Equal("CD001"u8.ToArray(), first.AsSpan((16 * IsoUserDataStream.UserSectorSize) + 1, 5).ToArray());
    }

    [Fact]
    public async Task ACdWithAudioTracksIsReportedAsMultiTrack()
    {
        using var tree = new TempTree();
        var data = DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Cd", "BOOT2 = cdrom0:\\SLES_538.86;1\n"));
        var chd = Path.Combine(tree.Root, "audio.chd");
        ChdTestFile.WriteCd(chd, DiscImageBuilder.AppendRawSectors(data, 150), ("MODE2_RAW", data.Length / 2352), ("AUDIO", 150));

        var item = Assert.Single((await ScanChdAsync(tree, chd)).Items);

        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Contains("multi-track", item.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractedDiscIdComesFromTheIsoNotTheChdName()
    {
        using var tree = new TempTree();
        var chd = WriteDvdChd(tree, "SLES_999.99.chd", "From The Disc", "BOOT2 = cdrom0:\\SLUS_200.01;1\n");

        var item = Assert.Single((await ScanChdAsync(tree, chd)).Items);

        Assert.Equal("SLUS-20001", item.DiscId);
        Assert.False(item.TitleIsFilenameFallback);
        Assert.Equal("From The Disc", item.DetectedTitle);
    }

    [Fact]
    public async Task DamageWhereTheInstallReadsStopsIt()
    {
        using var tree = new TempTree();
        var chd = Path.Combine(tree.Root, "damaged.chd");
        var damaged = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Images", "ChdFixtures", "dvd-zlib.chd"));
        damaged[damaged.Length / 2] ^= 0x5A;
        await File.WriteAllBytesAsync(chd, damaged);

        // The scan reads only the disc's first sectors, so the damage shows when the install reaches it.
        Assert.Single((await ScanChdAsync(tree, chd)).Items);
        using var sectors = IsoUserDataStream.Open(chd);
        var ex = Assert.Throws<ChdException>(() => sectors.CopyTo(Stream.Null));
        Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatIsNotAChdSaysSo()
    {
        using var tree = new TempTree();
        var chd = Path.Combine(tree.Root, "game.chd");
        await File.WriteAllTextAsync(chd, "not-a-real-chd");

        var item = Assert.Single((await ScanChdAsync(tree, chd)).Items);

        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Contains("not a complete chd", item.Detail, StringComparison.Ordinal);
        Assert.Equal("game", item.DetectedTitle);
    }

    [Theory]
    [InlineData("GDDD", "not a PS2")]
    [InlineData("XXXX", "does not say")]
    public async Task OtherKindsOfChdAreRefused(string tag, string detail)
    {
        using var tree = new TempTree();
        var chd = Path.Combine(tree.Root, "game.chd");
        ChdTestFile.WriteTagged(chd, DiscImageBuilder.Build("Game", "BOOT2 = cdrom0:\\SLES_538.86;1\n"), tag);

        var item = Assert.Single((await ScanChdAsync(tree, chd)).Items);

        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Contains(detail, item.Detail, StringComparison.Ordinal);
        Assert.Null(item.DiscId);
    }

    [Fact]
    public async Task ACancelledScanStopsBetweenImages()
    {
        using var tree = new TempTree();
        var first = WriteDvdChd(tree, "a.chd", "Big", "BOOT2 = cdrom0:\\SLES_538.86;1\n");
        var second = WriteDvdChd(tree, "b.chd", "Other", "BOOT2 = cdrom0:\\SLES_538.87;1\n");
        using var cancel = new CancellationTokenSource();

        var result = await DiscScanner.ScanAsync(
            [new ScanInput(first, ScanDepth.File), new ScanInput(second, ScanDepth.File)],
            null,
            new DiscScanOptions(),
            new CancelOnFirstReport(cancel),
            cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(first, Assert.Single(result.Items).SourceFile);
    }

    private static async Task<DiscScanResult> ScanAsync(TempTree tree, params ScanInput[] inputs)
        => await ScanAsync(tree, null, inputs);

    private static async Task<DiscScanResult> ScanAsync(TempTree tree, InstalledGameIndex? installed, params ScanInput[] inputs)
        => await DiscScanner.ScanAsync(inputs, installed, new DiscScanOptions());

    private static async Task<DiscScanResult> ScanChdAsync(TempTree tree, params string[] chds)
    {
        var inputs = chds.Select(path => new ScanInput(path, ScanDepth.File)).ToArray();
        return await DiscScanner.ScanAsync(inputs, null, new DiscScanOptions());
    }

    private static string WriteDvdChd(TempTree tree, string name, string label, string cnf, int extraSectors = 0, int trailingBytes = 0)
    {
        var path = Path.Combine(tree.Root, name);
        ChdTestFile.WriteDvd(path, DiscImageBuilder.Build(label, cnf, extraSectors: extraSectors), trailingBytes);
        return path;
    }

    private static string WriteCdChd(TempTree tree, string name)
    {
        var path = Path.Combine(tree.Root, name);
        var raw = DiscImageBuilder.WrapMode2(DiscImageBuilder.Build("Cd", "BOOT2 = cdrom0:\\SLES_538.86;1\n"));
        ChdTestFile.WriteCd(path, raw, ("MODE2_RAW", raw.Length / 2352));
        return path;
    }

    private static string WriteIso(
        TempTree tree,
        string name,
        string label,
        string? cnf,
        int extraSectors = 0,
        int layer1Sectors = 0,
        ushort volumeSetSize = 1,
        ushort volumeSequence = 1)
    {
        var path = Path.Combine(tree.Root, name);
        File.WriteAllBytes(path, DiscImageBuilder.Build(label, cnf, volumeSetSize, volumeSequence, extraSectors, layer1Sectors));
        return path;
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Directory.CreateTempSubdirectory("loadout-scan-").FullName;
        }

        public string Root { get; }

        public void Dispose()
        {
            TempDirectory.Delete(Root);
        }
    }
}

internal sealed class ListProgress : IProgress<ScanProgress>
{
    public List<ScanProgress> Reports { get; } = new();

    public void Report(ScanProgress value) => Reports.Add(value);
}

internal sealed class CancelOnFirstReport : IProgress<ScanProgress>
{
    private readonly CancellationTokenSource _cancel;

    public CancelOnFirstReport(CancellationTokenSource cancel) => _cancel = cancel;

    public void Report(ScanProgress value) => _cancel.Cancel();
}
