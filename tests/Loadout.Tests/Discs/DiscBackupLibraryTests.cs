using System.Security.Cryptography;
using System.Text;

namespace Loadout.Discs;

public sealed class DiscBackupLibraryTests : IDisposable
{
    private const long Sectors = 4;
    private const string Identity = "SLES-55002 4 0123456789abcdef";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loadout-library-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _content = Content();

    public DiscBackupLibraryTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public async Task AMatchingCopyTakesRedumpsNameAndItsHashesAreKept()
    {
        var files = Copy("Need for Speed ProStreet [SLES-55002]", complete: true);

        var backup = await DiscBackupLibrary.FinishAsync(files, Dat(Sha1(_content)), null, CancellationToken.None);

        Assert.Equal(DiscBackupResult.MatchesRedump, backup.Result);
        Assert.Equal("Need for Speed - ProStreet (Europe, Australia)", backup.Files.Name);
        Assert.True(File.Exists(backup.Files.Image));
        Assert.True(File.Exists(backup.Files.Map));
        Assert.DoesNotContain(files.All, File.Exists);
        var hashes = File.ReadAllLines(backup.Files.Hashes);
        Assert.Equal("Need for Speed - ProStreet (Europe, Australia).iso", hashes[0]);
        Assert.Contains($"SHA-1   {Sha1(_content)}", hashes);
        Assert.Equal(Identity, backup.Identity?.Format());
        Assert.Equal(Sectors * DiscSectors.Size, backup.Bytes);
    }

    [Fact]
    public async Task ACopyThatDoesNotMatchKeepsItsName()
    {
        var files = Copy("Need for Speed ProStreet [SLES-55002]", complete: true);

        var backup = await DiscBackupLibrary.FinishAsync(files, Dat(new string('9', 40)), null, CancellationToken.None);

        Assert.Equal(DiscBackupResult.DoesNotMatchRedump, backup.Result);
        Assert.Equal(files.Name, backup.Files.Name);
    }

    [Fact]
    public async Task WithoutRedumpsListACopyIsCheckedLater()
    {
        var files = Copy("Need for Speed ProStreet [SLES-55002]", complete: true);

        var waiting = await DiscBackupLibrary.FinishAsync(files, redump: null, null, CancellationToken.None);
        Assert.Equal(DiscBackupResult.NotCheckedYet, waiting.Result);
        Assert.Equal(files.Name, waiting.Files.Name);

        var checkedLater = DiscBackupLibrary.CheckLater(waiting, Dat(Sha1(_content)));

        Assert.Equal(DiscBackupResult.MatchesRedump, checkedLater?.Result);
        Assert.Equal("Need for Speed - ProStreet (Europe, Australia)", checkedLater?.Files.Name);
        Assert.Null(DiscBackupLibrary.CheckLater(checkedLater!, Dat(Sha1(_content))));
    }

    [Fact]
    public void TheFolderListsEveryBackupItHolds()
    {
        Copy("Complete [SLES-55002]", complete: true);
        Copy("Half done [SLES-55002]", complete: false);
        File.WriteAllText(Path.Combine(_folder, "Broken.map"), "not a map");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "hello");

        var backups = DiscBackupLibrary.List(_folder);

        Assert.Equal(["Complete [SLES-55002]", "Half done [SLES-55002]"], backups.Select(b => b.Files.Name));
        Assert.Equal(DiscBackupResult.NotCheckedYet, backups[0].Result);
        Assert.Equal(DiscBackupResult.Incomplete, backups[1].Result);
        Assert.Equal(2, backups[1].ReadSectors);
        Assert.Equal(Sectors, backups[1].Sectors);
    }

    [Fact]
    public void AFolderThatIsNotThereHasNoBackups() => Assert.Empty(DiscBackupLibrary.List(Path.Combine(_folder, "missing")));

    [Fact]
    public void TheSameDiscIsFoundByItsNewestCopy()
    {
        var half = Copy("A half [SLES-55002]", complete: false);
        var whole = Copy("B whole [SLES-55002]", complete: true);
        var earlier = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(whole.Image, earlier);
        File.SetLastWriteTimeUtc(half.Image, earlier.AddMinutes(5));
        Assert.True(DiscIdentity.TryParse(Identity, out var identity));

        // Backed up again and stopped: the newer, incomplete copy is the one to resume.
        Assert.Equal("A half [SLES-55002]", DiscBackupLibrary.FindSameDisc(DiscBackupLibrary.List(_folder), identity)?.Files.Name);

        File.SetLastWriteTimeUtc(whole.Image, earlier.AddMinutes(10));
        Assert.Equal("B whole [SLES-55002]", DiscBackupLibrary.FindSameDisc(DiscBackupLibrary.List(_folder), identity)?.Files.Name);

        File.SetLastWriteTimeUtc(half.Image, earlier.AddMinutes(10));
        Assert.Equal("B whole [SLES-55002]", DiscBackupLibrary.FindSameDisc(DiscBackupLibrary.List(_folder), identity)?.Files.Name);
        Assert.Null(DiscBackupLibrary.FindSameDisc(DiscBackupLibrary.List(_folder), identity with { VolumeHash = "ffffffffffffffff" }));
    }

    private DiscBackupFiles Copy(string name, bool complete)
    {
        var files = new DiscBackupFiles(_folder, name);
        File.WriteAllBytes(files.Image, _content);
        var map = DiscCopyMap.New(Sectors, Identity);
        map.Mark(0, complete ? Sectors : 2, DiscSectorState.ReadNormally);
        map.Save(files.Map);
        return files;
    }

    private static RedumpDat Dat(string sha1) => RedumpDatTests.Parse($"""
        <?xml version="1.0"?>
        <datafile>
        	<header><name>Sony - PlayStation 2</name><version>2026-06-15 03-41-38</version></header>
        	<game name="Need for Speed - ProStreet (Europe, Australia)">
        		<category>Games</category>
        		<rom name="Need for Speed - ProStreet (Europe, Australia).iso" size="{Sectors * DiscSectors.Size}" crc="00000000" md5="00000000000000000000000000000000" sha1="{sha1}"/>
        	</game>
        </datafile>
        """);

    private static string Sha1(byte[] data)
    {
        // Redump's list names discs by SHA-1; it is a checksum here, not security.
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(data);
        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    private static byte[] Content()
    {
        var data = new byte[Sectors * DiscSectors.Size];
        Encoding.ASCII.GetBytes("PS2 disc").CopyTo(data, 0);
        data[^1] = 7;
        return data;
    }
}
