using System.Globalization;

namespace Loadout.Discs;

/// <summary>A copy in a few words, for its line on the Logs page: how much the first pass read, the weak areas, the cleanings.</summary>
public static class DiscCopySummary
{
    /// <summary>Stretches read with trouble that lie closer than this (32 MB) count as one weak area.</summary>
    public const long SameAreaGap = 16_384;

    /// <summary>Such as "all on the first pass" or "99.3% on the first pass, 2 weak areas, 2 cleanings".</summary>
    public static string Describe(IReadOnlyList<DiscMapRange> ranges, long sectors, int cleanings)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var normal = ranges.Where(r => r.State == DiscSectorState.ReadNormally).Sum(r => r.Count);
        var areas = WeakAreas(ranges);
        if (normal >= sectors && areas == 0)
        {
            return cleanings == 0 ? "all on the first pass" : Cleanings(cleanings) + ", all on the first pass";
        }

        // Rounded down, so a copy with trouble never reads as all on the first pass; one decimal near the top.
        var share = 100.0 * normal / Math.Max(1, sectors);
        var first = share >= 99
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(share * 10) / 10:0.0}%")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(share):0}%");
        var parts = new List<string> { first + " on the first pass" };
        if (areas > 0)
        {
            parts.Add(areas == 1 ? "1 weak area" : string.Create(CultureInfo.InvariantCulture, $"{areas} weak areas"));
        }

        if (cleanings > 0)
        {
            parts.Add(Cleanings(cleanings));
        }

        return string.Join(", ", parts);
    }

    /// <summary>The stretches not read on the first pass, those closer than <see cref="SameAreaGap"/> counted as one.</summary>
    public static int WeakAreas(IReadOnlyList<DiscMapRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var areas = 0;
        long? lastEnd = null;
        foreach (var range in ranges.Where(r => r.State != DiscSectorState.ReadNormally && r.State != DiscSectorState.Untried).OrderBy(r => r.Start))
        {
            if (lastEnd is not { } end || range.Start - end > SameAreaGap)
            {
                areas++;
            }

            lastEnd = Math.Max(lastEnd ?? range.End, range.End);
        }

        return areas;
    }

    private static string Cleanings(int count) => count == 1 ? "1 cleaning" : string.Create(CultureInfo.InvariantCulture, $"{count} cleanings");
}
