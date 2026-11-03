using System.Net;
using Loadout.Core;

namespace Loadout.Artwork;

public class ArtworkTests
{
    [Fact]
    public void GuideFilenamesIncludeTheDiscIdAndRegion()
    {
        var europe = Disc("SLES_538.86");
        var america = Disc("SLUS_538.86");

        Assert.Equal("SLES_538.86_COV.jpg", OplArtNames.FileName(europe, OplArtKind.Cover, ArtworkFormat.Jpeg));
        Assert.Equal("SLES_538.86_ICO.png", OplArtNames.FileName(europe, OplArtKind.Icon, ArtworkFormat.Png));
        Assert.Equal("SLES_538.86_BG.jpg", OplArtNames.FileName(europe, OplArtKind.Background, ArtworkFormat.Jpeg));
        Assert.Equal("SLES_538.86_SCR.png", OplArtNames.FileName(europe, OplArtKind.Screenshot1, ArtworkFormat.Png));
        Assert.Equal("SLES_538.86_SCR1.jpg", OplArtNames.FileName(europe, OplArtKind.Screenshot1, ArtworkFormat.Jpeg));
        Assert.Equal("SLES_538.86_SCR2.jpg", OplArtNames.FileName(europe, OplArtKind.Screenshot2, ArtworkFormat.Jpeg));
        Assert.Equal("SLES_538.86_SCR2.png", OplArtNames.FileName(europe, OplArtKind.Screenshot2, ArtworkFormat.Png));
        Assert.Equal(
            ["SLES_538.86_SCR.png", "SLES_538.86_SCR1.png"],
            OplArtNames.UploadFileNames(europe, OplArtKind.Screenshot1, ArtworkFormat.Png));
        Assert.Equal(
            ["SLES_538.86_SCR1.jpg"],
            OplArtNames.UploadFileNames(europe, OplArtKind.Screenshot1, ArtworkFormat.Jpeg));
        Assert.Equal(["SLES_538.86_COV.png"], OplArtNames.UploadFileNames(europe, OplArtKind.Cover, ArtworkFormat.Png));
        var gnome = Disc("SLUS-20745");
        Assert.Equal(["SLUS_207.45_COV.png"], OplArtNames.UploadFileNames(gnome, OplArtKind.Cover, ArtworkFormat.Png));
        Assert.Equal("SLES_538.86_COV.png", OplArtNames.FileName(europe, OplArtKind.Cover, ArtworkFormat.Png));
        Assert.Equal("SLUS_538.86_COV.jpg", OplArtNames.FileName(america, OplArtKind.Cover, ArtworkFormat.Jpeg));
        Assert.NotEqual(
            OplArtNames.FileName(europe, OplArtKind.Cover, ArtworkFormat.Jpeg),
            OplArtNames.FileName(america, OplArtKind.Cover, ArtworkFormat.Jpeg));
        Assert.Equal(OplArtNames.GuideCoverExample, "SLES_544.39_COV.jpg");
        Assert.Equal(OplArtNames.GuideIconExample, "SLES_544.39_ICO.png");
        Assert.Equal(OplArtNames.GuideBackgroundExample, "SLES_544.39_BG.jpg");
        Assert.Equal(OplArtNames.GuideScreenshot1Example, "SLES_544.39_SCR1.jpg");
        Assert.Equal(OplArtNames.GuideScreenshot2Example, "SLES_544.39_SCR2.jpg");
        Assert.Equal(OplArtNames.CurrentLoaderScreenshotExample, "SLES_544.39_SCR.png");
        Assert.Equal("4cc74baabb73be9ca43f817474b321cd70fbcc2d", OplArtNames.CurrentOplMasterCommit);
        Assert.Equal(["COV", "ICO", "BG", "SCR", "SCR2"], OplArtNames.CurrentLoaderSuffixes);
    }

    [Fact]
    public void IconArtIsPng()
    {
        var disc = Disc("SLES_538.86");
        Assert.Throws<ArgumentException>(() => OplArtNames.FileName(disc, OplArtKind.Icon, ArtworkFormat.Jpeg));
    }

    [Fact]
    public void Luden02PathsUseTheDatabaseNames()
    {
        var europe = Disc("SLES_538.86");
        var america = Disc("SLUS_538.86");
        Assert.Equal("PS2/SLES_538.86/SLES_538.86_COV.png", Luden02ArtworkLayout.RelativePath(europe, OplArtKind.Cover));
        Assert.Equal("PS2/SLES_538.86/SLES_538.86_ICO.png", Luden02ArtworkLayout.RelativePath(europe, OplArtKind.Icon));
        Assert.Equal("PS2/SLES_538.86/SLES_538.86_BG_00.png", Luden02ArtworkLayout.RelativePath(europe, OplArtKind.Background));
        Assert.Equal("PS2/SLES_538.86/SLES_538.86_SCR_00.png", Luden02ArtworkLayout.RelativePath(europe, OplArtKind.Screenshot1));
        Assert.Equal("PS2/SLES_538.86/SLES_538.86_SCR_01.png", Luden02ArtworkLayout.RelativePath(europe, OplArtKind.Screenshot2));
        Assert.Equal("PS2/SLUS_538.86/SLUS_538.86_COV.png", Luden02ArtworkLayout.RelativePath(america, OplArtKind.Cover));
    }

