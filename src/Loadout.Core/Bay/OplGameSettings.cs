using System.Globalization;
using System.Text;
using Loadout.Core;

namespace Loadout.Bay;

/// <summary>
/// A game's settings file in OPL's CFG folder, <c>&lt;startup&gt;.cfg</c>: <c>key=value</c> lines ending
/// in CRLF, as OPL's <c>configWrite</c> writes them. Loadout changes only <c>$Compatibility</c> (one bit
/// per compatibility mode), <c>$DMA</c> (the transfer mode), and <c>$VMC_0</c> (the memory card in slot 1);
/// every other line stays as it was. Like OPL's own game settings, it leaves <c>$Compatibility</c> out at 0
/// and <c>$DMA</c> out at UDMA 4.
/// </summary>
public sealed class OplGameSettings
{
    public const string CompatibilityKey = "$Compatibility";

    public const string DmaKey = "$DMA";

    /// <summary>The virtual memory card in slot 1, named without <c>.bin</c>.</summary>
    public const string MemoryCardKey = "$VMC_0";

    /// <summary>OPL's transfer mode when the file names none: UDMA 4.</summary>
    public const int DefaultDma = 7;

    private readonly IReadOnlyList<string> _lines;

    private OplGameSettings(IReadOnlyList<string> lines, byte compatibilityModes, int dma, string? memoryCard)
    {
        _lines = lines;
        CompatibilityModes = compatibilityModes;
        Dma = dma;
        MemoryCard = memoryCard;
    }

    /// <summary>Compatibility modes 1 to 8, mode 1 in bit 0.</summary>
    public byte CompatibilityModes { get; }

    /// <summary>The transfer mode: 0 to 2 for MDMA 0 to 2, 3 to 7 for UDMA 0 to 4.</summary>
    public int Dma { get; }

    /// <summary>The card in slot 1, such as <c>SLUS_202.97_0</c>, or null when the game uses none.</summary>
    public string? MemoryCard { get; }

    public static OplGameSettings Empty { get; } = new([], 0, DefaultDma, null);

    /// <summary>The settings file's name for a disc ID: <c>SLUS_202.97.cfg</c>.</summary>
    public static string FileName(DiscId discId) => discId.OplFileStem + ".cfg";

    /// <summary>The transfer modes OPL's game settings offer, in its order.</summary>
    public static IReadOnlyList<string> DmaLabels { get; } =
        ["MDMA 0", "MDMA 1", "MDMA 2", "UDMA 0", "UDMA 1", "UDMA 2", "UDMA 3", "UDMA 4"];

    /// <summary>Reads a settings file. Values that do not parse count as missing, the way OPL's <c>atoi</c> gives 0.</summary>
    public static OplGameSettings Parse(ReadOnlySpan<byte> content)
    {
        var lines = Encoding.UTF8.GetString(content).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var compatibility = Number(lines, CompatibilityKey) ?? 0;
        var dma = Number(lines, DmaKey) ?? DefaultDma;
        var card = Text(lines, MemoryCardKey);
        return new OplGameSettings(
            lines, (byte)compatibility, dma is >= 0 and <= DefaultDma ? dma : DefaultDma, string.IsNullOrEmpty(card) ? null : card);
    }

    /// <summary>The file with these modes and this transfer mode, every other line kept in its place.</summary>
    public byte[] With(byte compatibilityModes, int dma)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dma);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dma, DefaultDma);
        var lines = _lines.ToList();
        Set(lines, CompatibilityKey, compatibilityModes == 0 ? null : compatibilityModes.ToString(CultureInfo.InvariantCulture));
        Set(lines, DmaKey, dma == DefaultDma ? null : dma.ToString(CultureInfo.InvariantCulture));
        return Encode(lines);
    }

    /// <summary>The file with this card in slot 1, every other line kept in its place.</summary>
    public byte[] WithMemoryCard(string card)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(card);
        var lines = _lines.ToList();
        Set(lines, MemoryCardKey, card);
        return Encode(lines);
    }

    private static byte[] Encode(List<string> lines) => Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\r\n")));

    private static string? Text(List<string> lines, string key) =>
        lines.LastOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();

    private static int? Number(List<string> lines, string key) =>
        int.TryParse(Text(lines, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    // Replaces the key's line where it is, adds it at the end, or removes it for null.
    private static void Set(List<string> lines, string key, string? value)
    {
        var at = lines.FindIndex(line => line.StartsWith(key + "=", StringComparison.Ordinal));
        lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.Ordinal));
        if (value is null)
        {
            return;
        }

        lines.Insert(at >= 0 ? at : lines.Count, key + "=" + value);
    }
}
