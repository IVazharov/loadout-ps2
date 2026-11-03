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

    /// <summary>The disc ID in a game partition name, <c>PP.&lt;disc ID&gt;.HDL.&lt;title&gt;</c>, or null.</summary>
    public static string? DiscIdOfName(string partitionName)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        if (!partitionName.StartsWith("PP.", StringComparison.Ordinal))
        {
            return null;
        }

        var end = partitionName.IndexOf(".HDL.", 3, StringComparison.Ordinal);
        return end > 3 && DiscId.TryParse(partitionName[3..end], out var discId) ? discId.Canonical : null;
    }

    /// <summary>
    /// The title part of a game partition name, as far as it goes: the first 14 characters, with
    /// every character that was not a letter, a digit or '_' already turned into '_'. Shown with
    /// spaces for an incomplete install, which has no header to take its title from.
    /// </summary>
    public static string TitleOfName(string partitionName)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        var start = partitionName.IndexOf(".HDL.", StringComparison.Ordinal);
        var title = start < 0 ? partitionName : partitionName[(start + 5)..];
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
