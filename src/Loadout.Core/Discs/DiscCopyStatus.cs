namespace Loadout.Discs;

public enum DiscCopyPhase
{
    /// <summary>The drive refuses reads, as it does for a while after a failed one; the copy waits for it.</summary>
    WaitingForDrive,

    /// <summary>The first pass, in order and in big pieces.</summary>
    FirstPass,

    /// <summary>A later pass over what is still missing.</summary>
    Sweep,

    /// <summary>The drive rests after a failed read.</summary>
    Resting,

    Complete,

    Stopped,
}

/// <summary>Where a copy is, for the page and its disc map.</summary>
public sealed record DiscCopyStatus
{
    public DiscCopyPhase Phase { get; init; }

    /// <summary>The pass after the first that is under way (1, 2, ...); 0 during the first pass.</summary>
    public int Sweep { get; init; }

    public bool Backwards { get; init; }

    /// <summary>Where the drive reads now.</summary>
    public long Sector { get; init; }

    /// <summary>The piece size in sectors: 128 is 256 KB, 16 is 32 KB, 1 is one sector.</summary>
    public int PieceSectors { get; init; }

    public long TotalSectors { get; init; }

    public long ReadSectors { get; init; }

    public long MissingSectors { get; init; }

    public long UntriedSectors { get; init; }

    /// <summary>How fast the drive has read lately, rests left out; 0 until known.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>During a rest: how long until the drive is tried again.</summary>
    public TimeSpan RestLeft { get; init; }

    public int Failures { get; init; }

    /// <summary>Little has been read for a while: cleaning the disc is likely to help.</summary>
    public bool Stalled { get; init; }
}

public enum DiscCopyNoteKind
{
    WaitingForDrive,
    FirstPassStarted,
    SweepStarted,
    ReadFailed,
    SlowRead,
    JumpedAhead,
    Rested,
    DriveStillRefusing,
    DeadBlock,
    Stalled,
    Complete,
    Stopped,
}

/// <summary>One event of a copy, for its report: a pass, a failed read, a rest.</summary>
public sealed record DiscCopyNote(DiscCopyNoteKind Kind, string Message)
{
    public long Sector { get; init; } = -1;

    public long Count { get; init; }

    public TimeSpan Took { get; init; }
}

public enum DiscCopyOutcome
{
    /// <summary>Every sector is in the copy.</summary>
    Complete,

    /// <summary>Stopped on request. The map says where it was, so it can go on later.</summary>
    Stopped,
}

/// <summary>The copy's numbers. The defaults come from the drive the proof of concept was built on (a slim USB DVD writer).</summary>
public sealed record DiscCopierSettings
{
    /// <summary>The first pass's piece: 128 sectors, 256 KB.</summary>
    public int FirstPassPiece { get; init; } = 128;

    /// <summary>The piece sizes of the later passes, biggest first: 256 KB, one 32 KB error-correction block, one sector.</summary>
    public IReadOnlyList<int> PieceSizes { get; init; } = [128, 16, 1];

    /// <summary>Good reads in a row at each size before going one size up again.</summary>
    public IReadOnlyList<int> GoodReadsBeforeBigger { get; init; } = [0, 8, 32];

    /// <summary>A DVD's error-correction block: 16 sectors.</summary>
    public int Block { get; init; } = 16;

    /// <summary>Single sectors that fail in one block before the rest of it is left for the next pass.</summary>
    public int DeadBlockAfter { get; init; } = 3;

    /// <summary>The first pass's first jump after a failed read (1 MB); it doubles up to <see cref="LastJump"/> (64 MB).</summary>
    public long FirstJump { get; init; } = 512;

    public long LastJump { get; init; } = 32_768;

    /// <summary>Good reads in a row after which the jump is back to the first one.</summary>
    public int GoodReadsBeforeJumpReset { get; init; } = 64;

    public TimeSpan FirstRest { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ShortestRest { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan LongestRest { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How much the first wait of a rest changes after each rest.</summary>
    public TimeSpan RestChange { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a resting drive is tried again.</summary>
    public TimeSpan RestCheckEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a rest may last before the copy goes on anyway.</summary>
    public TimeSpan RestLimit { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the copy waits at its start for a drive that still refuses reads.</summary>
    public TimeSpan StartWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The copy has stalled when the later passes read less than <see cref="StallSectors"/> in this time.</summary>
    public TimeSpan StallTime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>1 MB.</summary>
    public long StallSectors { get; init; } = 512;

    /// <summary>A good read slower than this is noted.</summary>
    public TimeSpan SlowRead { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan StatusEvery { get; init; } = TimeSpan.FromMilliseconds(250);
}
