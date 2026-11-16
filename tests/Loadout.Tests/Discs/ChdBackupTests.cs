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

        var pending = Assert.Single(DiscBackupLibrary.PendingCompression(_folder));

        Assert.Equal(marked.Name, pending.Files.Name);
        Assert.Empty(DiscBackupLibrary.PendingCompression(Path.Combine(_folder, "missing")));
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
