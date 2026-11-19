using System.Text.RegularExpressions;
using Loadout.Discs;

namespace Loadout.Metadata;

/// <summary>
/// A title for a game the OPL title database doesn't know: made from Redump's name for the disc or from a file's name, else
/// the disc's label as it is. A disc's label is often no name at all (King of Fighters XI's is "1"), so a candidate that isn't a name
/// is passed over, and when none is one, Loadout asks the user for the name.
/// </summary>
public static partial class GameNames
{
    /// <summary>
    /// Redump's name or a file's name made into a title: tags in brackets dropped (regions, languages, versions, a disc ID;
    /// a "(Disc 2)" stays), underscores made spaces, and an article Redump puts last moved back to the front, so
    /// "King of Fighters XI, The (Europe)" gives "The King of Fighters XI".
    /// </summary>
    public static string Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = Tags().Replace(name.Replace('_', ' '), " ");
        text = Spaces().Replace(text, " ").Trim();
        var parts = text.Split(" - ");
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = ArticleInFront(parts[i].Trim());
        }

        return string.Join(" - ", parts).Trim(' ', '-');
    }

    /// <summary>
    /// True when <paramref name="title"/> is no name to install a game under: blank, fewer than three letters (a label
    /// such as "1"), or only a disc ID ("SLES_544.37", "SLES-54437").
    /// </summary>
    public static bool IsJunk(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Count(char.IsLetter) < 3 || DiscIdOnly().IsMatch(Bare(title));

    /// <summary>The first of the candidates, trimmed, that is a name; null when none is.</summary>
    public static string? FirstName(params string?[] candidates) =>
        candidates.Select(candidate => candidate?.Trim()).FirstOrDefault(candidate => !IsJunk(candidate));

    /// <summary>
    /// Redump's name for a disc whose copy will be <paramref name="bytes"/> long, tidied, before the copy is made: when the
    /// discs of that size in Redump's list all tidy to one title. Null when there are none, or they are different games.
    /// </summary>
    public static string? FromRedump(RedumpDat? list, long bytes)
    {
        var titles = list?.OfSize(bytes).Select(game => Tidy(game.Name)).Distinct(StringComparer.Ordinal).ToList();
        return titles is [var only] ? only : null;
    }

    private static string ArticleInFront(string part)
    {
        var match = TrailingArticle().Match(part);
        if (!match.Success)
        {
            return part;
        }

        var article = match.Groups["article"].Value;
        var rest = match.Groups["rest"].Value;
        return article.EndsWith('\'') ? article + rest : article + " " + rest;
    }

    private static string Bare(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    // A disc ID's letters and digits, and nothing else.
    [GeneratedRegex(@"^[A-Z]{4}[0-9]{5}$")]
    private static partial Regex DiscIdOnly();

    // Round or square brackets and what is in them, except a "(Disc N)".
    [GeneratedRegex(@"\s*(\((?!Disc \d+\))[^()]*\)|\[[^\[\]]*\])")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // The articles Redump moves to the end of a name, in English and the other languages of its PS2 list.
    [GeneratedRegex(@"^(?<rest>.+), (?<article>The|A|An|Le|La|Les|L'|Il|Lo|Gli|Der|Die|Das|El|Los|Las|Un|Una|Une|De|Het)$")]
    private static partial Regex TrailingArticle();
}
