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
/// </summary>
public sealed record Question(
    string Title,
    string Body,
    string ConfirmLabel,
    string CancelLabel,
    QuestionKind Kind = QuestionKind.Question,
    bool Destructive = false,
    IReadOnlyList<string>? Items = null,
    bool ChooseItem = false);
