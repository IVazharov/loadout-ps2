namespace Loadout.App;

public sealed class NavPage
{
    public required string Title { get; init; }

    public required object Content { get; init; }

    /// <summary>The page's place in the rail, 1 to 5, which is also its Ctrl+number shortcut.</summary>
    public required int Number { get; init; }

    public string ToolTip => Title + " (Ctrl+" + Number + ")";
}
