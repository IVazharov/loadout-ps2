using System.Globalization;

namespace Loadout.Core;

/// <summary>
/// The text rules of the PS2 address: exactly four parts of 0 to 255, digits only. The octet editor in
/// Settings types through these, so it can only ever hold a valid address.
/// </summary>
public static class Ipv4Text
{
    public const int Max = 255;

    /// <summary>
    /// True for a dotted quad such as "10.0.0.55". "10.1", "10.0.0.300", "-1.0.0.0" and host names are not.
    /// </summary>
    public static bool TryParse(string? text, out int[] octets)
    {
        octets = [];
        var parts = (text ?? string.Empty).Trim().Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var values = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3
                || !parts[i].All(char.IsAsciiDigit)
                || (values[i] = int.Parse(parts[i], CultureInfo.InvariantCulture)) > Max)
            {
                return false;
            }
        }

        octets = values;
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _);

    public static string Format(IReadOnlyList<int> octets) =>
        string.Join('.', octets.Select(octet => octet.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// One part as typed: digits only (others dropped), no leading zeros, and above 255 it becomes 255,
    /// so typing 3, 0, 0 gives "255". Empty stays empty.
    /// </summary>
    public static string Octet(string typed)
    {
        var digits = new string((typed ?? string.Empty).Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        if (digits.Length == 0)
        {
            return typed is { Length: > 0 } && typed.Any(char.IsAsciiDigit) ? "0" : string.Empty;
        }

        return digits.Length > 3 || int.Parse(digits, CultureInfo.InvariantCulture) > Max
            ? Max.ToString(CultureInfo.InvariantCulture)
            : digits;
    }

    /// <summary>Up and Down add 1, Page Up and Page Down 10; the result stops at 0 and 255. Empty counts as 0.</summary>
    public static string Step(string octet, int delta)
    {
        var value = Octet(octet) is { Length: > 0 } text ? int.Parse(text, CultureInfo.InvariantCulture) : 0;
        return Math.Clamp(value + delta, 0, Max).ToString(CultureInfo.InvariantCulture);
    }
}
