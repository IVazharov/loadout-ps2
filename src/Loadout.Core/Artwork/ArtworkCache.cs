using Loadout.Core;

namespace Loadout.Artwork;

public sealed record CachedArtwork(
    string CanonicalDiscId,
    OplArtKind Kind,
    string RemoteFileName,
    string RelativePath,
    string FullPath,
    ArtworkFormat Format,
    byte[] Content);

public sealed record CacheStoreResult(ArtworkInspectionStatus Status, CachedArtwork? Artwork, bool KeptExisting);

/// <summary>
/// Local artwork files. A rejected download does not replace a file that already validated.
/// </summary>
public sealed class ArtworkCache
{
    private readonly string _root;

    public ArtworkCache(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A cache directory is required.", nameof(root));
        }

        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>
    /// Cover file for a disc, or the icon file when the cover is missing.
    /// Null when neither file is cached. Callers must not invent a picture.
    /// </summary>
    public string? CoverOrIconPath(string? discId)
    {
        if (!DiscId.TryParse(discId, out var parsed))
        {
            return null;
        }

        var cover = TryGet(parsed, OplArtKind.Cover);
        if (!string.IsNullOrWhiteSpace(cover?.FullPath))
        {
            return cover.FullPath;
        }

        var icon = TryGet(parsed, OplArtKind.Icon);
        return string.IsNullOrWhiteSpace(icon?.FullPath) ? null : icon.FullPath;
    }

    public CachedArtwork? TryGet(DiscId discId, OplArtKind kind)
    {
        CachedArtwork? newest = null;
        CachedArtwork? currentLoaderShot = null;
        foreach (var format in new[] { ArtworkFormat.Png, ArtworkFormat.Jpeg })
        {
            if (kind == OplArtKind.Icon && format != ArtworkFormat.Png)
            {
                continue;
            }

            IReadOnlyList<string> names;
            try
            {
                names = OplArtNames.LookupFileNames(discId, kind, format);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var remoteName in names)
            {
                var relative = Relative(discId, remoteName);
                var full = Full(relative);
                if (!File.Exists(full))
                {
                    continue;
                }

                byte[] bytes;
                ArtworkInspection inspection;
                try
                {
                    bytes = File.ReadAllBytes(full);
                    inspection = ArtworkImages.Inspect(bytes);
                }
                catch (Exception ex) when (ex is IOException
                    or InvalidDataException
                    or ArgumentException
                    or InvalidOperationException
                    or FormatException
                    or NotSupportedException)
                {
                    continue;
                }

                if (!inspection.IsCacheable || inspection.Format != format)
                {
                    continue;
                }

                var cached = new CachedArtwork(discId.Canonical, kind, remoteName, relative, full, format, bytes);
                if (remoteName.EndsWith("_SCR.png", StringComparison.Ordinal))
                {
                    currentLoaderShot = cached;
                }
                else if (newest is null || File.GetLastWriteTimeUtc(full) >= File.GetLastWriteTimeUtc(newest.FullPath))
                {
                    newest = cached;
                }
            }
        }

        return currentLoaderShot ?? newest;
    }

    public CacheStoreResult Store(DiscId discId, OplArtKind kind, ReadOnlySpan<byte> content)
    {
        var existing = TryGet(discId, kind);
        var inspection = ArtworkImages.Inspect(content);
        if (!inspection.IsCacheable || (kind == OplArtKind.Icon && inspection.Format != ArtworkFormat.Png))
        {
            return new CacheStoreResult(inspection.Status, existing, existing is not null);
        }

        var remoteName = OplArtNames.FileName(discId, kind, inspection.Format);
        var relative = Relative(discId, remoteName);
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var partial = full + ".partial";
        File.WriteAllBytes(partial, content.ToArray());
        var written = ArtworkImages.Inspect(File.ReadAllBytes(partial));
        if (!written.IsCacheable)
        {
            File.Delete(partial);
            return new CacheStoreResult(written.Status, existing, existing is not null);
        }

        File.Move(partial, full, overwrite: true);
        var stored = new CachedArtwork(
            discId.Canonical,
            kind,
            remoteName,
            relative,
            full,
            inspection.Format,
            content.ToArray());
        return new CacheStoreResult(ArtworkInspectionStatus.Valid, stored, false);
    }

    /// <summary>Reads a file previously stored under this cache. Missing files return null.</summary>
    public byte[]? ReadRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return null;
        }

        var full = Full(relativePath);
        if (!File.Exists(full))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Relative(DiscId discId, string remoteName)
        => discId.OplFileStem + "/" + remoteName;

    private string Full(string relative)
        => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
}
