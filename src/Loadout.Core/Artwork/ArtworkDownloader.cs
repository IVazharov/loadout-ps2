using System.Net.Sockets;
using Loadout.Core;

namespace Loadout.Artwork;

/// <summary>
/// Fills the artwork cache from the provider. A file already in the cache is not
/// downloaded again. A failed or missing download is skipped; only files that
/// validate are stored. Nothing here contacts the PS2.
/// </summary>
public sealed class ArtworkDownloader
{
    private readonly IArtworkProvider _provider;
    private readonly ArtworkCache _cache;

    public ArtworkDownloader(IArtworkProvider provider, ArtworkCache cache)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Downloads every missing kind for each disc and returns what is cached afterwards.</summary>
    public async Task<IReadOnlyList<CachedArtwork>> DownloadAsync(
        IReadOnlyList<DiscId> discs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discs);
        var ready = new List<CachedArtwork>();
        foreach (var discId in discs)
        {
            ready.AddRange(await CollectAsync(discId, cancellationToken).ConfigureAwait(false));
        }

        return ready;
    }

    public async Task<IReadOnlyList<CachedArtwork>> CollectAsync(DiscId discId, CancellationToken cancellationToken = default)
    {
        var ready = new List<CachedArtwork>();
        foreach (var kind in OplArtCatalog.Kinds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = _cache.TryGet(discId, kind);
            if (existing is not null)
            {
                ready.Add(existing);
                continue;
            }

            ArtworkDownload? download;
            try
            {
                download = await _provider.DownloadAsync(discId, kind, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDownloadFailure(ex, cancellationToken))
            {
                continue;
            }

            if (download is null)
            {
                continue;
            }

            var stored = _cache.Store(discId, kind, download.Content);
            if (stored.Artwork is not null)
            {
                ready.Add(stored.Artwork);
            }
        }

        return ready;
    }

    private static bool IsDownloadFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is HttpRequestException or IOException or SocketException or TaskCanceledException;
    }
}
