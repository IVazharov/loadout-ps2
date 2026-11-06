using System.Globalization;

namespace Loadout.Library;

public enum MemoryCardKind
{
    /// <summary>The game's OPL settings name no memory card in slot 1.</summary>
    None,

    /// <summary>The settings name a card, and it is in OPL's VMC folder.</summary>
    Present,

    /// <summary>The settings name a card that is not in the VMC folder, so OPL can't use it.</summary>
    Missing,
}

/// <summary>
/// An installed game's virtual memory card as OPL sees it: the card slot 1 of the game's settings names,
/// and whether that card is in OPL's VMC folder. <paramref name="SharedWith"/> holds the titles of the
/// other installed games whose settings name the same card.
/// </summary>
public sealed record MemoryCardStatus(MemoryCardKind Kind, string? Card = null, long Bytes = 0, IReadOnlyList<string>? SharedWith = null)
{
    public static MemoryCardStatus None { get; } = new(MemoryCardKind.None);

    public bool IsShared => SharedWith is { Count: > 0 };

    /// <summary>What the Installed Games column shows: a dash, the card's size, or Missing.</summary>
    public string Label => Kind switch
    {
        MemoryCardKind.Present => SizeLabel(Bytes) + (IsShared ? ", shared" : ""),
        MemoryCardKind.Missing => "Missing",
        _ => "–",
    };

    /// <summary>The column's tooltip: the card's file, and the games that share it, or why a card is missing.</summary>
    public string Detail => Kind switch
    {
        MemoryCardKind.Present => "VMC/" + Card + ".bin" + (IsShared ? ", shared with " + string.Join(", ", SharedWith!) : ""),
        MemoryCardKind.Missing => "The game's OPL settings name VMC/" + Card + ".bin, which is not on the PS2. Give memory cards makes a new, empty one.",
        _ => "No memory card. Give memory cards makes one.",
    };

    /// <summary>A card's size in whole MB, as OPL offers them: 8, 16, 32 or 64.</summary>
    public static string SizeLabel(long bytes) => (bytes >> 20).ToString(CultureInfo.CurrentCulture) + " MB";
}
