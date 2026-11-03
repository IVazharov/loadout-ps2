namespace Loadout.Core;

/// <summary>
/// PlayStation 2 disc identity. The canonical form is the HDL hyphen form
/// <c>SLES-53886</c> (four letters, hyphen, five digits). The ISO/OPL form
/// <c>SLES_538.86</c> and the startup filename <c>SLES_538.86;1</c> parse to the
/// same value. This type never pads, guesses, or synthesises an identifier.
/// </summary>
public readonly record struct DiscId
{
    private DiscId(string region, string digits)
    {
        Region = region;
        Digits = digits;
    }

    /// <summary>Four-letter region prefix, uppercase, for example <c>SLES</c>.</summary>
    public string Region { get; }

    /// <summary>Five catalogue digits, for example <c>53886</c>.</summary>
    public string Digits { get; }

    /// <summary>HDL wire form stored in <c>DiscID[11]</c>, for example <c>SLES-53886</c>.</summary>
    public string Canonical => Region + "-" + Digits;

    /// <summary>OPL artwork/config stem, for example <c>SLES_538.86</c>.</summary>
    public string OplFileStem => Region + "_" + Digits[..3] + "." + Digits[3..];

    /// <summary>
    /// The startup file field of the game header: 11 characters that stop before <c>;1</c>,
    /// so it is <c>SLES_538.86</c>, not <c>SLES_538.86;1</c>.
    /// OPL builds <c>ART/{startup}_COV.png</c> from this field and does not strip a suffix.
    /// </summary>
    public string StartupFileName => OplFileStem;

    public static bool TryParse(string? text, out DiscId discId)
    {
        discId = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            return false;
        }

        // Startup filenames append ";1". Only the 11-character underscore form uses that suffix.
        if (value.Length == 13 && value.EndsWith(";1", StringComparison.Ordinal))
        {
            value = value[..11];
        }

        if (value.Length == 10 && value[4] == '-')
        {
            if (!IsAsciiLetters(value, 0, 4) || !IsDigits(value, 5, 5))
            {
                return false;
            }

            discId = new DiscId(value[..4].ToUpperInvariant(), value[5..]);
            return true;
        }

        if (value.Length == 11 && value[4] == '_' && value[8] == '.')
        {
            if (!IsAsciiLetters(value, 0, 4) || !IsDigits(value, 5, 3) || !IsDigits(value, 9, 2))
            {
                return false;
            }

            discId = new DiscId(value[..4].ToUpperInvariant(), string.Concat(value.AsSpan(5, 3), value.AsSpan(9, 2)));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Two texts name the same game only when both parse and their canonical
    /// disc IDs are equal. Unparseable text is never treated as a duplicate.
    /// Region is part of the key: <c>SLUS-53886</c> is not <c>SLES-53886</c>.
    /// </summary>
    public static bool AreSameGame(string? left, string? right)
    {
        return TryParse(left, out var leftId)
            && TryParse(right, out var rightId)
            && leftId.Canonical == rightId.Canonical;
    }

    private static bool IsAsciiLetters(string value, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            var c = value[i];
            if (c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigits(string value, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
