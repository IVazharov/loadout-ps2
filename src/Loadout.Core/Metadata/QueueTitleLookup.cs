using Loadout.Core;
using Loadout.Images;

namespace Loadout.Metadata;

/// <summary>
/// Gives each queued image the title the OPL CFG database has for its disc ID, in place of the
/// title read from the disc (usually its volume label, such as <c>DARK_ALLIANCE</c>). That title
/// becomes the game's HDLoader title and its partition name when it is installed. An image whose
/// disc ID the database does not know, or that could not be looked up, keeps its title.
/// </summary>
public static class QueueTitleLookup
{
    public static async Task<IReadOnlyList<QueuePreviewItem>> ApplyAsync(
        IReadOnlyList<QueuePreviewItem> items,
        IDiscTitleCatalog titles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(titles);
        var result = new List<QueuePreviewItem>(items.Count);
        foreach (var item in items)
        {
            if (!DiscId.TryParse(item.DiscId, out var discId))
            {
                result.Add(item);
                continue;
            }

            DiscTitleLookup lookup;
            try
            {
                lookup = await titles.LookupAsync(discId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add(item);
                continue;
            }

            if (!lookup.Found || string.IsNullOrWhiteSpace(lookup.Title))
            {
                result.Add(item);
                continue;
            }

            result.Add(item with
            {
                DetectedTitle = TitleText.FitQueueTitle(lookup.Title, discId.Canonical),
                TitleIsFilenameFallback = false,
            });
        }

        return result;
    }
}
