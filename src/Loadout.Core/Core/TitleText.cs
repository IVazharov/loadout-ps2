using System.Text;

namespace Loadout.Core;

/// <summary>
/// Truncates titles on Unicode scalar-value boundaries so a multibyte UTF-8
/// character is kept whole or dropped. Limits match the HDLoader game entry:
/// 80 characters / 160 UTF-8 bytes.
/// </summary>
public static class TitleText
{
    public const int FullTitleMaxCharacters = 80;

    public const int FullTitleMaxUtf8Bytes = 160;

    public static string FitFullTitle(string? text)
        => Fit(text, FullTitleMaxCharacters, FullTitleMaxUtf8Bytes);

    /// <summary>
    /// Fits a queue title within the limits. A blank title gives way to <paramref name="fallback"/>,
    /// then to <c>Unknown</c>, so the result is never blank.
    /// </summary>
    public static string FitQueueTitle(string? detectedTitle, string? fallback)
    {
        var full = FitFullTitle(detectedTitle);
        if (string.IsNullOrWhiteSpace(full))
        {
            full = FitFullTitle(fallback);
        }

        return string.IsNullOrWhiteSpace(full) ? "Unknown" : full;
    }

    public static string Fit(string? text, int maxCharacters, int maxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(maxUtf8Bytes);

        if (string.IsNullOrEmpty(text) || maxCharacters == 0 || maxUtf8Bytes == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var characters = 0;
        var bytes = 0;
        var index = 0;
        while (index < text.Length && characters < maxCharacters)
        {
            if (!Rune.TryGetRuneAt(text, index, out var rune))
            {
                break;
            }

            if (bytes + rune.Utf8SequenceLength > maxUtf8Bytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
            characters++;
            index += rune.Utf16SequenceLength;
        }

        return builder.ToString();
    }
}
