using Loadout.Images;

namespace Loadout.Discs;

/// <summary>What converting a chd back made: the chd's size and the image's.</summary>
public sealed record DiscBackupExpansion(long ChdBytes, long ImageBytes);

/// <summary>
/// Turns a copy kept as a chd back into the iso, or the CD's bin and cue, it was made from. The image is written as
/// .iso.part (.bin.part) from what the chd reads back as, hashed on the way, then read back from the drive and hashed
/// again. Only an image whose hashes are both the copy's own (its hashes file, written when it was checked against Redump)
/// is given its name, so it holds the same bytes that check saw, and is then marked made (.iso.pending, .bin.pending): it
/// is the copy from then on. A CD gets its cue sheet as Redump writes it. Removing the chd, and then the mark, is the
/// caller's.
/// </summary>
public static class DiscBackupExpander
{
    // Writing takes most of the time; reading back the rest.
    private const double WritingShare = 0.8;

    /// <summary>
    /// Converts the copy back and gives the sizes. <paramref name="progress"/> gets 0 to 1. Throws
    /// <see cref="InvalidDataException"/> when the image does not read back the same, and leaves no image then.
    /// </summary>
    public static async Task<DiscBackupExpansion> ExpandAsync(DiscBackupFiles files, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var chd = new FileInfo(files.Chd);
        if (!chd.Exists)
        {
            throw new FileNotFoundException($"{files.Name} has no chd to convert.", files.Chd);
        }

        var expected = DiscBackupLibrary.ReadHashes(files.Hashes);
        TryDelete(files.ImagePart);
        DiscImageHashes written;
        DiscImageHashes back;
        try
        {
            await using (var source = ChdImageStream.Open(files.Chd))
            {
                var total = (double)Math.Max(1, source.Length);
                var writing = new DiscBackupCompressor.Forward<long>(read => progress?.Report(WritingShare * read / total));
                await using var target = new FileStream(files.ImagePart, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                written = await DiscImageHashes.CopyAsync(source, target, writing, cancellationToken).ConfigureAwait(false);
            }

            await using (var image = new FileStream(files.ImagePart, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            {
                var total = (double)Math.Max(1, image.Length);
                var reading = new DiscBackupCompressor.Forward<long>(read => progress?.Report(WritingShare + ((1 - WritingShare) * read / total)));
                back = await DiscImageHashes.ComputeAsync(image, reading, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(files.ImagePart);
            throw;
        }

        if (!back.SameAs(written) || (expected is not null && !written.SameAs(expected)))
        {
            TryDelete(files.ImagePart);
            throw new InvalidDataException(
                $"The {(files.IsCd ? "bin" : "iso")} made from the chd of {files.Name} did not read back the same as the copy, so the copy is kept as a chd.");
        }

        if (files.IsCd)
        {
            files.WriteCue(DiscBackupLibrary.CdMode(files.ImagePart));
        }

        File.Move(files.ImagePart, files.Image, overwrite: true);
        File.WriteAllText(files.ImagePending, DiscBackupFiles.ImageMadeMark);
        progress?.Report(1);
        return new DiscBackupExpansion(chd.Length, written.Size);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
