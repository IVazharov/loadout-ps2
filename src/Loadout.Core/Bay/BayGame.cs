using Loadout.Core;
using Loadout.Library;

namespace Loadout.Bay;

/// <summary>
/// A game partition Loadout Bay listed. <see cref="Header"/> is null when its HDLoader header
/// could not be read or is not one; the game is still listed so it does not silently vanish.
/// <see cref="Incomplete"/> marks a copy that stopped early: a game's name with a header area of
/// zeros, read without error.
/// </summary>
public sealed record BayGame(string PartitionName, HdlGameHeader? Header, bool Incomplete = false)
{
    public const string UnreadableTitle = "(unreadable game header)";

    /// <summary>
    /// The disc ID in a game partition name, or null. Loadout and HDLGameInstaller name a game
    /// <c>PP.&lt;disc ID&gt;.HDL.&lt;title&gt;</c>; other tools use other forms, such as
    /// <c>PP.&lt;disc ID&gt;..&lt;title&gt;</c>, so any <c>PP.&lt;disc ID&gt;.&lt;rest&gt;</c> counts.
    /// </summary>
    public static string? DiscIdOfName(string partitionName)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        if (!partitionName.StartsWith("PP.", StringComparison.Ordinal))
        {
            return null;
        }

        var end = partitionName.IndexOf('.', 3);
        return end > 3 && DiscId.TryParse(partitionName[3..end], out var discId) ? discId.Canonical : null;
    }

    /// <summary>
    /// The title part of a game partition name, as far as it goes: after <c>.HDL.</c>, or after the
    /// disc ID's dots in other tools' names, with each '_' shown as a space. Shown for an incomplete
    /// install, which has no header to take its title from.
    /// </summary>
    public static string TitleOfName(string partitionName)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        var hdl = partitionName.IndexOf(".HDL.", StringComparison.Ordinal);
        var dot = partitionName.StartsWith("PP.", StringComparison.Ordinal) ? partitionName.IndexOf('.', 3) : -1;
        var title = hdl >= 0 ? partitionName[(hdl + 5)..]
            : dot > 3 ? partitionName[(dot + 1)..].TrimStart('.')
            : partitionName;
        title = string.Join(' ', title.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return title.Length > 0 ? title : partitionName;
    }

    /// <summary>
    /// The row Installed Games shows. The disc ID comes from the startup file name, but only
    /// when that name parses; it is never guessed.
    /// </summary>
    public InstalledGameRow ToInstalledGameRow()
    {
        if (Incomplete)
        {
            return new InstalledGameRow(PartitionName, TitleOfName(PartitionName), DiscIdOfName(PartitionName) ?? "", 0, 0, 0, 0, 0, Incomplete: true);
        }

        if (Header is null)
        {
            return new InstalledGameRow(PartitionName, UnreadableTitle, "", 0, 0, 0, 0, 0);
        }

        var discId = DiscId.TryParse(Header.StartupFileName, out var parsed) ? parsed.Canonical : "";
        return new InstalledGameRow(
            PartitionName,
            Header.Title,
            discId,
            Header.CompatibilityModeFlags,
            Header.TransferType,
            Header.TransferMode,
            (byte)Header.DiscType,
            Header.Sectors);
    }
}
