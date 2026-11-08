using System.Text;
using System.Text.RegularExpressions;

namespace Loadout.Updates;

public enum NoteLineKind
{
    /// <summary>A section such as Added, Changed or Fixed.</summary>
    Heading,

    /// <summary>One change.</summary>
    Bullet,

    Text,
}

public sealed record NoteLine(NoteLineKind Kind, string Text);

/// <summary>
/// A release's notes (its changelog section, in Markdown) as plain lines for the update dialog: headings,
/// bullets with their wrapped lines joined, and other text. Links keep their words; emphasis and code marks go.
/// </summary>
public static partial class ReleaseNotesText
{
    public static IReadOnlyList<NoteLine> Lines(string? markdown)
    {
        var lines = new List<NoteLine>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return lines;
        }

        StringBuilder? bullet = null;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                Flush();
                lines.Add(new NoteLine(NoteLineKind.Heading, Plain(heading.Groups[1].Value)));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                bullet = new StringBuilder(line[2..].Trim());
            }
            else if (bullet is not null && raw.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                // A bullet's text wrapped onto an indented line.
                bullet.Append(' ').Append(line);
            }
            else
            {
                Flush();
                lines.Add(new NoteLine(NoteLineKind.Text, Plain(line)));
            }
        }

        Flush();
        return lines;

        void Flush()
        {
            if (bullet is not null)
            {
                lines.Add(new NoteLine(NoteLineKind.Bullet, Plain(bullet.ToString())));
                bullet = null;
            }
        }
    }

    private static string Plain(string text)
    {
        text = LinkPattern().Replace(text, "$1");
        text = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
        return text.Trim();
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)\s]+\)")]
    private static partial Regex LinkPattern();
}
