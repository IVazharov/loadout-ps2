namespace Loadout.Discs;

/// <summary>The disc map's shades, from the easiest read to failure, as the page colours them.</summary>
public enum DiscMapShade
{
    /// <summary>Soft blue.</summary>
    ReadNormally,

    /// <summary>Yellow: read on a later pass.</summary>
    ReadLater,

    /// <summary>Light amber: read in 32 KB pieces.</summary>
    ReadInPieces,

    /// <summary>Amber: read one sector at a time.</summary>
    ReadBySector,

    /// <summary>Deep amber: read only after the disc was cleaned.</summary>
    ReadAfterCleaning,

    /// <summary>Track grey: not read yet.</summary>
    NotRead,

    /// <summary>Red: a read failed here and it is still missing.</summary>
    Failed,
}

/// <summary>Turns a copy's map into the disc map's shades: per pixel of the bar, or per ring of the disc picture.</summary>
public static class DiscMapShades
{
    public static DiscMapShade Of(DiscSectorState state) => state switch
    {
        DiscSectorState.ReadNormally => DiscMapShade.ReadNormally,
        DiscSectorState.ReadLater => DiscMapShade.ReadLater,
        DiscSectorState.ReadInPieces => DiscMapShade.ReadInPieces,
        DiscSectorState.ReadBySector => DiscMapShade.ReadBySector,
        DiscSectorState.ReadAfterCleaning => DiscMapShade.ReadAfterCleaning,
        DiscSectorState.Failed => DiscMapShade.Failed,
        _ => DiscMapShade.NotRead,
    };

    /// <summary>The worse of two shades: a failure over anything, not read yet over any read, a harder read over an easier one.</summary>
    public static DiscMapShade Worse(DiscMapShade a, DiscMapShade b) => a >= b ? a : b;

    /// <summary>The worst shade of the sectors in [from, to), or null when the stretch is empty.</summary>
    public static DiscMapShade? Worst(IReadOnlyList<DiscMapRange> ranges, long from, long to)
    {
        if (to <= from || ranges.Count == 0)
        {
            return null;
        }

        DiscMapShade? worst = null;
        for (var i = FirstEndingAfter(ranges, from); i < ranges.Count && ranges[i].Start < to; i++)
        {
            var shade = Of(ranges[i].State);
            worst = worst is { } w ? Worse(w, shade) : shade;
        }

        return worst;
    }

    /// <summary>
    /// One shade per column of a bar <paramref name="columns"/> wide for a disc of <paramref name="sectors"/>: each column
    /// shows the worst shade of the sectors under it.
    /// </summary>
    public static DiscMapShade[] Columns(IReadOnlyList<DiscMapRange> ranges, long sectors, int columns)
    {
        var shades = new DiscMapShade[Math.Max(columns, 0)];
        if (columns <= 0 || sectors <= 0)
        {
            return shades;
        }

        var seen = new bool[columns];
        foreach (var range in ranges)
        {
            var first = (int)Math.Min(columns - 1, range.Start * columns / sectors);
            var last = (int)Math.Min(columns - 1, (range.End - 1) * columns / sectors);
            var shade = Of(range.State);
            for (var x = first; x <= last; x++)
            {
                shades[x] = seen[x] ? Worse(shades[x], shade) : shade;
                seen[x] = true;
            }
        }

        return shades;
    }

    /// <summary>How many sectors each shade has, for the legend.</summary>
    public static IReadOnlyDictionary<DiscMapShade, long> Totals(IReadOnlyList<DiscMapRange> ranges)
    {
        var totals = Enum.GetValues<DiscMapShade>().ToDictionary(s => s, _ => 0L);
        foreach (var range in ranges)
        {
            totals[Of(range.State)] += range.Count;
        }

        return totals;
    }

    /// <summary>
    /// The sectors at a distance from the centre: the ring [fromMm, toMm) of a disc, on layer 0, and on layer 1 of a
    /// two-layer DVD (which runs back from the edge). Empty stretches where the ring is outside the data.
    /// </summary>
    public static IReadOnlyList<(long From, long To)> SectorsAt(double fromMm, double toMm, DiscMedia media, long sectors, long layer0Sectors = 0)
    {
        var layer0 = layer0Sectors > 0 ? layer0Sectors : sectors;
        var first = Math.Max(0, (long)Math.Floor(SectorAt(fromMm, media)));
        var end = Math.Min(layer0, (long)Math.Ceiling(SectorAt(toMm, media)));
        var stretches = new List<(long, long)>();
        if (end > first)
        {
            stretches.Add((first, end));
        }

        if (layer0Sectors > 0 && end > first)
        {
            // Layer 1's sector s lies where layer 0's (layer0 - 1 - (s - layer0)) does.
            var from1 = Math.Max(layer0, (2 * layer0) - end);
            var to1 = Math.Min(sectors, (2 * layer0) - first);
            if (to1 > from1)
            {
                stretches.Add((from1, to1));
            }
        }

        return stretches;
    }

    /// <summary>The sector (as a fraction) at a distance from the centre on layer 0: the inverse of <see cref="DiscGeometry.RadiusMm"/>.</summary>
    public static double SectorAt(double mm, DiscMedia media)
    {
        var (start, full) = media == DiscMedia.Cd
            ? (DiscGeometry.CdDataStartMm, DiscGeometry.FullCdSectors)
            : (DiscGeometry.DvdDataStartMm, DiscGeometry.FullDvdLayerSectors);
        return ((mm * mm) - (start * start)) / ((DiscGeometry.DataEndMm * DiscGeometry.DataEndMm) - (start * start)) * full;
    }

    private static int FirstEndingAfter(IReadOnlyList<DiscMapRange> ranges, long sector)
    {
        int low = 0, high = ranges.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (ranges[middle].End <= sector)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
