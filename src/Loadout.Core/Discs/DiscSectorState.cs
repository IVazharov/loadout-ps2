namespace Loadout.Discs;

/// <summary>
/// What is known about a stretch of a disc being backed up. The values are the characters the map file uses.
/// The read states go from the easiest read to the hardest, in the order the disc map shades them.
/// </summary>
public enum DiscSectorState : byte
{
    /// <summary>Not read yet.</summary>
    Untried = (byte)'?',

    /// <summary>Jumped over by the first pass after a failed read nearby; not tried yet.</summary>
    Jumped = (byte)'/',

    /// <summary>A read failed here. The next pass tries it again.</summary>
    Failed = (byte)'-',

    /// <summary>Read in the first pass, in big pieces, as a good disc reads.</summary>
    ReadNormally = (byte)'+',

    /// <summary>Read on a later pass, still in big pieces.</summary>
    ReadLater = (byte)'1',

    /// <summary>Read in 32 KB pieces (one DVD error-correction block).</summary>
    ReadInPieces = (byte)'2',

    /// <summary>Read one sector at a time.</summary>
    ReadBySector = (byte)'3',

    /// <summary>Read on a pass after the disc was cleaned.</summary>
    ReadAfterCleaning = (byte)'C',
}

public static class DiscSectorStates
{
    /// <summary>True when the sectors are in the copy.</summary>
    public static bool IsRead(this DiscSectorState state) => state is DiscSectorState.ReadNormally
        or DiscSectorState.ReadLater
        or DiscSectorState.ReadInPieces
        or DiscSectorState.ReadBySector
        or DiscSectorState.ReadAfterCleaning;

    /// <summary>True when the sectors are still missing after the first pass reached them: a later pass tries them.</summary>
    public static bool IsMissing(this DiscSectorState state) => state is DiscSectorState.Jumped or DiscSectorState.Failed;

    /// <summary>The state a map file's character stands for. The proof of concept's '*' (failed in a big piece) reads as failed.</summary>
    public static bool TryFromCode(char code, out DiscSectorState state)
    {
        state = code switch
        {
            '?' => DiscSectorState.Untried,
            '/' => DiscSectorState.Jumped,
            '-' or '*' => DiscSectorState.Failed,
            '+' => DiscSectorState.ReadNormally,
            '1' => DiscSectorState.ReadLater,
            '2' => DiscSectorState.ReadInPieces,
            '3' => DiscSectorState.ReadBySector,
            'C' => DiscSectorState.ReadAfterCleaning,
            _ => (DiscSectorState)0,
        };
        return state != 0;
    }
}
