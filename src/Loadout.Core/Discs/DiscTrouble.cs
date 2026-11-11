using System.Globalization;

namespace Loadout.Discs;

/// <summary>
/// Where a copy's missing sectors are on the disc, for the cleaning prompt: the rings to mark (distances from the centre),
/// how much is missing, and in how many separate areas.
/// </summary>
public sealed record DiscTrouble(IReadOnlyList<double> RingsMm, long MissingSectors, int Areas)
{
    /// <summary>Missing stretches closer than this on the disc are one ring.</summary>
    private const double SameRingMm = 1.5;

    public static DiscTrouble From(IReadOnlyList<DiscMapRange> ranges, DiscMedia media, long layer0Sectors = 0)
    {
        var missing = ranges.Where(r => r.State.IsMissing()).ToList();
        var rings = new List<(double From, double To, long Sectors)>();
        foreach (var range in missing.OrderBy(r => DiscGeometry.RadiusMm(r.Start, media, layer0Sectors)))
        {
            var a = DiscGeometry.RadiusMm(range.Start, media, layer0Sectors);
            var b = DiscGeometry.RadiusMm(range.End - 1, media, layer0Sectors);
            var (from, to) = a <= b ? (a, b) : (b, a);
            if (rings.Count > 0 && from - rings[^1].To <= SameRingMm)
            {
                rings[^1] = (rings[^1].From, Math.Max(rings[^1].To, to), rings[^1].Sectors + range.Count);
            }
            else
            {
                rings.Add((from, to, range.Count));
            }
        }

        return new DiscTrouble([.. rings.Select(r => Math.Round((r.From + r.To) / 2, 1))], missing.Sum(r => r.Count), missing.Count);
    }

    /// <summary>The sectors read only after cleaning within 1.5 mm of a ring: what the cleaning there gave back.</summary>
    public static long CameBackNear(IReadOnlyList<DiscMapRange> ranges, double ringMm, DiscMedia media, long layer0Sectors = 0) =>
        ranges
            .Where(r => r.State == DiscSectorState.ReadAfterCleaning)
            .Where(r => Math.Abs(DiscGeometry.RadiusMm(r.Start + (r.Count / 2), media, layer0Sectors) - ringMm) <= SameRingMm)
            .Sum(r => r.Count);

    /// <summary>The distances in words, such as "about 3.5 cm from the centre" or "about 3.5 and 4.9 cm from the centre".</summary>
    public string Where(CultureInfo culture)
    {
        var cm = RingsMm.Select(mm => (mm / 10).ToString("0.0", culture)).ToList();
        return cm.Count switch
        {
            0 => string.Empty,
            1 => $"about {cm[0]} cm from the centre",
            _ => $"about {string.Join(", ", cm.Take(cm.Count - 1))} and {cm[^1]} cm from the centre",
        };
    }
}
