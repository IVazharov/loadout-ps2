namespace Loadout.Discs;

/// <summary>Why a picked copy is left out of a conversion.</summary>
public enum ConversionSkip
{
    /// <summary>It is kept in the form asked for already: a chd for Convert to CHD, an iso or bin and cue for the other way.</summary>
    AlreadyThere,

    /// <summary>Sectors are still missing, so there is nothing whole to convert.</summary>
    Incomplete,

    /// <summary>It is being backed up, converted, or waits to be converted.</summary>
    Busy,
}

/// <summary>A picked copy a conversion leaves out, and why.</summary>
public sealed record SkippedCopy(DiscBackup Backup, ConversionSkip Why);

/// <summary>
/// Which of the picked copies a conversion takes, in the order they were picked, which it leaves out and why, and the
/// room it needs on the drive at most: a chd counted as big as its image, as one can be; an image at its full size.
/// </summary>
public sealed record DiscBackupConversionPlan(IReadOnlyList<DiscBackup> Convert, IReadOnlyList<SkippedCopy> Skipped, long BytesNeeded)
{
    /// <summary>
    /// Plans Convert to CHD (<paramref name="toChd"/>) or Convert to ISO/BIN for the picked copies. <paramref name="busy"/>
    /// says whether a copy is being backed up or converted, or waits to be.
    /// </summary>
    public static DiscBackupConversionPlan For(IEnumerable<DiscBackup> picked, bool toChd, Func<DiscBackup, bool> busy)
    {
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(busy);
        var convert = new List<DiscBackup>();
        var skipped = new List<SkippedCopy>();
        var bytes = 0L;
        foreach (var backup in picked)
        {
            var why = backup.Result == DiscBackupResult.Incomplete ? ConversionSkip.Incomplete
                : !CanTake(backup, toChd) ? ConversionSkip.AlreadyThere
                : busy(backup) ? ConversionSkip.Busy
                : (ConversionSkip?)null;
            if (why is { } skip)
            {
                skipped.Add(new SkippedCopy(backup, skip));
                continue;
            }

            convert.Add(backup);
            bytes += BytesFor(backup, toChd);
        }

        return new DiscBackupConversionPlan(convert, skipped, bytes);
    }

    /// <summary>
    /// The room converting this copy needs at most: for a chd, as much as its image takes; for an image, its whole size,
    /// or nothing when the image is there already and only its chd waits to go.
    /// </summary>
    public static long BytesFor(DiscBackup backup, bool toChd)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return toChd ? Math.Max(backup.BytesOnDisk, backup.Bytes)
            : backup.Files.ChdWaitsToGo() ? 0
            : backup.Bytes;
    }

    // A chd is made from a copy kept as an image; an image is made from a chd, or finished when it was made and only its
    // chd still waits to be removed.
    private static bool CanTake(DiscBackup backup, bool toChd) => toChd
        ? backup.Format != DiscBackupFormat.Chd
        : backup.Format == DiscBackupFormat.Chd || backup.Files.ChdWaitsToGo();
}
