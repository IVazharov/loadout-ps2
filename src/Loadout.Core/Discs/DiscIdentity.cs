using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Loadout.Core;
using Loadout.Images;

namespace Loadout.Discs;

/// <summary>
/// Tells one disc from another: its disc ID from SYSTEM.CNF, its length, and a hash of its volume descriptor. A copy only
/// goes on with the same disc, in any drive.
/// </summary>
public sealed record DiscIdentity(DiscId? DiscId, long Sectors, string VolumeHash)
{
    /// <summary>True for the same disc. A disc ID that could not be read on one of them doesn't count against it.</summary>
    public bool IsSameDisc(DiscIdentity other) =>
        Sectors == other.Sectors
        && string.Equals(VolumeHash, other.VolumeHash, StringComparison.Ordinal)
        && (DiscId is null || other.DiscId is null || DiscId == other.DiscId);

    /// <summary>One line for the disc map, such as "SLES-50510 481712 3f2a91c07b5e66d4".</summary>
    public string Format() => string.Create(CultureInfo.InvariantCulture, $"{DiscId?.Canonical ?? "-"} {Sectors} {VolumeHash}");

    public static bool TryParse(string? text, out DiscIdentity identity)
    {
        identity = new DiscIdentity(null, 0, string.Empty);
        var parts = text?.Split(' ');
        if (parts is not { Length: 3 }
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sectors)
            || parts[2].Length == 0)
        {
            return false;
        }

        DiscId? discId = null;
        if (parts[0] != "-")
        {
            if (!Core.DiscId.TryParse(parts[0], out var parsed))
            {
                return false;
            }

            discId = parsed;
        }

        identity = new DiscIdentity(discId, sectors, parts[2]);
        return true;
    }

    /// <summary>Reads the identity of the disc in a drive, or null when its volume descriptor can't be read.</summary>
    public static DiscIdentity? Read(IDiscReader reader)
    {
        var descriptor = new byte[DiscSectors.Size];
        if (!reader.TryRead(DiscSectors.VolumeDescriptor, descriptor, out _))
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(descriptor).AsSpan(0, 8));
        return new DiscIdentity(ReadDiscId(reader), reader.Sectors, hash);
    }

    private static DiscId? ReadDiscId(IDiscReader reader)
    {
        var root = IsoFileTree.Read((sector, destination) => reader.TryRead(sector, destination, out _), reader.Sectors, rootOnly: true);
        var cnf = root.Files.FirstOrDefault(f => f.Path.Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase));
        if (cnf is null || cnf.Bytes is <= 0 or > 8 * DiscSectors.Size)
        {
            return null;
        }

        var bytes = new byte[cnf.Sectors * DiscSectors.Size];
        if (!reader.TryRead(cnf.FirstSector, bytes, out _))
        {
            return null;
        }

        var result = SystemCnf.Parse(Encoding.ASCII.GetString(bytes, 0, (int)cnf.Bytes));
        return result.Ambiguous ? null : result.DiscId;
    }
}
