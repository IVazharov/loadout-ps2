using System.Net;
using System.Text;
using Loadout.Core;
using Loadout.Persistence;

namespace Loadout.Metadata;

public class DiscTitleLookupTests
{
    [Fact]
    public void RecordUriIsOneEnglishCfgNamedWithTheOplStem()
    {
        var gnome = Id("SLUS-20745");
        Assert.Equal(
            "https://raw.githubusercontent.com/Thecat1080/PS2-OPL-CFG-Database/master/CFG_en/SLUS_207.45.cfg",
            OplCfgDatabase.RecordUri(gnome).AbsoluteUri);
    }

    [Fact]
    public void ParseTitleReadsTheTitleLineOnly()
    {
        const string cfg = "CfgVersion=8\nTitle=Gnome 2\nDescription=Not the title\n";
        Assert.Equal("Gnome 2", OplCfgDatabase.ParseTitle(cfg));
        Assert.Equal("Mythic's Keep - Deep Crossing 2", OplCfgDatabase.ParseTitle("Title=Mythic''s Keep - Deep Crossing 2'"));
        Assert.Equal("Rick Ramp's Pro Skater 3", OplCfgDatabase.ParseTitle("Title=Rick Ramp''s Pro Skater 3'"));
        Assert.Equal("Mythic's Keep - Deep Crossing 2", OplCfgDatabase.ParseTitle("Title='Mythic''s Keep - Deep Crossing 2'"));
        Assert.Equal("Tom Clancy's Shadow Tactic", OplCfgDatabase.ParseTitle("Title=Tom Clancy's Shadow Tactic"));
        Assert.Equal("Rick Ramp's Undercovers", OplCfgDatabase.ParseTitle("Title=Rick Ramp's Undercovers"));
        Assert.Equal("Gnome 2", OplCfgDatabase.ParseTitle("Title=Gnome 2"));
        Assert.Null(OplCfgDatabase.ParseTitle("CfgVersion=8\nTitle=\n"));
        Assert.Null(OplCfgDatabase.ParseTitle("CfgVersion=8\n"));
        Assert.Null(OplCfgDatabase.ParseTitle(null));
    }

    [Fact]
    public async Task AnOfflineLookupIsAFailureAndIsNotCached()
    {
        var directory = Directory.CreateTempSubdirectory("loadout-titles-").FullName;
        try
        {
            var cache = new DiscTitleCache(Path.Combine(directory, "titles.sqlite"));
            using var http = new HttpClient(new OfflineHandler());
            var catalog = new OplCfgTitleCatalog(http, cache);

            Assert.True(DiscId.TryParse("SLES-55665", out var discId));
            var lookup = await catalog.LookupAsync(discId, CancellationToken.None);

            Assert.False(lookup.Found);
            Assert.True(lookup.Failed);
            Assert.Null(cache.TryGet("SLES-55665"));
        }
        finally
        {
            TempDirectory.Delete(directory);
        }
    }

    [Fact]
    public async Task OfflineCacheReturnsTheSavedTitle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "loadout-titles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "titles.sqlite");
        try
        {
            var cache = new DiscTitleCache(database);
            cache.Store(new DiscTitleEntry("SLUS-20745", "Gnome 2", true));
            cache.Store(new DiscTitleEntry("SLUS-20001", null, false));
            var handler = new OfflineHandler();
            using var http = new HttpClient(handler);
            var catalog = new OplCfgTitleCatalog(http, cache);

            var saved = await catalog.LookupAsync(Id("SLUS-20745"), CancellationToken.None);
            var missed = await catalog.LookupAsync(Id("SLUS-20001"), CancellationToken.None);
            Assert.True(saved.FromCache);
            Assert.True(saved.Found);
            Assert.Equal("Gnome 2", saved.Title);
            Assert.True(missed.FromCache);
            Assert.False(missed.Found);
            Assert.Null(missed.Title);

            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task SuccessfulLookupCachesTheTitleAndNotTheCfgBody()
    {
        var directory = Path.Combine(Path.GetTempPath(), "loadout-titles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "titles.sqlite");
        try
        {
            const string cfg = "CfgVersion=8\nTitle=Blast\nDeveloper=Criterion\nDescription=Do not store this sentence.\n";
            var handler = new BodyHandler(HttpStatusCode.OK, cfg);
            using var http = new HttpClient(handler);
            var cache = new DiscTitleCache(database);
            var catalog = new OplCfgTitleCatalog(http, cache);

            var first = await catalog.LookupAsync(Id("SLUS-21376"), CancellationToken.None);
            var second = await catalog.LookupAsync(Id("SLUS-21376"), CancellationToken.None);

            Assert.Equal("Blast", first.Title);
            Assert.False(first.FromCache);
            Assert.True(second.FromCache);
            Assert.Equal("Blast", second.Title);
            Assert.Equal(1, handler.Calls);
            Assert.Equal("Blast", cache.TryGet("SLUS-21376")?.Title);

            var stored = Encoding.UTF8.GetString(File.ReadAllBytes(database));
            Assert.DoesNotContain("Do not store this sentence", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("CfgVersion", stored, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CachedOplTitleIsCleanedWhenReadAndStoredAgain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "loadout-titles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cache = new DiscTitleCache(Path.Combine(directory, "titles.sqlite"));
            cache.Store(new DiscTitleEntry("SLUS-20745", "Mythic''s Keep - Deep Crossing 2'", true));
            var handler = new OfflineHandler();
            using var http = new HttpClient(handler);
            var catalog = new OplCfgTitleCatalog(http, cache);

            var lookup = await catalog.LookupAsync(Id("SLUS-20745"), CancellationToken.None);

            Assert.True(lookup.FromCache);
            Assert.Equal("Mythic's Keep - Deep Crossing 2", lookup.Title);
            Assert.Equal("Mythic's Keep - Deep Crossing 2", cache.TryGet("SLUS-20745")?.Title);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static DiscId Id(string text)
    {
        Assert.True(DiscId.TryParse(text, out var discId));
        return discId;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            TempDirectory.Delete(directory);
        }
        catch (IOException)
        {
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("offline");
        }
    }

    private sealed class BodyHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public BodyHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
