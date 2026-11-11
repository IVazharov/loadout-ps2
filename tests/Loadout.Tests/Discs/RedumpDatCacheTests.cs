using System.Net;

namespace Loadout.Discs;

public sealed class RedumpDatCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loadout-redump-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public async Task TheListIsDownloadedOnceAndThenComesFromTheCache()
    {
        var server = new Server(_ => Zip());
        using var http = new HttpClient(server);
        var cache = new RedumpDatCache(http, _folder, "0.11.0-dev.1", _time);

        var first = await cache.GetAsync(refresh: false, CancellationToken.None);
        var second = await cache.GetAsync(refresh: false, CancellationToken.None);

        Assert.True(first.Downloaded);
        Assert.Null(first.Failure);
        Assert.Equal(4, first.Dat?.Games.Count);
        Assert.False(second.Downloaded);
        Assert.Equal(4, second.Dat?.Games.Count);
        Assert.Equal(1, server.Calls);
        Assert.Equal(RedumpDatCache.Ps2DatUri, server.LastUri);
        Assert.Equal("LoadoutPS2/0.11.0-dev.1", server.LastUserAgent);
        Assert.False(File.Exists(cache.ZipPath + ".download"));
    }

    [Fact]
    public async Task AnOldListIsDownloadedAgain()
    {
        var server = new Server(_ => Zip());
        using var http = new HttpClient(server);
        var cache = new RedumpDatCache(http, _folder, "1.0.0", _time);
        await cache.GetAsync(refresh: false, CancellationToken.None);

        _time.Now += RedumpDatCache.MaxAge + TimeSpan.FromDays(1);
        var result = await cache.GetAsync(refresh: false, CancellationToken.None);

        Assert.True(result.Downloaded);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task RefreshDownloadsItAtOnce()
    {
        var server = new Server(_ => Zip());
        using var http = new HttpClient(server);
        var cache = new RedumpDatCache(http, _folder, "1.0.0", _time);
        await cache.GetAsync(refresh: false, CancellationToken.None);

        Assert.True((await cache.GetAsync(refresh: true, CancellationToken.None)).Downloaded);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task OfflineTheCachedListIsUsedAndTheFailureSaid()
    {
        var online = true;
        var server = new Server(_ => online ? Zip() : throw new HttpRequestException("offline"));
        using var http = new HttpClient(server);
        var cache = new RedumpDatCache(http, _folder, "1.0.0", _time);
        await cache.GetAsync(refresh: false, CancellationToken.None);
        online = false;

        var result = await cache.GetAsync(refresh: true, CancellationToken.None);

        Assert.Equal(4, result.Dat?.Games.Count);
        Assert.False(result.Downloaded);
        Assert.Equal("redump.org didn't answer. Check that this PC is online.", result.Failure);
    }

    [Fact]
    public async Task OfflineWithNoListThereIsNoneAndTheFailureIsSaid()
    {
        using var http = new HttpClient(new Server(_ => throw new HttpRequestException("offline")));
        var cache = new RedumpDatCache(http, _folder, "1.0.0", _time);

        var result = await cache.GetAsync(refresh: false, CancellationToken.None);

        Assert.Null(result.Dat);
        Assert.NotNull(result.Failure);
        Assert.False(File.Exists(cache.ZipPath));
    }

    [Fact]
    public async Task AnAnswerThatIsNotTheListLeavesTheCachedOneInPlace()
    {
        var good = true;
        var server = new Server(_ => good ? Zip() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") });
        using var http = new HttpClient(server);
        var cache = new RedumpDatCache(http, _folder, "1.0.0", _time);
        await cache.GetAsync(refresh: false, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(cache.ZipPath);
        good = false;

        var result = await cache.GetAsync(refresh: true, CancellationToken.None);

        Assert.Equal("redump.org's answer was not its list of discs.", result.Failure);
        Assert.Equal(4, result.Dat?.Games.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(cache.ZipPath));
        Assert.False(File.Exists(cache.ZipPath + ".download"));
    }

    [Fact]
    public async Task AServerErrorIsSaid()
    {
        using var http = new HttpClient(new Server(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var cache = new RedumpDatCache(http, _folder, "1.0.0", _time);

        var result = await cache.GetAsync(refresh: false, CancellationToken.None);

        Assert.Equal("redump.org answered with an error (503). Try again later.", result.Failure);
    }

    private static HttpResponseMessage Zip()
    {
        using var zip = RedumpDatTests.Zip(("Sony - PlayStation 2 - Datfile (4) (2026-01-02 03-04-05).dat", RedumpDatTests.Fixture));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip.ToArray()) };
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
