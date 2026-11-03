using Loadout.Core;

namespace Loadout.Metadata;

/// <summary>
/// One CFG per game under <c>CFG_en</c> on Thecat1080/PS2-OPL-CFG-Database master.
/// The file name is the OPL stem (<c>SLUS_207.45.cfg</c>). The title is the <c>Title=</c> line.
/// The repository is GPL-3.0 and is not copied into this project.
/// </summary>
public static class OplCfgDatabase
{
    public const string Repository = "https://github.com/Thecat1080/PS2-OPL-CFG-Database";

    public const string RawDirectory = "https://raw.githubusercontent.com/Thecat1080/PS2-OPL-CFG-Database/master/CFG_en/";

    public static Uri RecordUri(DiscId discId)
        => new(RawDirectory + discId.OplFileStem + ".cfg", UriKind.Absolute);

    public static string? ParseTitle(string? cfgText)
    {
        if (string.IsNullOrWhiteSpace(cfgText))
        {
            return null;
        }

        var text = cfgText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Title=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["Title=".Length..].Trim();
            return CleanTitle(value);
        }

        return null;
    }

    /// <summary>
    /// OPL CFG titles are sometimes wrapped in single quotes, with an apostrophe stored as <c>''</c>.
    /// A broken export can drop the opening quote and leave the closer. Plain titles are left as written.
    /// </summary>
    public static string? CleanTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            text = Unescape(text[1..^1]);
            return text.Length == 0 ? null : text;
        }

        if (text.Length >= 2
            && text[^1] == '\''
            && text.Contains("''", StringComparison.Ordinal)
            && !text.EndsWith("''", StringComparison.Ordinal))
        {
            text = Unescape(text[..^1]);
            return text.Length == 0 ? null : text;
        }

        return text;
    }

    private static string Unescape(string text)
        => text.Replace("''", "'", StringComparison.Ordinal);
}