    [Fact]
    public void HtmlAndBlankImagesAreNotCachedOverAGoodFile()
    {
        var root = NewDirectory();
        var cache = new ArtworkCache(root);
        var disc = Disc("SLES_538.86");
        var good = TinyImages.Png((10, 20, 30), (40, 50, 60));
        var stored = cache.Store(disc, OplArtKind.Cover, good);
        Assert.Equal(ArtworkInspectionStatus.Valid, stored.Status);
        Assert.NotNull(stored.Artwork);
        var path = stored.Artwork!.FullPath;
        var before = File.ReadAllBytes(path);

        var html = cache.Store(disc, OplArtKind.Cover, TinyImages.Html);
        Assert.Equal(ArtworkInspectionStatus.NotAnImage, html.Status);
        Assert.True(html.KeptExisting);
        Assert.Equal(before, File.ReadAllBytes(path));

        var empty = cache.Store(disc, OplArtKind.Cover, Array.Empty<byte>());
        Assert.Equal(ArtworkInspectionStatus.Empty, empty.Status);
        Assert.True(empty.KeptExisting);
        Assert.Equal(before, File.ReadAllBytes(path));

        var blank = cache.Store(disc, OplArtKind.Cover, TinyImages.SolidPng(0, 0, 0));
        Assert.Equal(ArtworkInspectionStatus.Blank, blank.Status);
        Assert.True(blank.KeptExisting);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("SLES_538.86_COV.png", stored.Artwork.RemoteFileName);
    }

    [Fact]
    public void HtmlIsNotWrittenWhenTheCacheIsEmpty()
    {
        var cache = new ArtworkCache(NewDirectory());
        var result = cache.Store(Disc("SLES_538.86"), OplArtKind.Cover, TinyImages.Html);
        Assert.Null(result.Artwork);
        Assert.False(result.KeptExisting);
        Assert.Empty(Directory.GetFiles(cache.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void JpegAndFilteredPngPassInspection()
    {
        var jpeg = ArtworkImages.Inspect(TinyImages.Jpeg);
        Assert.Equal(ArtworkInspectionStatus.Valid, jpeg.Status);
        Assert.Equal(ArtworkFormat.Jpeg, jpeg.Format);
        Assert.Equal(2, jpeg.Width);
        Assert.Equal(2, jpeg.Height);

        var filtered = ArtworkImages.Inspect(TinyImages.FilteredPng());
        Assert.Equal(ArtworkInspectionStatus.Valid, filtered.Status);
        Assert.Equal(ArtworkFormat.Png, filtered.Format);
    }

    [Fact]
    public void PreviewKeepsAPathOrBytes()
    {
        var fromPath = ArtworkPreview.FromFile("/tmp/SLES_538.86_COV.png");
        Assert.Equal("/tmp/SLES_538.86_COV.png", fromPath.FilePath);
        Assert.Null(fromPath.Bytes);

        var bytes = TinyImages.Png((1, 2, 3), (4, 5, 6));
        var fromBytes = ArtworkPreview.FromBytes(bytes);
        Assert.Null(fromBytes.FilePath);
        Assert.Equal(bytes, fromBytes.Bytes);
        Assert.NotSame(bytes, fromBytes.Bytes);
    }

    [Fact]
    public async Task ProviderHtmlIsNotCachedAsACover()
    {
        var root = NewDirectory();
        var cache = new ArtworkCache(Path.Combine(root, "artwork-cache"));
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("_COV.png", StringComparison.Ordinal)
                ? TinyImages.Html
                : TinyImages.Png((2, 3, 4), (5, 6, 7));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });
        var provider = new Luden02ArtworkProvider(new HttpClient(handler), "http://art.test/db/");

        var ready = await new ArtworkDownloader(provider, cache).CollectAsync(Disc("SLES_538.86"));

        Assert.DoesNotContain(ready, art => art.Kind == OplArtKind.Cover);
        Assert.Contains(ready, art => art.Kind == OplArtKind.Icon);
        Assert.False(File.Exists(Path.Combine(cache.Root, "SLES_538.86", "SLES_538.86_COV.png")));
        Assert.False(File.Exists(Path.Combine(cache.Root, "SLES_538.86", "SLES_538.86_COV.jpg")));
    }

    private static DiscId Disc(string text)
    {
        Assert.True(DiscId.TryParse(text, out var discId));
        return discId;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "loadout-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class MapProvider : IArtworkProvider
    {
        public Dictionary<(string Disc, OplArtKind Kind), byte[]> Files { get; } = new();

        public int Calls { get; private set; }

        public bool ThrowIfCalled { get; init; }

        public Task<ArtworkDownload?> DownloadAsync(DiscId discId, OplArtKind kind, CancellationToken cancellationToken)
        {
            Calls++;
            if (ThrowIfCalled)
            {
                throw new InvalidOperationException("The provider was called.");
            }

            if (!Files.TryGetValue((discId.Canonical, kind), out var bytes))
            {
                return Task.FromResult<ArtworkDownload?>(null);
            }

            return Task.FromResult<ArtworkDownload?>(new ArtworkDownload(kind.ToString(), bytes));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
