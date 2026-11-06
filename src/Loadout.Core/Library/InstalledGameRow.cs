using System.Globalization;
using Loadout.Images;

namespace Loadout.Library;

/// <summary>
/// One game on the PS2's hard disk, with the fields of its HDLoader game entry.
/// <see cref="Incomplete"/> marks a copy that stopped early. It has no header, so its title comes
/// from the title database or its partition name, and its sectors are 0.
/// </summary>
public sealed record InstalledGameRow(
    string PartitionName,
    string Title,
    string DiscId,
    byte CompatibilityModeFlags,
    byte TransferType,
    byte TransferMode,
    byte DiscType,
    uint Sectors,
    string? ArtPath = null,
    bool Incomplete = false,
    MemoryCardStatus? MemoryCard = null)
{
    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => Incomplete ? Title + ", incomplete install" : Title;

    /// <summary>The Memory card column: a dash, the card's size, or Missing; empty while the cards are unknown (a saved list).</summary>
    public string MemoryCardLabel => MemoryCard?.Label ?? "";

    /// <summary>The Memory card column's tooltip, or null while the cards are unknown.</summary>
    public string? MemoryCardDetail => MemoryCard?.Detail;

    /// <summary>True when the game could get a memory card: whole, and its card is none, missing, or not read yet.</summary>
    public bool CanGetMemoryCard => !Incomplete && MemoryCard?.Kind != MemoryCardKind.Present;

    /// <summary>The OPL compatibility modes, as the Game details panel numbers them.</summary>
    public string ModesLabel => Modes(CompatibilityModeFlags);

    /// <summary>HDLoader's transfer mode, such as <c>UDMA 4</c>.</summary>
    public string TransferLabel => Transfer(TransferType, TransferMode);

    /// <summary>The installed size in GB (2^30 bytes, as the Bay counts), or a dash for an incomplete install.</summary>
    public string SizeLabel => Size(Sectors);

    /// <summary>The set bits as OPL's mode numbers, such as <c>1, 3</c>, or a dash when none is set.</summary>
    public static string Modes(byte flags) =>
        flags == 0
            ? "–"
            : string.Join(", ", Enumerable.Range(0, 8).Where(bit => (flags & (1 << bit)) != 0).Select(bit => (bit + 1).ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// HDLoader's transfer type 0x20 is MDMA and 0x40 is UDMA, each with its mode number.
    /// Any other type keeps its raw form, so nothing is hidden.
    /// </summary>
    public static string Transfer(byte type, byte mode) => type switch
    {
        0x20 => "MDMA " + mode.ToString(CultureInfo.InvariantCulture),
        0x40 => "UDMA " + mode.ToString(CultureInfo.InvariantCulture),
        _ => "0x" + type.ToString("X2", CultureInfo.InvariantCulture) + " / " + mode.ToString(CultureInfo.InvariantCulture),
    };

    public static string Size(uint sectors) =>
        sectors == 0 ? "–" : (sectors * 2048.0 / (1L << 30)).ToString("0.00", CultureInfo.CurrentCulture) + " GB";

    public string DiscTypeLabel => DiscType switch
    {
        HdlDiscTypes.Ps2Cd => "CD",
        HdlDiscTypes.Ps2CdDa => "CDDA",
        HdlDiscTypes.Ps2Dvd => "DVD",
        _ => "0x" + DiscType.ToString("X2", CultureInfo.InvariantCulture),
    };
}
