using System.Globalization;
using Loadout.Core;
using Loadout.Images;

namespace Loadout.Bay;

/// <summary>The Install page's header: what the queue holds, and how much the ready games take against the PS2's free space.</summary>
public static class InstallQueueSummary
{
    /// <summary>
    /// Such as "3 IN QUEUE · 3 READY · 3.8 GB OF 17.9 GB FREE". Without a Connect, the free space is unknown and only the
    /// total shows. Only ready games count, as only they are sent.
    /// </summary>
    public static string Describe(IReadOnlyCollection<QueuePreviewItem> items, BayFreeSpace? space) => Describe(items, space, 0);

    /// <summary>
    /// As above, with the free space less what an install has sent since it was read, so the figure follows a batch
    /// until the list is read again.
    /// </summary>
    public static string Describe(IReadOnlyCollection<QueuePreviewItem> items, BayFreeSpace? space, ulong sentBytes)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (space is not null && sentBytes > 0)
        {
            var sentSectors = sentBytes / FreeSpace.ApaSectorSizeBytes;
            space = space with { FreeSectors = space.FreeSectors > sentSectors ? space.FreeSectors - sentSectors : 0 };
        }

        if (items.Count == 0)
        {
            return string.Empty;
        }

        var ready = items.Where(item => item.Status == QueueItemStatus.Ready).ToList();
        var text = string.Create(CultureInfo.CurrentCulture, $"{items.Count} IN QUEUE · {ready.Count} READY");
        if (ready.Count == 0)
        {
            return text;
        }

        var total = (ulong)ready.Sum(item => item.InstallBytes);
        text += " · " + Gb(total) + " GB";
        if (space is null)
        {
            return text;
        }

        return total <= space.FreeBytes
            ? text + " OF " + Gb(space.FreeBytes) + " GB FREE"
            : text + ", MORE THAN THE " + Gb(space.FreeBytes) + " GB FREE";
    }

    private static string Gb(ulong bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture);
}
