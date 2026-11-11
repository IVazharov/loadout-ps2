namespace Loadout.Discs;

/// <summary>Where a sector lies on the disc, as the distance from its centre.</summary>
public static class DiscGeometry
{
    /// <summary>A DVD's data starts 24 mm from the centre; a full single layer ends at 58 mm.</summary>
    public const double DvdDataStartMm = 24;

    /// <summary>A CD's program area starts 25 mm from the centre; an 80-minute CD ends at 58 mm.</summary>
    public const double CdDataStartMm = 25;

    public const double DataEndMm = 58;

    /// <summary>The sectors of a full DVD-5 layer (4.7 GB).</summary>
    public const long FullDvdLayerSectors = 2_295_104;

    /// <summary>The sectors of an 80-minute CD.</summary>
    public const long FullCdSectors = 360_000;

    /// <summary>
    /// The distance of a sector from the centre in millimetres. The track is a spiral of even pitch, so the area read grows
    /// with each sector: r² goes up evenly. On a two-layer DVD, layer 1 runs back from the edge towards the centre
    /// (opposite track path), so its sector s lies where layer 0's sector (layer 0's end - 1 - s) does.
    /// </summary>
    public static double RadiusMm(long sector, DiscMedia media, long layer0Sectors = 0)
    {
        if (layer0Sectors > 0 && sector >= layer0Sectors)
        {
            sector = Math.Max(0, layer0Sectors - 1 - (sector - layer0Sectors));
        }

        var (start, full) = media == DiscMedia.Cd ? (CdDataStartMm, FullCdSectors) : (DvdDataStartMm, FullDvdLayerSectors);
        return Math.Sqrt((start * start) + ((double)sector / full * ((DataEndMm * DataEndMm) - (start * start))));
    }
}
