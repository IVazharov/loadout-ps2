using System.Text;

namespace Loadout.Discs;

public sealed class CdBackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loadout-cd-" + Guid.NewGuid().ToString("N"));

    public CdBackupTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public async Task ACdIsCopiedRawAs2352ByteSectors()
    {
        var clock = new FakeDiscClock();
        using var drive = new FakeDiscDrive(1000, clock) { Media = DiscMedia.Cd, CopySectorSize = 2352 };
        drive.Weak[700] = 1;
        using var image = new MemoryStream();
        var map = DiscCopyMap.New(1000, "test");
        var copier = new DiscCopier(drive, image, map, _ => { }, clock: clock);

        Assert.Equal(DiscCopyOutcome.Complete, await copier.RunAsync(CancellationToken.None));

        Assert.Equal(1000L * 2352, image.Length);
        Assert.Equal(drive.Expected(0, 1000), image.ToArray());
    }

    [Fact]
    public void ACdsCueIsRedumpsByteForByte()
    {
        var files = new DiscBackupFiles(_folder, "NBA Street (Europe) (En,Fr)", IsCd: true);
        var cue = Encoding.ASCII.GetBytes(files.CueText(2));

        // Redump's PS2 list gives NBA Street's cue as 93 bytes with CRC-32 dbfdef6d.
        Assert.Equal(93, cue.Length);
        Assert.Equal(0xdbfdef6du, Crc32Ieee.Finish(Crc32Ieee.Append(Crc32Ieee.Start, cue)));
        Assert.EndsWith("NBA Street (Europe) (En,Fr).bin", files.Image, StringComparison.Ordinal);
        Assert.Contains(files.Cue, files.All);
    }

    [Fact]
    public void RenamingACdCopyRewritesItsCue()
    {
        var files = new DiscBackupFiles(_folder, "NBA Street [SLES-50219]", IsCd: true);
        File.WriteAllText(files.Image, "bin");
        files.WriteCue(2);

        var renamed = files.RenameTo("NBA Street (Europe) (En,Fr)");

        Assert.True(renamed.IsCd);
        Assert.Equal("bin", File.ReadAllText(renamed.Image));
        Assert.Equal(renamed.CueText(2), File.ReadAllText(renamed.Cue));
        Assert.False(File.Exists(files.Cue));
    }

    [Fact]
    public void ACdNameIsTakenByADvdCopyOfThatName()
    {
        File.WriteAllText(Path.Combine(_folder, "Game.iso"), "x");

        Assert.Equal("Game (2)", DiscBackupFiles.Unused(_folder, "Game", isCd: true).Name);
    }

    [Fact]
    public async Task AFinishedCdCopyGetsItsCueAndIsListedAsACd()
    {
        var files = new DiscBackupFiles(_folder, "Hoops [SLES-50219]", IsCd: true);
        var bin = new byte[20 * 2352];
        bin[(16 * 2352) + 15] = 2;
        File.WriteAllBytes(files.Image, bin);
        var map = DiscCopyMap.New(20, "SLES-50219 20 0123456789abcdef");
        map.Mark(0, 20, DiscSectorState.ReadNormally);
        map.Save(files.Map);

        var backup = await DiscBackupLibrary.FinishAsync(files, redump: null, null, CancellationToken.None);

        Assert.True(backup.Files.IsCd);
        Assert.Equal(files.CueText(2), File.ReadAllText(files.Cue));
        Assert.Equal(20L * 2352, backup.Bytes);
        var listed = Assert.Single(DiscBackupLibrary.List(_folder));
        Assert.True(listed.Files.IsCd);
        Assert.Equal(DiscBackupResult.NotCheckedYet, listed.Result);
    }
}
