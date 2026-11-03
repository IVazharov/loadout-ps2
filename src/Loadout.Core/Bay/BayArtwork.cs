using Loadout.Artwork;
using Loadout.Core;

namespace Loadout.Bay;

/// <summary>An installed game the Artwork page sends art for.</summary>
public sealed record ArtworkGame(DiscId DiscId, string Title);

/// <summary>One file to send: a cached image under one of the names OPL looks for in its ART folder.</summary>
public sealed record ArtworkFile(ArtworkGame Game, string RemoteName, CachedArtwork Cached);

/// <summary>What a send would do: the files to send, how many the PS2 already has, and the games with nothing cached.</summary>
public sealed record ArtworkPlan(IReadOnlyList<ArtworkFile> ToSend, int AlreadyOnPs2, IReadOnlyList<ArtworkGame> WithoutArt)
{
    public long Bytes => ToSend.Sum(file => (long)file.Cached.Content.Length);
}

public sealed record ArtworkSendProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile);

/// <summary>What a send did: the files sent, and each file Loadout Bay refused, with the reason.</summary>
public sealed record ArtworkSendResult(IReadOnlyList<ArtworkFile> Sent, IReadOnlyList<ArtworkSendFailure> Failed, ArtworkPlan Plan);

public sealed record ArtworkSendFailure(ArtworkFile File, string Reason);

/// <summary>
/// Sends cached artwork to OPL's ART folder through Loadout Bay. The PS2's own listing decides what
/// it lacks, so nothing is kept on the PC about what was sent before.
/// </summary>
public static class BayArtwork
{
    /// <summary>
    /// The cached files of these games that the PS2 does not have under their OPL names, or every
    /// cached file with <paramref name="replaceExisting"/>. Files of other games are never touched.
    /// </summary>
    public static ArtworkPlan Plan(IReadOnlyList<ArtworkGame> games, ArtworkCache cache, IReadOnlySet<string> onPs2, bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(onPs2);
        var send = new List<ArtworkFile>();
        var withoutArt = new List<ArtworkGame>();
        var already = 0;
        foreach (var game in games)
        {
            var any = false;
            foreach (var kind in OplArtCatalog.Kinds)
            {
                if (cache.TryGet(game.DiscId, kind) is not { } cached)
                {
                    continue;
                }

                any = true;
                foreach (var name in OplArtNames.UploadFileNames(game.DiscId, kind, cached.Format))
                {
                    if (!replaceExisting && onPs2.Contains(name))
                    {
                        already++;
                    }
                    else
                    {
                        send.Add(new ArtworkFile(game, name, cached));
                    }
                }
            }

            if (!any)
            {
                withoutArt.Add(game);
            }
        }

        return new ArtworkPlan(send, already, withoutArt);
    }

    /// <summary>
    /// Lists OPL's ART folder, plans, and sends every planned file over one connection. Each file
    /// replaces the PS2's copy only once all of it is there. A file Loadout Bay refuses does not stop the others.
    /// </summary>
    public static async Task<ArtworkSendResult> SendAsync(
        string host,
        IReadOnlyList<ArtworkGame> games,
        ArtworkCache cache,
        bool replaceExisting,
        IProgress<ArtworkSendProgress>? progress = null,
        int port = BayWire.Port,
        CancellationToken cancellationToken = default)
    {
        using var bay = await LoadoutBayClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var onPs2 = (await bay.ListFilesAsync(BayFolder.Art, cancellationToken).ConfigureAwait(false)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        var plan = Plan(games, cache, onPs2, replaceExisting);
        var sent = new List<ArtworkFile>();
        var failed = new List<ArtworkSendFailure>();
        long done = 0;
        for (var i = 0; i < plan.ToSend.Count; i++)
        {
            var file = plan.ToSend[i];
            var before = done;
            var index = i;
            progress?.Report(new ArtworkSendProgress(i, plan.ToSend.Count, done, plan.Bytes, file.RemoteName));
            try
            {
                await bay.WriteFileAsync(
                    BayFolder.Art,
                    file.RemoteName,
                    file.Cached.Content,
                    new Relay(bytes => progress?.Report(new ArtworkSendProgress(index, plan.ToSend.Count, before + bytes, plan.Bytes, file.RemoteName))),
                    cancellationToken).ConfigureAwait(false);
                sent.Add(file);
            }
            catch (LoadoutBayException ex) when (ex.Status != 0)
            {
                failed.Add(new ArtworkSendFailure(file, ex.Message));
            }

            done = before + file.Cached.Content.Length;
        }

        progress?.Report(new ArtworkSendProgress(plan.ToSend.Count, plan.ToSend.Count, plan.Bytes, plan.Bytes, ""));
        return new ArtworkSendResult(sent, failed, plan);
    }

    /// <summary>
    /// Sends one game's cached files that <paramref name="onPs2"/> does not name, over a connection already
    /// open, and adds what it sent to <paramref name="onPs2"/>. A file Loadout Bay refuses does not stop the
    /// others. A lost connection throws, with status 0.
    /// </summary>
    internal static async Task<ArtworkSendResult> SendGameAsync(
        LoadoutBayClient bay,
        ArtworkGame game,
        ArtworkCache cache,
        ISet<string> onPs2,
        CancellationToken cancellationToken)
    {
        var plan = Plan([game], cache, onPs2.ToHashSet(StringComparer.Ordinal), replaceExisting: false);
        var sent = new List<ArtworkFile>();
        var failed = new List<ArtworkSendFailure>();
        foreach (var file in plan.ToSend)
        {
            try
            {
                await bay.WriteFileAsync(BayFolder.Art, file.RemoteName, file.Cached.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
                sent.Add(file);
                onPs2.Add(file.RemoteName);
            }
            catch (LoadoutBayException ex) when (ex.Status != 0)
            {
                failed.Add(new ArtworkSendFailure(file, ex.Message));
            }
        }

        return new ArtworkSendResult(sent, failed, plan);
    }

    // Reports on the sender's thread, so the totals stay in order.
    private sealed class Relay(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
