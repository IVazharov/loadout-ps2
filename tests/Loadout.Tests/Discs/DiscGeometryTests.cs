namespace Loadout.Discs;

public class DiscGeometryTests
{
    [Fact]
    public void ADvdsDataRunsFrom24To58Millimetres()
    {
        Assert.Equal(24, DiscGeometry.RadiusMm(0, DiscMedia.Dvd), 3);
        Assert.Equal(58, DiscGeometry.RadiusMm(DiscGeometry.FullDvdLayerSectors, DiscMedia.Dvd), 3);
    }

    [Fact]
    public void ProStreetsWeakAreasWereAbout35And49MillimetresOut()
    {
        Assert.Equal(35.6, DiscGeometry.RadiusMm(570_000, DiscMedia.Dvd), 1);
        Assert.Equal(49.0, DiscGeometry.RadiusMm(1_500_000, DiscMedia.Dvd), 1);
    }

    [Fact]
    public void ACdsDataStartsAt25Millimetres()
    {
        Assert.Equal(25, DiscGeometry.RadiusMm(0, DiscMedia.Cd), 3);
        Assert.Equal(58, DiscGeometry.RadiusMm(DiscGeometry.FullCdSectors, DiscMedia.Cd), 3);
    }

    [Fact]
    public void LayerOneRunsBackFromTheEdge()
    {
        const long layer0 = 2_000_000;

        Assert.Equal(DiscGeometry.RadiusMm(layer0 - 1, DiscMedia.Dvd), DiscGeometry.RadiusMm(layer0, DiscMedia.Dvd, layer0), 6);
        Assert.Equal(DiscGeometry.RadiusMm(0, DiscMedia.Dvd), DiscGeometry.RadiusMm((2 * layer0) - 1, DiscMedia.Dvd, layer0), 6);
        Assert.Equal(DiscGeometry.RadiusMm(500, DiscMedia.Dvd), DiscGeometry.RadiusMm(500, DiscMedia.Dvd, layer0), 6);
    }
}
