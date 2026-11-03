namespace Loadout.Images;

/// <summary>
/// Removes rows from the local install queue. It does not contact the PS2 and does not
/// delete a partition on the PlayStation 2.
/// </summary>
public static class InstallQueueEditor
{
    public static IReadOnlyList<QueuePreviewItem> Remove(
        IReadOnlyList<QueuePreviewItem> scanned,
        IReadOnlyList<QueuePreviewItem> selected)
    {
        ArgumentNullException.ThrowIfNull(scanned);
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count == 0)
        {
            return scanned.ToArray();
        }

        var keys = new HashSet<string>(selected.Select(SourceKey), StringComparer.Ordinal);
        return scanned.Where(item => !keys.Contains(SourceKey(item))).ToArray();
    }

    private static string SourceKey(QueuePreviewItem item)
        => item.SourceFile + "\n" + (item.DiscId ?? string.Empty);
}
