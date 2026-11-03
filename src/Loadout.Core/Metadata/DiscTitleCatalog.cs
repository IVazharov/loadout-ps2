using System.Net;
using Loadout.Core;
using Loadout.Persistence;

namespace Loadout.Metadata;

/// <summary>
/// <see cref="Failed"/> means the database could not be asked (offline or a server error), so
/// <see cref="Found"/> = false says nothing about the game. That answer is not cached.
/// </summary>
public sealed record DiscTitleLookup(string CanonicalDiscId, string? Title, bool Found, bool FromCache, bool Failed = false);

public interface IDiscTitleCatalog
{
    Task<DiscTitleLookup> LookupAsync(DiscId discId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads one CFG at a time. A cached title is returned without a network call.
/// The CFG text is parsed and discarded. Only the title is stored.
/// </summary>
public sealed class OplCfgTitleCatalog : IDiscTitleCatalog
{
    private readonly HttpClient _http;
    private readonly DiscTitleCache _cache;

    public OplCfgTitleCatalog(HttpClient http, DiscTitleCache cache)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<DiscTitleLookup> LookupAsync(DiscId discId, CancellationToken cancellationToken)
    {
        var cached = _cache.TryGet(discId.Canonical);
        if (cached is not null)
        {
            if (cached.Found && !string.IsNullOrWhiteSpace(cached.Title))
            {
                var cleaned = OplCfgDatabase.CleanTitle(cached.Title);
                if (!string.Equals(cleaned, cached.Title, StringComparison.Ordinal))
                {
                    _cache.Store(new DiscTitleEntry(discId.Canonical, cleaned, !string.IsNullOrWhiteSpace(cleaned)));
                    return new DiscTitleLookup(discId.Canonical, cleaned, !string.IsNullOrWhiteSpace(cleaned), true);
                }
            }

            return new DiscTitleLookup(discId.Canonical, cached.Title, cached.Found, true);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(OplCfgDatabase.RecordUri(discId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new DiscTitleLookup(discId.Canonical, null, false, false, Failed: true);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache.Store(new DiscTitleEntry(discId.Canonical, null, false));
                return new DiscTitleLookup(discId.Canonical, null, false, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DiscTitleLookup(discId.Canonical, null, false, false, Failed: true);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var title = OplCfgDatabase.ParseTitle(body);
            if (string.IsNullOrWhiteSpace(title))
            {
                _cache.Store(new DiscTitleEntry(discId.Canonical, null, false));
                return new DiscTitleLookup(discId.Canonical, null, false, false);
            }

            _cache.Store(new DiscTitleEntry(discId.Canonical, title, true));
            return new DiscTitleLookup(discId.Canonical, title, true, false);
        }
    }
}
