namespace Loadout.Discs;

public class DiscMapShadesTests
{
    private static readonly DiscMapRange[] Ranges =
    [
        new(0, 400, DiscSectorState.ReadNormally),
        new(400, 100, DiscSectorState.ReadLater),
        new(500, 1, DiscSectorState.Failed),
        new(501, 99, DiscSectorState.ReadBySector),
        new(600, 400, DiscSectorState.Untried),
    ];

    [Fact]
    public void AFailureIsWorseThanNotReadYetWhichIsWorseThanAnyRead()
    {
        Assert.Equal(DiscMapShade.Failed, DiscMapShades.Worse(DiscMapShade.NotRead, DiscMapShade.Failed));
        Assert.Equal(DiscMapShade.NotRead, DiscMapShades.Worse(DiscMapShade.ReadAfterCleaning, DiscMapShade.NotRead));
        Assert.Equal(DiscMapShade.ReadBySector, DiscMapShades.Worse(DiscMapShade.ReadBySector, DiscMapShade.ReadLater));
        Assert.Equal(DiscMapShade.NotRead, DiscMapShades.Of(DiscSectorState.Jumped));
    }

    [Fact]
    public void AStretchShowsItsWorstSector()
    {
        Assert.Equal(DiscMapShade.ReadNormally, DiscMapShades.Worst(Ranges, 0, 400));
        Assert.Equal(DiscMapShade.ReadLater, DiscMapShades.Worst(Ranges, 399, 401));
        Assert.Equal(DiscMapShade.Failed, DiscMapShades.Worst(Ranges, 450, 550));
        Assert.Equal(DiscMapShade.NotRead, DiscMapShades.Worst(Ranges, 550, 650));
        Assert.Null(DiscMapShades.Worst(Ranges, 10, 10));
    }

    [Fact]
    public void EachColumnOfTheBarShowsItsWorstSector()
    {
        var columns = DiscMapShades.Columns(Ranges, 1000, 10);

        Assert.Equal(
            [
                DiscMapShade.ReadNormally, DiscMapShade.ReadNormally, DiscMapShade.ReadNormally, DiscMapShade.ReadNormally,
                DiscMapShade.ReadLater, DiscMapShade.Failed, DiscMapShade.NotRead, DiscMapShade.NotRead,
                DiscMapShade.NotRead, DiscMapShade.NotRead,
            ],
            columns);
    }

    [Fact]
    public void ASingleFailedSectorStillShowsOnAWideDisc()
    {
        var ranges = new DiscMapRange[]
        {
            new(0, 1_000_000, DiscSectorState.ReadNormally),
            new(1_000_000, 1, DiscSectorState.Failed),
            new(1_000_001, 809_375, DiscSectorState.ReadNormally),
        };

        var columns = DiscMapShades.Columns(ranges, 1_809_376, 800);

        Assert.Equal(1, columns.Count(c => c == DiscMapShade.Failed));
    }

    [Fact]
    public void TheLegendCountsSectorsPerShade()
    {
        var totals = DiscMapShades.Totals(Ranges);

        Assert.Equal(400, totals[DiscMapShade.ReadNormally]);
        Assert.Equal(1, totals[DiscMapShade.Failed]);
        Assert.Equal(400, totals[DiscMapShade.NotRead]);
        Assert.Equal(0, totals[DiscMapShade.ReadAfterCleaning]);
    }

    [Fact]
    public void ARingCoversTheSectorsAtItsDistance()
    {
        var at35 = DiscMapShades.SectorsAt(35.5, 35.7, DiscMedia.Dvd, 1_809_376);

        var (from, to) = Assert.Single(at35);
        Assert.InRange(from, 560_000, 575_000);
        Assert.InRange(to, from + 1, 580_000);
        Assert.Equal(35.5, DiscGeometry.RadiusMm(from, DiscMedia.Dvd), 1);
        Assert.Empty(DiscMapShades.SectorsAt(10, 20, DiscMedia.Dvd, 1_809_376));
        Assert.Empty(DiscMapShades.SectorsAt(57, 58, DiscMedia.Dvd, 1_809_376));
    }

    [Fact]
    public void OnATwoLayerDvdARingHasSectorsOnBothLayers()
    {
        const long layer0 = 2_000_000;

        var stretches = DiscMapShades.SectorsAt(40, 41, DiscMedia.Dvd, 2 * layer0, layer0);

        Assert.Equal(2, stretches.Count);
        var (from0, to0) = stretches[0];
        var (from1, to1) = stretches[1];
        Assert.Equal((2 * layer0) - to0, from1);
        Assert.Equal((2 * layer0) - from0, to1);
    }
}
