using Loadout.Core;
using Loadout.Images;
using Loadout.Library;

namespace Loadout.Metadata;

public sealed class QueueTitleLookupTests
{
    [Fact]
    public async Task AKnownDiscIdTakesTheDatabaseTitleAndAnUnknownOneKeepsItsOwn()
    {
        var items = new[] { Item("SLUS-20035", "DARK_ALLIANCE"), Item("SLES-99999", "HOMEBREW"), Item(null, "No disc ID") };

        var titled = await QueueTitleLookup.ApplyAsync(items, new Catalog(("SLUS-20035", "Mythic's Keep - Deep Crossing")));

        Assert.Equal(["Mythic's Keep - Deep Crossing", "HOMEBREW", "No disc ID"], titled.Select(item => item.DetectedTitle));
        Assert.False(titled[0].TitleIsFilenameFallback);
    }

    [Fact]
    public async Task ALookupThatFailsKeepsTheTitle()
    {
        var titled = await QueueTitleLookup.ApplyAsync([Item("SLUS-20035", "DARK_ALLIANCE")], new Failing());

        Assert.Equal("DARK_ALLIANCE", titled.Single().DetectedTitle);
    }

    [Fact]
    public async Task AnIncompleteInstallTakesTheDatabaseTitleAndAWholeGameKeepsItsHeaderTitle()
    {
        var games = new[]
        {
            new InstalledGameRow("PP.SLUS-20035.HDL.Mythic_s_Keep_", "Mythic s Keep", "SLUS-20035", 0, 0, 0, 0, 0, Incomplete: true),
            new InstalledGameRow("PP.SLUS-20675.HDL.Mythic_s_Keep_", "Mythic's Keep - Deep Crossing 2", "SLUS-20675", 0, 0x40, 4, 0x14, 1425696),
            new InstalledGameRow("PP.SLES-99999.HDL.Unknown", "Unknown", "SLES-99999", 0, 0, 0, 0, 0, Incomplete: true),
        };
        var catalog = new Catalog(("SLUS-20035", "Mythic's Keep - Deep Crossing"), ("SLUS-20675", "Another title"));

        var titled = await IncompleteTitleLookup.ApplyAsync(games, catalog);

        Assert.Equal(["Mythic's Keep - Deep Crossing", "Mythic's Keep - Deep Crossing 2", "Unknown"], titled.Select(game => game.Title));
        Assert.True(titled[0].Incomplete);
    }

    private static QueuePreviewItem Item(string? discId, string title) =>
        new() { SourceFile = title + ".iso", DiscId = discId, DetectedTitle = title, Status = QueueItemStatus.Ready };

    private sealed class Catalog(params (string DiscId, string Title)[] titles) : IDiscTitleCatalog
    {
        public Task<DiscTitleLookup> LookupAsync(DiscId discId, CancellationToken cancellationToken)
        {
            var match = titles.FirstOrDefault(entry => entry.DiscId == discId.Canonical);
            return Task.FromResult(match.Title is null
                ? new DiscTitleLookup(discId.Canonical, null, false, false)
                : new DiscTitleLookup(discId.Canonical, match.Title, true, false));
        }
    }

    private sealed class Failing : IDiscTitleCatalog
    {
        public Task<DiscTitleLookup> LookupAsync(DiscId discId, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
