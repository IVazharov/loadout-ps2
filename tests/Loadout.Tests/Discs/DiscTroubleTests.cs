using System.Globalization;

namespace Loadout.Discs;

public class DiscTroubleTests
{
    [Fact]
    public void ProStreetsTwoWeakZonesAreTwoRings()
    {
        var ranges = new DiscMapRange[]
        {
            new(0, 562_700, DiscSectorState.ReadNormally),
            new(562_700, 100, DiscSectorState.Failed),
            new(562_800, 10_000, DiscSectorState.ReadBySector),
            new(572_800, 50, DiscSectorState.Failed),
            new(572_850, 906_850, DiscSectorState.ReadNormally),
            new(1_479_700, 36_700, DiscSectorState.Jumped),
            new(1_516_400, 292_976, DiscSectorState.ReadNormally),
        };

        var trouble = DiscTrouble.From(ranges, DiscMedia.Dvd);

        Assert.Equal(2, trouble.RingsMm.Count);
        Assert.InRange(trouble.RingsMm[0], 35.3, 35.9);
        Assert.InRange(trouble.RingsMm[1], 48.7, 49.4);
        Assert.Equal(100 + 50 + 36_700, trouble.MissingSectors);
        Assert.Equal(3, trouble.Areas);
        Assert.StartsWith("about 3.6 and 4.9 cm", trouble.Where(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ACompleteCopyHasNoTrouble()
    {
        var trouble = DiscTrouble.From([new DiscMapRange(0, 1000, DiscSectorState.ReadNormally)], DiscMedia.Dvd);

        Assert.Empty(trouble.RingsMm);
        Assert.Equal(0, trouble.MissingSectors);
        Assert.Equal(string.Empty, trouble.Where(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void WhatCameBackAfterCleaningIsCountedNearItsRing()
    {
        var ranges = new DiscMapRange[]
        {
            new(0, 562_700, DiscSectorState.ReadNormally),
            new(562_700, 400, DiscSectorState.ReadAfterCleaning),
            new(563_100, 1_246_276, DiscSectorState.ReadNormally),
        };

        Assert.Equal(400, DiscTrouble.CameBackNear(ranges, 35.6, DiscMedia.Dvd));
        Assert.Equal(0, DiscTrouble.CameBackNear(ranges, 49.0, DiscMedia.Dvd));
    }

    [Fact]
    public void OneRingIsSaidAlone()
    {
        var trouble = DiscTrouble.From([new DiscMapRange(0, 100, DiscSectorState.ReadNormally), new DiscMapRange(100, 5, DiscSectorState.Failed), new DiscMapRange(105, 895, DiscSectorState.ReadNormally)], DiscMedia.Cd);

        Assert.Equal("about 2.5 cm from the centre", trouble.Where(CultureInfo.InvariantCulture));
    }
}
