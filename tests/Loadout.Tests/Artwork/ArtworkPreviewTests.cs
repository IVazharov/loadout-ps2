using System.Net;
using Loadout.Core;
using Loadout.Persistence;

namespace Loadout.Artwork;

public class ArtworkPreviewTests
{
    [Fact]
    public void AnEmptyOrGarbageCacheFileIsNotAPictureAndDoesNotThrow()
    {
        var root = NewDirectory();
        var cache = new ArtworkCache(Path.Combine(root, "artwork-cache"));
        var coverPath = Path.Combine(cache.Root, "SLES_538.86", "SLES_538.86_COV.png");
        Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!);
        File.WriteAllBytes(coverPath, Array.Empty<byte>());
        var iconPath = Path.Combine(cache.Root, "SLES_538.86", "SLES_538.86_ICO.png");
        File.WriteAllBytes(iconPath, "<html>not an image</html>"u8.ToArray());

        Assert.Null(cache.CoverOrIconPath("SLES-53886"));
    }

    [Fact]
    public void AGameWithoutACachedFileHasNoPicture()
    {
        var root = NewDirectory();
        var cache = new ArtworkCache(Path.Combine(root, "artwork-cache"));
        cache.Store(Disc("SLUS_200.73"), OplArtKind.Cover, TinyImages.Png((1, 2, 3), (4, 5, 6)));

        Assert.Null(cache.CoverOrIconPath("SLES-53886"));
        Assert.Null(cache.CoverOrIconPath("not a disc"));
        Assert.EndsWith("SLUS_200.73_COV.png", cache.CoverOrIconPath("SLUS-20073"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIconIsUsedWhenTheCoverIsMissing()
    {
        var root = NewDirectory();
        var cache = new ArtworkCache(Path.Combine(root, "artwork-cache"));
        cache.Store(Disc("SLES_538.86"), OplArtKind.Icon, TinyImages.Png((1, 1, 1), (2, 2, 2)));

        Assert.EndsWith("SLES_538.86_ICO.png", cache.CoverOrIconPath("SLES-53886"), StringComparison.Ordinal);

        cache.Store(Disc("SLES_538.86"), OplArtKind.Cover, TinyImages.Png((3, 3, 3), (4, 4, 4)));
        Assert.EndsWith("SLES_538.86_COV.png", cache.CoverOrIconPath("SLES-53886"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheRequestedDiscsAreDownloadedAndAKnownFileIsNotFetchedAgain()
    {
        var root = NewDirectory();
        var cache = new ArtworkCache(Path.Combine(root, "artwork-cache"));
        var provider = new MapProvider();
        var png = TinyImages.Png((8, 8, 8), (9, 9, 9));
        provider.Files[(Disc("SLES_538.86").Canonical, OplArtKind.Cover)] = png;
        provider.Files[(Disc("SLUS_200.73").Canonical, OplArtKind.Cover)] = png;
        var downloader = new ArtworkDownloader(provider, cache);

        var ready = await downloader.DownloadAsync([Disc("SLES_538.86")]);

        Assert.Contains(ready, art => art.CanonicalDiscId == Disc("SLES_538.86").Canonical && art.Kind == OplArtKind.Cover);
        Assert.DoesNotContain(provider.Requested, disc => disc == Disc("SLUS_200.73").Canonical);
        Assert.NotNull(new ArtworkCache(Path.Combine(root, "artwork-cache")).TryGet(Disc("SLES_538.86"), OplArtKind.Cover));
        Assert.Null(new ArtworkCache(Path.Combine(root, "artwork-cache")).TryGet(Disc("SLUS_200.73"), OplArtKind.Cover));

        var requests = provider.Requested.Count;
        await downloader.DownloadAsync([Disc("SLES_538.86")]);
        Assert.Equal(requests + OplArtCatalog.Kinds.Count - 1, provider.Requested.Count);
    }

    private static DiscId Disc(string text)
    {
        Assert.True(DiscId.TryParse(text, out var discId));
        return discId;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "loadout-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MapProvider : IArtworkProvider
    {
        public Dictionary<(string Disc, OplArtKind Kind), byte[]> Files { get; } = new();

        public List<string> Requested { get; } = new();

        public Task<ArtworkDownload?> DownloadAsync(DiscId discId, OplArtKind kind, CancellationToken cancellationToken)
        {
            Requested.Add(discId.Canonical);
            if (!Files.TryGetValue((discId.Canonical, kind), out var bytes))
            {
                return Task.FromResult<ArtworkDownload?>(null);
            }

            return Task.FromResult<ArtworkDownload?>(new ArtworkDownload(kind.ToString(), bytes));
        }
    }
}
