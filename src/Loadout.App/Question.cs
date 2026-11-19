namespace Loadout.App;

public enum QuestionKind
{
    Question,
    Warn,
    Error,
}

/// <summary>
/// What a <see cref="QuestionDialog"/> asks. Confirm is the action, Cancel the safe choice.
/// A destructive question makes Cancel the default and shows no amber button. Labels may carry an access key ("_Keep them").
/// With <paramref name="ChooseItem"/> the items are a list to choose one from, and <c>QuestionDialog.Choose</c> returns its index.
/// With <paramref name="Picker"/> the dialog shows a choice above the items, and <c>QuestionDialog.Choose</c> returns the option picked.
/// An empty <paramref name="ConfirmLabel"/> makes a message: only the Cancel label's button (such as OK) shows.
/// With <paramref name="Text"/> the dialog asks for a line of text, and <c>QuestionDialog.AskText</c> returns it.
/// </summary>
public sealed record Question(
    string Title,
    string Body,
    string ConfirmLabel,
    string CancelLabel,
    QuestionKind Kind = QuestionKind.Question,
    bool Destructive = false,
    IReadOnlyList<string>? Items = null,
    bool ChooseItem = false,
    QuestionPicker? Picker = null,
    QuestionText? Text = null);

/// <summary>
/// A line of text the dialog asks for, such as a game's name: its box starts with <paramref name="Initial"/>, all of it
/// selected, and Confirm is on only while the box holds a letter or a digit.
/// </summary>
public sealed record QuestionText(string Label, string Initial, int MaxLength);

/// <summary>
/// A choice the dialog shows above its items, such as a card size. Each time it changes, <paramref name="Show"/>
/// gives what the dialog says for that option, so a choice that can't work turns Confirm off and says why.
/// </summary>
public sealed record QuestionPicker(string Label, IReadOnlyList<string> Options, int Selected, Func<int, QuestionView> Show);

/// <summary>What a question with a picker shows for one option: its items, a note below them, and whether Confirm is on.</summary>
public sealed record QuestionView(IReadOnlyList<string> Items, string Note, bool NoteIsWarning, bool CanConfirm);
