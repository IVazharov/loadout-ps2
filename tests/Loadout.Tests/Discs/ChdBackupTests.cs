using System.Security.Cryptography;
using Loadout.Images;

namespace Loadout.Discs;

/// <summary>Copies kept as chds: compressed only when the chd reads back the same, then listed as chds.</summary>
public sealed class ChdBackupTests : IDisposable
{
    private const long DvdSectors = 300;
    private const long CdSectors = 41;
    private const string DvdIdentity = "SLES-55002 300 0123456789abcdef";
    private const string CdIdentity = "SLES-50219 41 0123456789abcdef";

    private readonly string _folder = Directory.CreateTempSubdirectory("loadout-chd-backup-").FullName;

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public async Task ADvdCopyBecomesAChdThatReadsBackAsItsIso()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        var iso = File.ReadAllBytes(files.Image);
        var reports = new List<double>();

        var made = await DiscBackupCompressor.CompressAsync(files, new Collect(reports), CancellationToken.None);

        Assert.True(File.Exists(files.Chd));
        Assert.False(File.Exists(files.ChdPart));
        Assert.Equal(iso.Length, made.ImageBytes);
        Assert.Equal(new FileInfo(files.Chd).Length, made.ChdBytes);
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(iso, ReadChd(files.Chd));

        // The image stays for the caller to remove; the chd is the copy from now on.
        Assert.True(File.Exists(files.Image));
        File.Delete(files.Image);
        var listed = Assert.Single(DiscBackupLibrary.List(_folder));
        Assert.Equal(DiscBackupFormat.Chd, listed.Format);
        Assert.False(listed.Files.IsCd);
        Assert.Equal(DiscBackupResult.NotCheckedYet, listed.Result);
        Assert.Equal(made.ChdBytes, listed.BytesOnDisk);
        Assert.Equal(DvdSectors * DiscSectors.Size, listed.Bytes);
        Assert.Equal(files.Chd, DiscBackupInstall.ScanPath(listed.Files));
    }

    [Fact]
    public async Task ACdCopyBecomesAChdAndIsStillKnownAsACd()
    {
        var files = await FinishedCd("NBA Street [SLES-50219]");
        var bin = File.ReadAllBytes(files.Image);

        await DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None);

        Assert.Equal(bin, ReadChd(files.Chd));
        foreach (var path in files.Uncompressed)
        {
            File.Delete(path);
        }

        var listed = Assert.Single(DiscBackupLibrary.List(_folder));
        Assert.Equal(DiscBackupFormat.Chd, listed.Format);
        Assert.True(listed.Files.IsCd);
        Assert.Equal(CdSectors * 2352, listed.Bytes);
        Assert.Equal(DiscBackupResult.NotCheckedYet, listed.Result);
    }

    [Fact]
    public async Task AChdThatDoesNotReadBackTheSameIsNotKept()
    {
        var files = await FinishedDvd("Game [SLES-55002]");

        // The hashes say other bytes than the image holds, as after the image changed on the drive.
        var hashes = File.ReadAllText(files.Hashes).Replace("SHA-1   ", "SHA-1   0", StringComparison.Ordinal);
        File.WriteAllText(files.Hashes, hashes);

        await Assert.ThrowsAsync<InvalidDataException>(() => DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None));

        Assert.False(File.Exists(files.Chd));
        Assert.False(File.Exists(files.ChdPart));
        Assert.True(File.Exists(files.Image));
        Assert.Equal(DiscBackupFormat.Iso, Assert.Single(DiscBackupLibrary.List(_folder)).Format);
    }

    [Fact]
    public async Task ACancelledCompressionLeavesTheCopyAsItWas()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscBackupCompressor.CompressAsync(files, null, cancel.Token));

        Assert.False(File.Exists(files.Chd));
        Assert.False(File.Exists(files.ChdPart));
        Assert.True(File.Exists(files.Image));
    }

    [Fact]
    public async Task ACopyMarkedToBeCompressedIsFoundAgain()
    {
        var marked = await FinishedDvd("Marked [SLES-55002]");
        await FinishedDvd("Kept as it is [SLES-55002]");
        File.WriteAllText(marked.ChdPending, string.Empty);

        var pending = Assert.Single(DiscBackupLibrary.PendingConversions(_folder));

        Assert.Equal(marked.Name, pending.Backup.Files.Name);
        Assert.Equal(DiscBackupFormat.Chd, pending.To);
        Assert.True(pending.Interim);
        Assert.Empty(DiscBackupLibrary.PendingConversions(Path.Combine(_folder, "missing")));
    }

    [Fact]
    public async Task PendingConversionsSayWhichWayAndWhetherTheUserAskedForThem()
    {
        var toChd = await FinishedDvd("To chd [SLES-55002]");
        File.WriteAllText(toChd.ChdPending, DiscBackupLibrary.ConvertMark);
        File.SetLastWriteTimeUtc(toChd.ChdPending, new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc));
        var toIso = await FinishedDvd("To iso [SLES-55002]");
        await DiscBackupCompressor.CompressAsync(toIso, null, CancellationToken.None);
        File.Delete(toIso.Image);
        File.WriteAllText(toIso.ImagePending, string.Empty);
        File.SetLastWriteTimeUtc(toIso.ImagePending, new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc));
        var toBin = await FinishedCd("To bin [SLES-50219]");
        await DiscBackupCompressor.CompressAsync(toBin, null, CancellationToken.None);
        File.Delete(toBin.Image);
        File.Delete(toBin.Cue);
        File.WriteAllText(toBin.ImagePending, string.Empty);
        File.SetLastWriteTimeUtc(toBin.ImagePending, new DateTime(2026, 11, 17, 11, 0, 0, DateTimeKind.Utc));

        var pending = DiscBackupLibrary.PendingConversions(_folder);

        // Oldest mark first, so a batch goes on in the order it was asked for.
        Assert.Equal(["To iso [SLES-55002]", "To chd [SLES-55002]", "To bin [SLES-50219]"], pending.Select(p => p.Backup.Files.Name));
        Assert.Equal([DiscBackupFormat.Iso, DiscBackupFormat.Chd, DiscBackupFormat.BinCue], pending.Select(p => p.To));
        Assert.All(pending, p => Assert.False(p.Interim));
        Assert.True(pending[2].Backup.Files.IsCd);
    }

    [Fact]
    public async Task ADvdChdBecomesItsIsoAgain()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        var iso = File.ReadAllBytes(files.Image);
        await DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None);
        File.Delete(files.Image);
        var reports = new List<double>();

        var made = await DiscBackupExpander.ExpandAsync(files, new Collect(reports), CancellationToken.None);

        Assert.Equal(iso, File.ReadAllBytes(files.Image));
        Assert.False(File.Exists(files.ImagePart));
        Assert.Equal(iso.Length, made.ImageBytes);
        Assert.Equal(new FileInfo(files.Chd).Length, made.ChdBytes);
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.Order()));

        // The image converted back is marked made: it is the copy while the chd waits to be removed.
        Assert.Equal(DiscBackupFiles.ImageMadeMark, File.ReadAllText(files.ImagePending));
        var listed = Assert.Single(DiscBackupLibrary.List(_folder));
        Assert.Equal(DiscBackupFormat.Iso, listed.Format);
        Assert.Equal(iso.Length, listed.BytesOnDisk);
        Assert.Equal(files.Image, DiscBackupInstall.ScanPath(listed.Files));
        Assert.Contains(files.ImagePart, files.All);
        Assert.Contains(files.ImagePending, files.All);

        // An image not marked made beside a chd is only left over (as after compressing): the chd is the copy.
        File.WriteAllText(files.ImagePending, string.Empty);
        Assert.Equal(DiscBackupFormat.Chd, Assert.Single(DiscBackupLibrary.List(_folder)).Format);
        File.Delete(files.ImagePending);
        Assert.Equal(DiscBackupFormat.Chd, Assert.Single(DiscBackupLibrary.List(_folder)).Format);
    }

    [Fact]
    public async Task ACdChdBecomesItsBinAndCueAgain()
    {
        var files = await FinishedCd("NBA Street [SLES-50219]");
        var bin = File.ReadAllBytes(files.Image);
        var cue = File.ReadAllText(files.Cue);
        await DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None);
        foreach (var path in files.Uncompressed)
        {
            File.Delete(path);
        }

        var cdFiles = Assert.Single(DiscBackupLibrary.List(_folder)).Files;
        await DiscBackupExpander.ExpandAsync(cdFiles, null, CancellationToken.None);
        File.Delete(cdFiles.Chd);

        Assert.Equal(bin, File.ReadAllBytes(files.Image));
        Assert.Equal(cue, File.ReadAllText(files.Cue));
        var listed = Assert.Single(DiscBackupLibrary.List(_folder));
        Assert.Equal(DiscBackupFormat.BinCue, listed.Format);
        Assert.Equal(files.Cue, DiscBackupInstall.ScanPath(listed.Files));
    }

    [Fact]
    public async Task AnImageThatDoesNotMatchTheCopysHashesIsNotKept()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        await DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None);
        File.Delete(files.Image);
        var hashes = File.ReadAllText(files.Hashes).Replace("MD5     ", "MD5     0", StringComparison.Ordinal);
        File.WriteAllText(files.Hashes, hashes);

        await Assert.ThrowsAsync<InvalidDataException>(() => DiscBackupExpander.ExpandAsync(files, null, CancellationToken.None));

        Assert.False(File.Exists(files.Image));
        Assert.False(File.Exists(files.ImagePart));
        Assert.True(File.Exists(files.Chd));
        Assert.Equal(DiscBackupFormat.Chd, Assert.Single(DiscBackupLibrary.List(_folder)).Format);
    }

    [Fact]
    public async Task ACancelledConversionBackLeavesTheChdAsItWas()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        await DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None);
        File.Delete(files.Image);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscBackupExpander.ExpandAsync(files, null, cancel.Token));

        Assert.False(File.Exists(files.Image));
        Assert.False(File.Exists(files.ImagePart));
        Assert.True(File.Exists(files.Chd));
    }

    [Fact]
    public async Task AChdWithoutAHashesFileIsCheckedAgainstItsOwnReadBack()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        var iso = File.ReadAllBytes(files.Image);
        await DiscBackupCompressor.CompressAsync(files, null, CancellationToken.None);
        File.Delete(files.Image);
        File.Delete(files.Hashes);

        await DiscBackupExpander.ExpandAsync(files, null, CancellationToken.None);

        Assert.Equal(iso, File.ReadAllBytes(files.Image));
    }

    [Fact]
    public async Task TheChdAndItsMarksGoWithTheCopy()
    {
        var files = await FinishedDvd("Game [SLES-55002]");
        File.WriteAllText(files.Chd, "chd");
        File.WriteAllText(files.ChdPending, string.Empty);

        var renamed = files.RenameTo("Game (Europe)");

        Assert.Equal("chd", File.ReadAllText(renamed.Chd));
        Assert.True(File.Exists(renamed.ChdPending));
        Assert.False(File.Exists(files.Chd));
        Assert.Contains(renamed.Chd, renamed.All);
        Assert.Contains(renamed.ChdPart, renamed.All);

        // A chd alone takes its name, as an iso does.
        Assert.Equal("Game (Europe) (2)", DiscBackupFiles.Unused(_folder, "Game (Europe)").Name);
    }

    private static byte[] ReadChd(string path)
    {
        using var image = ChdImageStream.Open(path);
        using var copy = new MemoryStream();
        image.CopyTo(copy);
        return copy.ToArray();
    }

    private async Task<DiscBackupFiles> FinishedDvd(string name)
    {
        var files = new DiscBackupFiles(_folder, name);
        var image = new byte[DvdSectors * DiscSectors.Size];
        new Random(name.Length).NextBytes(image.AsSpan(0, image.Length / 3));
        File.WriteAllBytes(files.Image, image);
        Complete(files, DvdSectors, DvdIdentity);
        return (await DiscBackupLibrary.FinishAsync(files, redump: null, null, CancellationToken.None)).Files;
    }

    private async Task<DiscBackupFiles> FinishedCd(string name)
    {
        var files = new DiscBackupFiles(_folder, name, IsCd: true);
        var bin = new byte[CdSectors * 2352];
        var random = new Random(3);
        for (var sector = 0; sector < CdSectors; sector++)
        {
            var raw = bin.AsSpan(sector * 2352, 2352);
            raw[15] = 2;
            random.NextBytes(raw.Slice(24, 2048));
            ChdCdEcc.Restore(raw);
        }

        File.WriteAllBytes(files.Image, bin);
        Complete(files, CdSectors, CdIdentity);
        return (await DiscBackupLibrary.FinishAsync(files, redump: null, null, CancellationToken.None)).Files;
    }

    private static void Complete(DiscBackupFiles files, long sectors, string identity)
    {
        var map = DiscCopyMap.New(sectors, identity);
        map.Mark(0, sectors, DiscSectorState.ReadNormally);
        map.Save(files.Map);
    }

    private sealed class Collect(List<double> into) : IProgress<double>
    {
        public void Report(double value) => into.Add(value);
    }
}
