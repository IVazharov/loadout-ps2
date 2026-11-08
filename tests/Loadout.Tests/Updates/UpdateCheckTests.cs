using System.Net;
using System.Text.Json;

namespace Loadout.Updates;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.9.3", "0.10.0-dev.1")]
    [InlineData("0.10.0-dev.2", "0.10.0-dev.3")]
    [InlineData("0.10.0-dev.9", "0.10.0-dev.10")]
    [InlineData("0.10.0-dev.3", "0.10.0")]
    [InlineData("0.10.0", "0.10.1-dev.1")]
    [InlineData("0.10.0", "0.10.1")]
    [InlineData("0.10.9", "0.11.0")]
    [InlineData("0.99.0", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    public void VersionsFollowSemVerOrder(string lower, string higher)
    {
        var a = AppVersion.Parse(lower);
        var b = AppVersion.Parse(higher);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a.CompareTo(b) < 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildMetadataIsIgnoredAndTextRoundTrips()
    {
        Assert.Equal(AppVersion.Parse("0.10.0-dev.2"), AppVersion.Parse("0.10.0-dev.2+abc123"));
        Assert.Equal("0.10.0-dev.2", AppVersion.Parse("0.10.0-dev.2+abc123").ToString());
        Assert.Equal("1.2.3", AppVersion.Parse("1.2.3").ToString());
        Assert.False(AppVersion.Parse("1.2.3").IsPreRelease);
        Assert.True(AppVersion.Parse("1.2.3-dev.1").IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-dev..1")]
    [InlineData("1.2.3-dev.01")]
    [InlineData("1.2.3-dev_1")]
    [InlineData("a.b.c")]
    public void NotAVersion(string text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Fact]
    public void TheNewestReleaseWithTheWindowsDownloadIsOfferedWithTheNotesOfEveryNewerOne()
    {
        var releases = GitHubReleaseFeed.Read(Element(ListJson(
            Release("v0.10.2", body: "two", withDownload: false),
            Release("v0.10.1", body: "one"),
            Release("v0.10.0", body: "zero"),
            Release("v0.9.3", body: "old"))));

        var offer = UpdateCheck.Choose(releases, AppVersion.Parse("0.9.3"));

        Assert.NotNull(offer);
        Assert.Equal("0.10.1", offer.Version.ToString());
        Assert.Equal("Loadout-PS2-0.10.1-win-x64.zip", offer.Download.Name);
        Assert.Equal(new Uri("https://github.com/IVazharov/loadout-ps2/releases/download/v0.10.1/Loadout-PS2-0.10.1-win-x64.zip"), offer.Download.DownloadUrl);
        Assert.Equal(["0.10.1", "0.10.0"], offer.Newer.Select(release => release.Version.ToString()));
        Assert.Equal(["one", "zero"], offer.Newer.Select(release => release.Notes));
    }

    [Fact]
    public void PreReleasesCountAndDevBuildsAreOlderThanTheirRelease()
    {
        var releases = GitHubReleaseFeed.Read(Element(ListJson(Release("v0.10.0", prerelease: true))));

        Assert.Equal("0.10.0", UpdateCheck.Choose(releases, AppVersion.Parse("0.10.0-dev.7"))?.Version.ToString());
        Assert.Null(UpdateCheck.Choose(releases, AppVersion.Parse("0.10.0")));
        Assert.Null(UpdateCheck.Choose(releases, AppVersion.Parse("0.10.1-dev.1")));
    }

    [Fact]
    public void DraftsAndTagsThatAreNotVersionsAreSkipped()
    {
        var releases = GitHubReleaseFeed.Read(Element(ListJson(
            Release("v0.11.0", draft: true),
            Release("nightly"),
            Release("0.10.5"),
            Release("v0.10.1"))));

        Assert.Equal(["0.10.1"], releases.Select(release => release.Version.ToString()));
    }

    [Fact]
    public void NothingIsOfferedWhenNoNewerReleaseHasTheDownload()
    {
        var releases = GitHubReleaseFeed.Read(Element(ListJson(Release("v0.10.1", withDownload: false), Release("v0.10.0"))));

        Assert.Null(UpdateCheck.Choose(releases, AppVersion.Parse("0.10.0")));
    }

    [Fact]
    public async Task TheFeedAsksGitHubAsLoadoutAndReadsTheList()
    {
        var handler = new Handler(HttpStatusCode.OK, ListJson(Release("v0.10.1")));
        using var http = new HttpClient(handler);
        var feed = new GitHubReleaseFeed(http, "0.10.0-dev.3");

        var releases = await feed.ListAsync(CancellationToken.None);

        Assert.Single(releases);
        Assert.Equal(new Uri(GitHubReleaseFeed.DefaultUrl), handler.Request?.RequestUri);
        Assert.Equal("LoadoutPS2/0.10.0-dev.3", handler.Request?.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", handler.Request?.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limiting")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.OK, "not a list")]
    public async Task AFailedAnswerSaysWhatWentWrong(HttpStatusCode status, string said)
    {
        using var http = new HttpClient(new Handler(status, "{\"message\":\"nope\"}"));
        var feed = new GitHubReleaseFeed(http, "0.10.0");

        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => feed.ListAsync(CancellationToken.None));

        Assert.Contains(said, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNetworkSaysToCheckThatThePcIsOnline()
    {
        using var http = new HttpClient(new Handler(null, ""));
        var feed = new GitHubReleaseFeed(http, "0.10.0");

        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => feed.ListAsync(CancellationToken.None));

        Assert.Contains("online", error.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> Release(string tag, string body = "notes", bool prerelease = false, bool draft = false, bool withDownload = true)
    {
        var version = tag.TrimStart('v');
        var assets = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["name"] = "Loadout-Bay-PS2-" + version + ".zip",
                ["browser_download_url"] = "https://github.com/IVazharov/loadout-ps2/releases/download/" + tag + "/Loadout-Bay-PS2-" + version + ".zip",
                ["size"] = 1_200_000,
            },
        };
        if (withDownload)
        {
            assets.Add(new Dictionary<string, object?>
            {
                ["name"] = "Loadout-PS2-" + version + "-win-x64.zip",
                ["browser_download_url"] = "https://github.com/IVazharov/loadout-ps2/releases/download/" + tag + "/Loadout-PS2-" + version + "-win-x64.zip",
                ["size"] = 3_400_000,
            });
        }

        return new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["html_url"] = "https://github.com/IVazharov/loadout-ps2/releases/tag/" + tag,
            ["body"] = body,
            ["assets"] = assets,
        };
    }

    private static string ListJson(params Dictionary<string, object?>[] releases) => JsonSerializer.Serialize(releases);

    private static JsonElement Element(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string _body;

        public Handler(HttpStatusCode? status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (_status is not { } status)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(_body) });
        }
    }
}
