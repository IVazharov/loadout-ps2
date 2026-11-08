using Loadout.Bay;
using Loadout.Core;

namespace Loadout.Updates;

/// <summary>Whether the Loadout Bay that answered is older than the one this Loadout carries.</summary>
public static class BayVersions
{
    /// <summary>
    /// True when <paramref name="bay"/> is older: an older protocol, or the same protocol and a lower version.
    /// A newer Bay, or a version that can't be read, is not older, so Loadout asks before replacing it.
    /// </summary>
    public static bool IsOlder(BayHello bay, string loadoutVersion, uint loadoutProtocol = BayWire.ProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(bay);
        if (bay.ProtocolVersion != loadoutProtocol)
        {
            return bay.ProtocolVersion < loadoutProtocol;
        }

        return AppVersion.TryParse(bay.Version, out var theirs)
            && AppVersion.TryParse(loadoutVersion, out var ours)
            && theirs < ours;
    }

    /// <inheritdoc cref="IsOlder(BayHello, string, uint)"/>
    public static bool IsOlder(BayHello bay) => IsOlder(bay, ProductInfo.Version);
}
