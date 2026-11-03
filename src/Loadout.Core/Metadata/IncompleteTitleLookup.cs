using Loadout.Core;
using Loadout.Library;

namespace Loadout.Metadata;

/// <summary>
/// Gives each incomplete install the title the OPL CFG database has for its disc ID. An incomplete
/// install has no header, so without this it shows only what its partition name keeps of the
/// title. Whole games keep the title from their header.
/// </summary>
public static class IncompleteTitleLookup
{
    public static async Task<IReadOnlyList<InstalledGameRow>> ApplyAsync(
        IReadOnlyList<InstalledGameRow> games,
        IDiscTitleCatalog titles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(titles);
        var result = new List<InstalledGameRow>(games.Count);
        foreach (var game in games)
        {
            if (!game.Incomplete || !DiscId.TryParse(game.DiscId, out var discId))
            {
                result.Add(game);
                continue;
            }

            DiscTitleLookup lookup;
            try
            {
                lookup = await titles.LookupAsync(discId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add(game);
                continue;
            }

            result.Add(lookup.Found && !string.IsNullOrWhiteSpace(lookup.Title) ? game with { Title = lookup.Title.Trim() } : game);
        }

        return result;
    }
}
