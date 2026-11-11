namespace Loadout.Discs;

public class DiscCopyMapTests
{
    [Fact]
    public void ANewMapHasEverySectorUntried()
    {
        var map = DiscCopyMap.New(1000, "SLES-50510 1000 abc");

        Assert.Equal([new DiscMapRange(0, 1000, DiscSectorState.Untried)], map.Ranges);
        Assert.Equal(1000, map.UntriedSectors);
        Assert.Equal(0, map.ReadSectors);
        Assert.False(map.IsComplete);
        Assert.Equal(-1, map.FirstReadSector());
    }

    [Fact]
    public void MarkingSplitsStretchesAndJoinsEqualNeighbours()
    {
        var map = DiscCopyMap.New(1000, "x");

        map.Mark(100, 50, DiscSectorState.ReadNormally);
        map.Mark(150, 50, DiscSectorState.ReadNormally);
        map.Mark(300, 10, DiscSectorState.Failed);

        Assert.Equal(
            [
                new DiscMapRange(0, 100, DiscSectorState.Untried),
                new DiscMapRange(100, 100, DiscSectorState.ReadNormally),
                new DiscMapRange(200, 100, DiscSectorState.Untried),
                new DiscMapRange(300, 10, DiscSectorState.Failed),
                new DiscMapRange(310, 690, DiscSectorState.Untried),
            ],
            map.Ranges);

        map.Mark(0, 100, DiscSectorState.ReadNormally);
        map.Mark(200, 100, DiscSectorState.ReadNormally);
        map.Mark(305, 700 - 5, DiscSectorState.ReadLater);

        Assert.Equal(
            [
                new DiscMapRange(0, 300, DiscSectorState.ReadNormally),
                new DiscMapRange(300, 5, DiscSectorState.Failed),
                new DiscMapRange(305, 695, DiscSectorState.ReadLater),
            ],
            map.Ranges);
        Assert.Equal(DiscSectorState.Failed, map.StateAt(304));
        Assert.Equal(DiscSectorState.ReadLater, map.StateAt(999));
        Assert.Equal(5, map.MissingSectors);
        Assert.Equal(995, map.ReadSectors);
        Assert.Equal(0, map.FirstReadSector());
    }

    [Fact]
    public void MarkingAcrossSeveralStretchesReplacesThemAll()
    {
        var map = DiscCopyMap.New(100, "x");
        for (var s = 0; s < 100; s += 10)
        {
            map.Mark(s, 5, DiscSectorState.Jumped);
        }

        map.Mark(3, 90, DiscSectorState.ReadBySector);

        Assert.Equal(
            [
                new DiscMapRange(0, 3, DiscSectorState.Jumped),
                new DiscMapRange(3, 90, DiscSectorState.ReadBySector),
                new DiscMapRange(93, 2, DiscSectorState.Jumped),
                new DiscMapRange(95, 5, DiscSectorState.Untried),
            ],
            map.Ranges);
    }

    [Fact]
    public void MarkingOutsideTheDiscIsRefused()
    {
        var map = DiscCopyMap.New(100, "x");

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Mark(90, 11, DiscSectorState.ReadNormally));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Mark(-1, 5, DiscSectorState.ReadNormally));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Mark(5, 0, DiscSectorState.ReadNormally));
    }

    [Fact]
    public void AMapReadsBackAsItWasSaved()
    {
        var map = DiscCopyMap.New(1000, "SLES-55002 1809376 0123456789abcdef");
        map.Mark(0, 600, DiscSectorState.ReadNormally);
        map.Mark(600, 16, DiscSectorState.Failed);
        map.Mark(616, 100, DiscSectorState.Jumped);
        map.Mark(716, 4, DiscSectorState.ReadAfterCleaning);
        map.StartOver(cleaned: true);
        map.Sweep = 3;
        map.SweepPosition = 640;

        var copy = DiscCopyMap.Parse(map.Format());

        Assert.Equal(map.Ranges, copy.Ranges);
        Assert.Equal("SLES-55002 1809376 0123456789abcdef", copy.Identity);
        Assert.Equal(3, copy.Sweep);
        Assert.Equal(640, copy.SweepPosition);
        Assert.Equal(1, copy.Cleanings);
        Assert.Equal(map.Format(), copy.Format());
    }

    [Fact]
    public void SavingReplacesTheFileInOneStep()
    {
        var folder = Path.Combine(Path.GetTempPath(), "loadout-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "game.map");
            var map = DiscCopyMap.New(100, "x");
            map.Save(path);
            map.Mark(0, 50, DiscSectorState.ReadNormally);
            map.Save(path);

            Assert.Equal(50, DiscCopyMap.Load(path).ReadSectors);
            Assert.False(File.Exists(path + ".new"));
        }
        finally
        {
            TempDirectory.Delete(folder);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("sectors 10\ndisc x\nsweep 0 -1\ncleanings 0\n0 10 ?\n")]
    [InlineData("loadout disc map 1\nsectors 10\ndisc x\nsweep 0 -1\ncleanings 0\n0 5 ?\n")]
    [InlineData("loadout disc map 1\nsectors 10\ndisc x\nsweep 0 -1\ncleanings 0\n0 5 ?\n6 4 +\n")]
    [InlineData("loadout disc map 1\nsectors 10\ndisc x\nsweep 0 -1\ncleanings 0\n0 10 Z\n")]
    [InlineData("loadout disc map 1\nsectors ten\ndisc x\nsweep 0 -1\ncleanings 0\n0 10 ?\n")]
    [InlineData("loadout disc map 1\nsectors 10\ndisc x\nsweep 0\ncleanings 0\n0 10 ?\n")]
    public void ABrokenMapIsRefused(string text) =>
        Assert.Throws<InvalidDataException>(() => DiscCopyMap.Parse(text));

    [Fact]
    public void TheProofOfConceptsFailedMarkReadsAsFailed()
    {
        var map = DiscCopyMap.Parse("loadout disc map 1\r\nsectors 10\r\ndisc x\r\nsweep 1 4\r\ncleanings 0\r\n0 4 +\r\n4 2 *\r\n6 4 -\r\n");

        Assert.Equal([new DiscMapRange(0, 4, DiscSectorState.ReadNormally), new DiscMapRange(4, 6, DiscSectorState.Failed)], map.Ranges);
    }

    [Fact]
    public void WhereTheDiscWasCleanedIsKeptOnceARing()
    {
        var map = DiscCopyMap.New(10, "x");

        map.StartOver(cleaned: true, [35.58, 48.95]);
        map.StartOver(cleaned: true, [35.9]);
        map.StartOver(cleaned: false, [20.0]);

        Assert.Equal([35.6, 49.0], map.CleanedRingsMm);
        Assert.Equal(2, map.Cleanings);
        var copy = DiscCopyMap.Parse(map.Format());
        Assert.Equal([35.6, 49.0], copy.CleanedRingsMm);
        Assert.Contains("cleaned-at 35.6 49.0\n", map.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void StartingOverAfterCleaningCountsTheCleaning()
    {
        var map = DiscCopyMap.New(10, "x");
        map.Sweep = 4;
        map.SweepPosition = 7;

        map.StartOver(cleaned: false);
        Assert.Equal((0, -1L, 0), (map.Sweep, map.SweepPosition, map.Cleanings));

        map.StartOver(cleaned: true);
        Assert.Equal(1, map.Cleanings);
    }
}
