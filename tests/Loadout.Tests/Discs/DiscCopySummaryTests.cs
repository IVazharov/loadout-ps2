namespace Loadout.Discs;

public class DiscCopySummaryTests
{
    [Fact]
    public void ACleanCopyWasAllReadOnTheFirstPass()
    {
        Assert.Equal("all on the first pass", DiscCopySummary.Describe([new DiscMapRange(0, 1000, DiscSectorState.ReadNormally)], 1000, 0));
    }

    [Fact]
    public void AScratchedCopyCountsItsWeakAreasAndCleanings()
    {
        // As NFS ProStreet: trouble around 1.16 GB and 3 GB, each in many small stretches, after two cleanings.
        var ranges = new List<DiscMapRange>
        {
            new(0, 568_320, DiscSectorState.ReadNormally),
            new(568_320, 300, DiscSectorState.ReadLater),
            new(568_620, 100, DiscSectorState.ReadNormally),
            new(568_720, 3_000, DiscSectorState.ReadAfterCleaning),
            new(571_720, 911_160, DiscSectorState.ReadNormally),
            new(1_482_880, 2_000, DiscSectorState.ReadBySector),
            new(1_484_880, 324_496, DiscSectorState.ReadNormally),
        };

        Assert.Equal("99.7% on the first pass, 2 weak areas, 2 cleanings", DiscCopySummary.Describe(ranges, 1_809_376, 2));
        Assert.Equal(2, DiscCopySummary.WeakAreas(ranges));
    }

    [Fact]
    public void ShareIsRoundedDownSoTroubleNeverReadsAsAll()
    {
        var ranges = new List<DiscMapRange> { new(0, 99_999, DiscSectorState.ReadNormally), new(99_999, 1, DiscSectorState.ReadLater) };

        Assert.Equal("99.9% on the first pass, 1 weak area", DiscCopySummary.Describe(ranges, 100_000, 0));
        Assert.Equal(
            "50% on the first pass, 1 weak area, 1 cleaning",
            DiscCopySummary.Describe([new DiscMapRange(0, 500, DiscSectorState.ReadNormally), new DiscMapRange(500, 500, DiscSectorState.ReadInPieces)], 1000, 1));
    }
}
