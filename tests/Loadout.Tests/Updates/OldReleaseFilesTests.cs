namespace Loadout.Updates;

public sealed class OldReleaseFilesTests : IDisposable
{
    // third_party/Puff/README.md exactly as the Windows zips 0.9.2 to 0.10.0 held it.
    private const string ShippedPuffReadme =
        "# puff\r\n\r\nLoadout's deflate decoder, `src/Loadout.Core/Images/Chd/ChdInflate.cs`, is written after puff, Mark Adler's small inflate implementation, and altered from it: it is C#, and it allocates nothing per hunk.\r\n\r\n- Source: `contrib/puff` in [zlib](https://github.com/madler/zlib).\r\n- Licence: the zlib-style notice in `LICENSE`.\r\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("loadout-old-files-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AFileAsAnOldZipShippedItGoesWithTheFoldersItLeavesEmpty()
    {
        var puff = Path.Combine(_folder, "third_party", "Puff", "README.md");
        Directory.CreateDirectory(Path.GetDirectoryName(puff)!);
        File.WriteAllText(puff, ShippedPuffReadme);

        var removed = OldReleaseFiles.Remove(_folder);

        Assert.Equal(["third_party/Puff/README.md"], removed);
        Assert.False(Directory.Exists(Path.Combine(_folder, "third_party")));
    }

    [Fact]
    public void AFileOfTheSameNameWithOtherContentsStays()
    {
        // Someone may have unzipped Loadout into a folder of other things, where a LICENSE belongs to something else.
        var license = Path.Combine(_folder, "LICENSE");
        File.WriteAllText(license, "Another program's licence.");
        var own = Path.Combine(_folder, "third_party", "Puff", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "mine");
        File.WriteAllText(Path.Combine(_folder, "third_party", "Puff", "README.md"), ShippedPuffReadme);

        var removed = OldReleaseFiles.Remove(_folder);

        Assert.Equal(["third_party/Puff/README.md"], removed);
        Assert.True(File.Exists(license));
        Assert.True(File.Exists(own));
    }

    [Fact]
    public void EveryOldZipsLicenceFileIsListedButNotTheProgramOrItsReadme()
    {
        Assert.Equal(44, OldReleaseFiles.Licences.Count);
        Assert.Contains("LICENSE", OldReleaseFiles.Licences.Keys);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", OldReleaseFiles.Licences.Keys);
        Assert.DoesNotContain("README.md", OldReleaseFiles.Licences.Keys);
        Assert.DoesNotContain("LoadoutPS2.exe", OldReleaseFiles.Licences.Keys);
    }
}
