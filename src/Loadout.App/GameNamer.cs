using System.IO;
using Loadout.Core;
using Loadout.Images;

namespace Loadout.App;

/// <summary>
/// Asks the user for a game's name when nothing else gives one: the title database doesn't know its disc, and Redump's
/// name, the file's name and the disc's label are no names (King of Fighters XI's label is "1"). The name becomes the
/// title OPL shows and the game's partition name on the PS2.
/// </summary>
public sealed class GameNamer(Func<Question, string?> ask)
{
    private const string Heading = "What is this game called?";

    private readonly Func<Question, string?> _ask = ask ?? throw new ArgumentNullException(nameof(ask));

    /// <summary>Before a disc is backed up: its copy, and the game when it is installed from it, take the name. Null for Cancel.</summary>
    public string? ForBackup(string discId, string? label) =>
        Ask(
            $"{discId} isn't in OPL's list of game titles, Redump's list can't tell it by its size, and {LabelWords(label)}. "
                + "The copy takes the name given here, and so does the game on the PS2.",
            "_Back up",
            "Cancel");

    /// <summary>
    /// A game the Install page or Install on PS2 is about to queue: the item with the name given as its title, or null when
    /// the user gave none (it is then not installed).
    /// </summary>
    public QueuePreviewItem? ForInstall(QueuePreviewItem item, string confirmLabel, string cancelLabel)
    {
        ArgumentNullException.ThrowIfNull(item);
        var name = Ask(
            $"{item.DiscId} isn't in OPL's list of game titles, its file's name ({Path.GetFileName(item.SourceFile)}) is no name for it, "
                + $"and {LabelWords(item.VolumeLabel)}. OPL shows the game under the name given here; Installed Games can change it later.",
            confirmLabel,
            cancelLabel);
        return name is null ? null : item with { DetectedTitle = TitleText.FitQueueTitle(name, item.DiscId), NeedsName = false };
    }

    private static string LabelWords(string? label) =>
        string.IsNullOrWhiteSpace(label) ? "the disc has no label" : $"the disc's label is only “{label.Trim()}”";

    private string? Ask(string body, string confirmLabel, string cancelLabel)
    {
        var name = _ask(new Question(
            Heading,
            body,
            confirmLabel,
            cancelLabel,
            // Empty: what Loadout had (the label) is no name, so Confirm waits for one.
            Text: new QuestionText("Name", string.Empty, TitleText.FullTitleMaxCharacters)));
        return string.IsNullOrWhiteSpace(name) ? null : TitleText.FitFullTitle(name);
    }
}
