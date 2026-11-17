using Loadout.Images;

namespace Loadout.Discs;

/// <summary>What compressing a copy made: the image's size and the chd's.</summary>
public sealed record DiscBackupCompression(long ImageBytes, long ChdBytes);

/// <summary>
/// Turns a complete copy into a chd beside it. The chd is written as .chd.part, read back whole with Loadout's own chd
/// reader and compared with the copy's hashes, and only a chd that reads back the same is given the name .chd. Removing
/// the image it replaces is the caller's: deleted for good when it was only the copy being made, sent to the Recycle Bin
/// when the user had kept it.
/// </summary>
public static class DiscBackupCompressor
{
    // Writing takes most of the time; reading back the rest.
    private const double WritingShare = 0.85;

    /// <summary>
    /// Compresses the copy and gives the sizes. <paramref name="progress"/> gets 0 to 1. Throws
    /// <see cref="InvalidDataException"/> when the chd does not read back the same, and leaves no chd then.
    /// </summary>
    public static async Task<DiscBackupCompression> CompressAsync(DiscBackupFiles files, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var image = new FileInfo(files.Image);
        if (!image.Exists)
        {
            throw new FileNotFoundException($"{files.Name} has no image to compress.", files.Image);
        }

        var expected = DiscBackupLibrary.ReadHashes(files.Hashes);
        if (expected is null)
        {
            await using var source = new FileStream(files.Image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            expected = await DiscImageHashes.ComputeAsync(source, null, cancellationToken).ConfigureAwait(false);
        }

        TryDelete(files.ChdPart);
        var total = (double)image.Length;
        var writing = new Forward<long>(read => progress?.Report(WritingShare * read / total));
        var length = files.IsCd
            ? await ChdWriter.WriteCdAsync(files.Image, DiscBackupLibrary.CdMode(files.Image) == 1 ? "MODE1_RAW" : "MODE2_RAW", files.ChdPart, writing, cancellationToken).ConfigureAwait(false)
            : await ChdWriter.WriteDvdAsync(files.Image, files.ChdPart, writing, cancellationToken).ConfigureAwait(false);

        DiscImageHashes back;
        try
        {
            await using var chd = ChdImageStream.Open(files.ChdPart);
            var reading = new Forward<long>(read => progress?.Report(WritingShare + ((1 - WritingShare) * read / total)));
            back = await DiscImageHashes.ComputeAsync(chd, reading, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(files.ChdPart);
            throw;
        }

        if (!back.SameAs(expected))
        {
            TryDelete(files.ChdPart);
            throw new InvalidDataException($"The chd of {files.Name} did not read back the same as the copy, so the copy is kept as it is.");
        }

        File.Move(files.ChdPart, files.Chd, overwrite: true);
        progress?.Report(1);
        return new DiscBackupCompression(image.Length, length);
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

    /// <summary>Passes progress on as it comes, from whichever thread reports it; the caller's own IProgress decides where it lands.</summary>
    internal sealed class Forward<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
