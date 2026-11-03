namespace Loadout.App;

/// <summary>The kinds of the status panel, each with its stripe colour and icon.</summary>
public enum StatusKind
{
    Idle,
    Busy,
    Ok,
    Warn,
    Error,
}

/// <summary>
/// What a page's status panel shows: a headline of what is happening or what happened, one line of
/// detail with the numbers and the next step, and the progress: <c>null</c> for no bar, NaN while
/// how far is not known.
/// </summary>
public sealed record PageStatus(StatusKind Kind, string Headline, string Detail = "", double? Percent = null)
{
    public static PageStatus Idle(string headline, string detail = "") => new(StatusKind.Idle, headline, detail);

    /// <summary>Busy, with an indeterminate bar unless the percent is known.</summary>
    public static PageStatus Busy(string headline, string detail = "", double percent = double.NaN) => new(StatusKind.Busy, headline, detail, percent);

    public static PageStatus Ok(string headline, string detail = "") => new(StatusKind.Ok, headline, detail);

    public static PageStatus Warn(string headline, string detail = "") => new(StatusKind.Warn, headline, detail);

    public static PageStatus Error(string headline, string detail = "") => new(StatusKind.Error, headline, detail);

    public static PageStatus NoAddress { get; } = Warn("No PS2 address", "Enter it in Settings.");

    /// <summary>
    /// A Loadout Bay failure in a detail line. Without an answer, Install and Artwork point to Connect and
    /// Find Bay, which search the network; they don't search themselves.
    /// </summary>
    public static string BayReason(Loadout.Bay.LoadoutBayException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Unreachable ? failure.Message + " Connect on Installed Games, or Find Bay in Settings." : failure.Message;
    }

    /// <summary>The line the Logs page keeps of it.</summary>
    public string LogLine => Detail.Length == 0 ? Headline + "." : Headline + ". " + Detail;
}
