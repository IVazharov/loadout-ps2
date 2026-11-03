namespace Loadout.Core;

/// <summary>
/// Sentences for failures the app already detects. Logs keep one line.
/// A stack trace is not the message.
/// </summary>
public static class UserFacingText
{
    public const string UnsupportedMultiTrack =
        "Unsupported multi-track image. A PS2 game partition holds one data track, so the audio tracks would be lost. The tracks were not removed.";

    public static string Describe(string? detail)
    {
        var line = FirstLine(detail);
        if (line.Length == 0)
        {
            return "The operation finished with no detail.";
        }

        if (line.Contains("multi-track", StringComparison.OrdinalIgnoreCase))
        {
            return UnsupportedMultiTrack;
        }

        return line.Length <= 300 ? line : line[..300];
    }

    public static string FirstLine(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        var text = detail.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var line = text.Split('\n')[0].Trim();
        var at = line.IndexOf("   at ", StringComparison.Ordinal);
        if (at >= 0)
        {
            line = line[..at].Trim();
        }

        return line;
    }
}
