using System.Net.Http.Headers;

namespace Loadout.Discs;

/// <summary>Redump's list as the copy check gets it: from the cache, freshly downloaded, or not at all and why.</summary>
public sealed record RedumpDatResult(RedumpDat? Dat, bool Downloaded, string? Failure);

/// <summary>
/// Keeps Redump's PS2 list in Loadout's data folder. It is downloaded when there is none, and again when it is older than
/// <see cref="MaxAge"/>; when redump.org can't be reached, the copy it has is used, and with none, copies wait to be
/// checked. The list is not shipped with Loadout: Redump states no licence for it.
/// </summary>
public sealed class RedumpDatCache
{
    /// <summary>redump.org answers on http only (2026-11-11: nothing on port 443).</summary>
    public static readonly Uri Ps2DatUri = new("http://redump.org/datfile/ps2/");

    // The zip is about 1.3 MB; anything near this is not Redump's list.
    private const long MaxBytes = 64L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly string _clientVersion;
    private readonly TimeProvider _time;
    private readonly Uri _uri;

    public RedumpDatCache(HttpClient http, string folder, string clientVersion, TimeProvider? time = null, Uri? uri = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        _folder = folder;
        _clientVersion = clientVersion;
        _time = time ?? TimeProvider.System;
        _uri = uri ?? Ps2DatUri;
    }

    public static TimeSpan MaxAge { get; } = TimeSpan.FromDays(30);

    public string ZipPath => Path.Combine(_folder, "redump-ps2.zip");

    /// <summary>The list on disk, or null when there is none or it can't be read.</summary>
    public RedumpDat? LoadCached()
    {
        try
        {
            using var zip = File.OpenRead(ZipPath);
            return RedumpDat.ReadZip(zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The list, downloaded first when there is none, when it is old, or when <paramref name="refresh"/> asks.</summary>
    public async Task<RedumpDatResult> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        var cached = LoadCached();
        if (cached is not null && !refresh && _time.GetUtcNow() - File.GetLastWriteTimeUtc(ZipPath) < MaxAge)
        {
            return new RedumpDatResult(cached, Downloaded: false, Failure: null);
        }

        var failure = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            var fresh = LoadCached();
            if (fresh is not null)
            {
                return new RedumpDatResult(fresh, Downloaded: true, Failure: null);
            }

            failure = "Redump's list could not be read after it was downloaded.";
        }

        return new RedumpDatResult(cached, Downloaded: false, failure);
    }

    /// <summary>Downloads the list and puts it in place once it reads as one. Returns why it failed, or null.</summary>
    private async Task<string?> DownloadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LoadoutPS2", _clientVersion));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return "redump.org didn't answer. Check that this PC is online.";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "redump.org didn't answer in time.";
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return $"redump.org answered with an error ({(int)response.StatusCode}). Try again later.";
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                return "redump.org's answer was not its list of discs.";
            }

            Directory.CreateDirectory(_folder);
            var temporary = ZipPath + ".download";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > MaxBytes)
                        {
                            return "redump.org's answer was not its list of discs.";
                        }

                        await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    }
                }

                using (var check = File.OpenRead(temporary))
                {
                    RedumpDat.ReadZip(check);
                }

                File.Move(temporary, ZipPath, overwrite: true);
                return null;
            }
            catch (InvalidDataException)
            {
                return "redump.org's answer was not its list of discs.";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                return $"Redump's list could not be downloaded: {ex.Message}";
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
