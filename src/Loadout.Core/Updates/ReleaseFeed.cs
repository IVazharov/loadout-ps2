using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Loadout.Updates;

/// <summary>One file of a release on GitHub.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>A published release: its version from the <c>v</c> tag, its notes (the changelog's section), and its files.</summary>
public sealed record PublishedRelease(
    AppVersion Version,
    Uri PageUrl,
    string Notes,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// A newer Loadout to offer: the newest release that has the Windows download, and the notes of every release
/// newer than this one up to it, newest first.
/// </summary>
public sealed record UpdateOffer(PublishedRelease Release, ReleaseAsset Download, IReadOnlyList<PublishedRelease> Newer)
{
    public AppVersion Version => Release.Version;
}

/// <summary>Why the release list could not be read, in words for the user.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException()
    {
    }

    public UpdateCheckException(string message)
        : base(message)
    {
    }

    public UpdateCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class UpdateCheck
{
    /// <summary>The Windows download's name in a release, as the release workflow names it.</summary>
    public static string DownloadName(AppVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return "Loadout-PS2-" + version + "-win-x64.zip";
    }

    /// <summary>
    /// The newest release newer than <paramref name="current"/> that has the Windows download, or null when there is none.
    /// Pre-releases count: until 1.0 every release is one.
    /// </summary>
    public static UpdateOffer? Choose(IEnumerable<PublishedRelease> releases, AppVersion current)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(current);
        var newer = releases.Where(release => release.Version > current).OrderByDescending(release => release.Version).ToList();
        foreach (var release in newer)
        {
            var download = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, DownloadName(release.Version), StringComparison.OrdinalIgnoreCase));
            if (download is not null)
            {
                return new UpdateOffer(release, download, newer.Where(other => other.Version <= release.Version).ToList());
            }
        }

        return null;
    }
}

/// <summary>
/// Reads Loadout's releases from GitHub's API, one request with no account. GitHub's "latest release" leaves
/// pre-releases out, so this lists them all; drafts and tags that are not <c>v</c> and a version are skipped.
/// </summary>
public sealed class GitHubReleaseFeed
{
    public const string DefaultUrl = "https://api.github.com/repos/IVazharov/loadout-ps2/releases?per_page=30";

    // The list is a few kilobytes per release; anything near this is not GitHub's answer.
    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string _clientVersion;

    public GitHubReleaseFeed(HttpClient http, string clientVersion, Uri? url = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        _clientVersion = clientVersion;
        _url = url ?? new Uri(DefaultUrl);
    }

    public async Task<IReadOnlyList<PublishedRelease>> ListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LoadoutPS2", _clientVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException("GitHub didn't answer. Check that this PC is online.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub didn't answer in time. Check that this PC is online.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateCheckException("GitHub is limiting requests from this network for now. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException("GitHub answered with an error (" + (int)response.StatusCode + "). Try again later.");
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                throw new UpdateCheckException("GitHub's answer was not a list of releases.");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Read(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                throw new UpdateCheckException("GitHub's answer was not a list of releases.", ex);
            }
            catch (IOException ex)
            {
                throw new UpdateCheckException("The connection to GitHub broke off. Try again.", ex);
            }
        }
    }

    /// <summary>The releases in GitHub's list, without drafts and tags that don't name a version.</summary>
    public static IReadOnlyList<PublishedRelease> Read(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The release list is not an array.");
        }

        var releases = new List<PublishedRelease>();
        foreach (var item in list.EnumerateArray())
        {
            if (Bool(item, "draft") || Text(item, "tag_name") is not { } tag || !tag.StartsWith('v')
                || !AppVersion.TryParse(tag[1..], out var version)
                || !Uri.TryCreate(Text(item, "html_url"), UriKind.Absolute, out var page))
            {
                continue;
            }

            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (Text(file, "name") is { } name
                        && Uri.TryCreate(Text(file, "browser_download_url"), UriKind.Absolute, out var download)
                        && download.Scheme == Uri.UriSchemeHttps
                        && file.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                    {
                        assets.Add(new ReleaseAsset(name, download, bytes));
                    }
                }
            }

            releases.Add(new PublishedRelease(version, page, Text(item, "body") ?? string.Empty, assets));
        }

        return releases;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
