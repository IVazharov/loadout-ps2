using System.Globalization;
using Loadout.Core;
using Loadout.Library;

namespace Loadout.Images;

public enum QueueItemStatus
{
    Ready,
    NeedsConversion,
    NeedsMetadataReview,
    Duplicate,
    Failed,
    Installed,
    Installing,
}

public enum InstallationState
{
    NotInstalled,
    Installed,

    /// <summary>Only an incomplete install of the disc is on the PS2, which installing it replaces.</summary>
    Incomplete,
}

public enum DuplicateMatch
{
    None,
    InLibrary,
    InQueue,
}

public enum MediaKind
{
    Unknown,
    Cd,
    CdMultiTrack,
    Dvd5,
    Dvd9,
}

public enum ScanDepth
{
    File,
    Directory,
    DirectoryRecursive,
}

/// <summary>
/// HDLoader disc-type byte stored in the game entry's <c>DiscType</c> field.
/// This is not the image's sector mode (2048-byte, MODE1, or MODE2).
/// DVD9 uses <see cref="Ps2Dvd"/> and keeps two layer sector counts.
/// </summary>
public static class HdlDiscTypes
{
    public const byte Ps2Cd = 0x12;

    public const byte Ps2CdDa = 0x13;

    public const byte Ps2Dvd = 0x14;
}

public sealed record ScanInput(string Path, ScanDepth Depth);

public sealed record ScanProgress(int Completed, int Total, string CurrentPath, double? Percent);

public sealed record DiscScanResult(
    IReadOnlyList<QueuePreviewItem> Items,
    IReadOnlyList<string> LeftoverTemporaryFiles,
    bool Cancelled)
{
    /// <summary>Selected images left unread because <see cref="DiscScanOptions.Queued"/> names them.</summary>
    public IReadOnlyList<string> AlreadyQueued { get; init; } = [];
}

public sealed record QueuePreviewItem
{
    public required string SourceFile { get; init; }

    public string? DataFile { get; init; }

    public IReadOnlyList<string> RelatedFiles { get; init; } = Array.Empty<string>();

    public string? DiscId { get; init; }

    public string? Region { get; init; }

    public string DetectedTitle { get; init; } = string.Empty;

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => DetectedTitle + ", " + StatusLabel;

    public string StatusLabel => Status switch
    {
        QueueItemStatus.NeedsConversion => "Needs conversion",
        QueueItemStatus.NeedsMetadataReview => "Needs metadata review",
        _ => Status.ToString(),
    };

    public string InstallationLabel => InstallationStatus switch
    {
        InstallationState.Installed => "Installed",
        InstallationState.Incomplete => "Incomplete",
        _ => "Not installed",
    };

    /// <summary>The Detail of a disc with only an incomplete install on the PS2.</summary>
    public const string IncompleteDetail = "An incomplete install of it is on the PS2. Installing it replaces that copy.";

    public string DuplicateLabel => DuplicateStatus switch
    {
        DuplicateMatch.InLibrary => "In library",
        DuplicateMatch.InQueue => "In queue",
        _ => "None",
    };

    public string FriendlyDetail => string.IsNullOrWhiteSpace(Detail) ? string.Empty : UserFacingText.Describe(Detail);

    public bool TitleIsFilenameFallback { get; init; }

    public MediaKind Media { get; init; }

    public string MediaLabel { get; init; } = "Unknown";

    public uint Layer0Sectors { get; init; }

    public uint Layer1Sectors { get; init; }

    /// <summary>The bytes the game takes on the PS2, which the install sends: its sectors, 2048 bytes each. Zero while unknown.</summary>
    public long InstallBytes => ((long)Layer0Sectors + Layer1Sectors) * 2048;

    /// <summary>The Size column, as Installed Games shows it: "1.40 GB", or a dash while the size is unknown.</summary>
    public string SizeLabel => InstalledGameRow.Size(checked(Layer0Sectors + Layer1Sectors));

    public byte HdlDiscType { get; init; }

    public int VolumeSetSize { get; init; }

    public int VolumeSequence { get; init; }

    public long? ConvertedSize { get; init; }

    public QueueItemStatus Preliminary { get; init; }

    public QueueItemStatus Status { get; init; }

    public InstallationState InstallationStatus { get; init; }

    public DuplicateMatch DuplicateStatus { get; init; }

    /// <summary>True once this queue's own install put the disc on the PS2, which makes it the copy there rather than a duplicate of it.</summary>
    public bool InstalledFromQueue { get; init; }

    /// <summary>True while this queue's install is sending the disc to the PS2.</summary>
    public bool Installing { get; init; }

    public string? Detail { get; init; }

    public int TrackCount { get; init; }

    /// <param name="incompleteOnPs2">True when an incomplete install of the disc is on the PS2. The row stays ready,
    /// as installing replaces that copy, and says so.</param>
    public QueuePreviewItem Resolve(bool inLibrary, bool inQueue, bool incompleteOnPs2 = false)
    {
        var status = Preliminary;
        var installation = inLibrary ? InstallationState.Installed : InstallationState.NotInstalled;
        var duplicate = DuplicateMatch.None;
        var detail = Detail;
        if (Preliminary == QueueItemStatus.Ready && Installing)
        {
            status = QueueItemStatus.Installing;
        }
        else if (Preliminary == QueueItemStatus.Ready && inLibrary && InstalledFromQueue)
        {
            status = QueueItemStatus.Installed;
        }
        else if (Preliminary == QueueItemStatus.Ready && (inLibrary || inQueue))
        {
            status = QueueItemStatus.Duplicate;
            duplicate = inLibrary ? DuplicateMatch.InLibrary : DuplicateMatch.InQueue;
        }
        else if (Preliminary == QueueItemStatus.Ready && incompleteOnPs2)
        {
            installation = InstallationState.Incomplete;
            detail = IncompleteDetail;
        }

        return this with
        {
            Status = status,
            InstallationStatus = installation,
            DuplicateStatus = duplicate,
            Detail = detail,
        };
    }

    public static string DescribeMedia(MediaKind kind, int volumeSetSize, int volumeSequence)
    {
        var name = kind switch
        {
            MediaKind.Cd => "CD",
            MediaKind.CdMultiTrack => "CD (multi-track)",
            MediaKind.Dvd5 => "DVD5",
            MediaKind.Dvd9 => "DVD9",
            _ => "Unknown",
        };

        if (volumeSetSize > 1)
        {
            return name
                + " (disc "
                + volumeSequence.ToString(CultureInfo.InvariantCulture)
                + " of "
                + volumeSetSize.ToString(CultureInfo.InvariantCulture)
                + ")";
        }

        return name;
    }
}
