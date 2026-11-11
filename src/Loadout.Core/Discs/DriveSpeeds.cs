using System.Globalization;

namespace Loadout.Discs;

/// <summary>A stretch of the disc and the drive's nominal read speed at its ends, in kB/s (1000 bytes).</summary>
public readonly record struct DriveSpeedStretch(long StartSector, int StartKbPerSecond, long EndSector, int EndKbPerSecond);

/// <summary>
/// What the drive says about its read speed for the disc in it: the nominal speed along the disc (GET PERFORMANCE) and its
/// maximum (the capabilities page). Read from the drive, not measured from the copy.
/// </summary>
public sealed record DriveSpeeds(DiscMedia Media, int MaxKbPerSecond, IReadOnlyList<DriveSpeedStretch> Stretches)
{
    /// <summary>1x: 176.4 kB/s of raw CD sectors, 1385 kB/s for a DVD.</summary>
    public double OneX => Media == DiscMedia.Cd ? 176.4 : 1385.0;

    public double Times(int kbPerSecond) => kbPerSecond / OneX;

    /// <summary>The drive's nominal speed at a sector, between the ends of its stretch by distance from the centre (a drive
    /// that spins at a constant rate reads faster in proportion further out), or null when the drive gave no curve.</summary>
    public double? KbPerSecondAt(long sector, long layer0Sectors = 0)
    {
        foreach (var s in Stretches)
        {
            if (sector < s.StartSector || sector > s.EndSector)
            {
                continue;
            }

            var r0 = DiscGeometry.RadiusMm(s.StartSector, Media, layer0Sectors);
            var r1 = DiscGeometry.RadiusMm(s.EndSector, Media, layer0Sectors);
            var r = DiscGeometry.RadiusMm(sector, Media, layer0Sectors);
            var t = Math.Abs(r1 - r0) < 1e-9 ? 0 : Math.Clamp((r - r0) / (r1 - r0), 0, 1);
            return s.StartKbPerSecond + (t * (s.EndKbPerSecond - s.StartKbPerSecond));
        }

        return null;
    }

    /// <summary>The range over the disc, such as "10x–24x", or the maximum alone, such as "24x".</summary>
    public string Range(CultureInfo culture)
    {
        if (Stretches.Count == 0)
        {
            return MaxKbPerSecond > 0 ? X(MaxKbPerSecond, culture) : string.Empty;
        }

        var low = Stretches.Min(s => Math.Min(s.StartKbPerSecond, s.EndKbPerSecond));
        var high = Stretches.Max(s => Math.Max(s.StartKbPerSecond, s.EndKbPerSecond));
        return low == high ? X(high, culture) : $"{X(low, culture)}–{X(high, culture)}";
    }

    public string X(double kbPerSecond, CultureInfo culture) => Times((int)Math.Round(kbPerSecond)).ToString("0.#", culture) + "x";

    /// <summary>Reads GET PERFORMANCE's answer (an 8-byte header, then 16-byte descriptors).</summary>
    public static IReadOnlyList<DriveSpeedStretch> ParsePerformance(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < 8)
        {
            return [];
        }

        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(answer);
        var end = Math.Min(answer.Length, length + 4);
        var stretches = new List<DriveSpeedStretch>();
        for (var at = 8; at + 16 <= end; at += 16)
        {
            var d = answer[at..];
            var stretch = new DriveSpeedStretch(
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d),
                (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[4..]),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[8..]),
                (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[12..]));
            if (stretch.StartKbPerSecond > 0 && stretch.EndKbPerSecond > 0 && stretch.EndSector >= stretch.StartSector)
            {
                stretches.Add(stretch);
            }
        }

        return stretches;
    }
}
