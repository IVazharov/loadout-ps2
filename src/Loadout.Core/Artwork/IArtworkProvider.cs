using System.Net;
using Loadout.Core;

namespace Loadout.Artwork;

public sealed record ArtworkDownload(string SourceName, byte[] Content);

public interface IArtworkProvider
{
    Task<ArtworkDownload?> DownloadAsync(DiscId discId, OplArtKind kind, CancellationToken cancellationToken);
}

/// <summary>
/// Reads one file from the Luden02 database layout. Tests should pass a fake provider
/// or an <see cref="HttpMessageHandler"/>. This type does not embed artwork.
/// </summary>
public sealed class Luden02ArtworkProvider : IArtworkProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public Luden02ArtworkProvider(HttpClient http, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? Luden02ArtworkLayout.DefaultBaseUrl : baseUrl;
        if (!_baseUrl.EndsWith('/'))
        {
            _baseUrl += "/";
        }
    }

    public async Task<ArtworkDownload?> DownloadAsync(DiscId discId, OplArtKind kind, CancellationToken cancellationToken)
    {
        var relative = Luden02ArtworkLayout.RelativePath(discId, kind);
        var address = new Uri(new Uri(_baseUrl, UriKind.Absolute), relative);
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var length = response.Content.Headers.ContentLength;
        if (length is > ArtworkImages.MaxBytes)
        {
            return new ArtworkDownload(relative, Array.Empty<byte>());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > ArtworkImages.MaxBytes)
            {
                return new ArtworkDownload(relative, Array.Empty<byte>());
            }

            memory.Write(buffer, 0, read);
        }

        return new ArtworkDownload(relative, memory.ToArray());
    }
}
