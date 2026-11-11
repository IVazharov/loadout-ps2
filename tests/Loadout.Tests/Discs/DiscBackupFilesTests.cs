using Loadout.Core;

namespace Loadout.Discs;

public sealed class DiscBackupFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loadout-backups-" + Guid.NewGuid().ToString("N"));

    public DiscBackupFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TempDirectory.Delete(_folder);

    [Fact]
    public void AMatchingCopyTakesRedumpsName()
    {
        var game = new RedumpGame("Need for Speed - ProStreet (Europe, Australia)", "Games", []);

        Assert.Equal("Need for Speed - ProStreet (Europe, Australia)", DiscBackupFiles.NameFor(game));
    }

    [Fact]
    public void AnyOtherCopyIsNamedByTitleAndDiscId()
    {
        Assert.True(DiscId.TryParse("SLES-55002", out var id));

        Assert.Equal("Need for Speed ProStreet [SLES-55002]", DiscBackupFiles.NameFor("Need for Speed ProStreet", id));
        Assert.Equal("Unknown game [SLES-55002]", DiscBackupFiles.NameFor(" ", id));
        Assert.Equal("Unknown game", DiscBackupFiles.NameFor(null, null));
    }

    [Theory]
    [InlineData("Star Wars: Battlefront II", "Star Wars Battlefront II")]
    [InlineData("What? Who/Why *now* <here> | \"there\"", "What Who Why now here 'there'")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.backup", "_nul.backup")]
    [InlineData("???", "Disc")]
    [InlineData("Tab\there", "Tab here")]
    public void NamesAreMadeSafeForWindows(string name, string safe) => Assert.Equal(safe, DiscBackupFiles.SafeName(name));

    [Fact]
    public void ALongNameIsCut()
    {
        Assert.Equal(120, DiscBackupFiles.SafeName(new string('a', 300)).Length);
    }

    [Fact]
    public void ATakenNameGetsANumber()
    {
        File.WriteAllText(Path.Combine(_folder, "Game.iso"), "x");
        File.WriteAllText(Path.Combine(_folder, "Game (2).map"), "x");

        Assert.Equal("Game (3)", DiscBackupFiles.Unused(_folder, "Game").Name);
        Assert.Equal("Other", DiscBackupFiles.Unused(_folder, "Other").Name);
    }

    [Fact]
    public void RenamingMovesEveryFileThatExists()
    {
        var files = new DiscBackupFiles(_folder, "Need for Speed ProStreet [SLES-55002]");
        File.WriteAllText(files.Image, "iso");
        File.WriteAllText(files.Map, "map");
        File.WriteAllText(files.Report, "report");

        var renamed = files.RenameTo("Need for Speed - ProStreet (Europe, Australia)");

        Assert.Equal("Need for Speed - ProStreet (Europe, Australia)", renamed.Name);
        Assert.Equal("iso", File.ReadAllText(renamed.Image));
        Assert.Equal("map", File.ReadAllText(renamed.Map));
        Assert.Equal("report", File.ReadAllText(renamed.Report));
        Assert.False(File.Exists(renamed.Hashes));
        Assert.DoesNotContain(files.All, File.Exists);
    }

    [Fact]
    public void RenamingToATakenNameAddsANumber()
    {
        var taken = new DiscBackupFiles(_folder, "Game (Europe)");
        File.WriteAllText(taken.Image, "first");
        var files = new DiscBackupFiles(_folder, "Game [SLES-12345]");
        File.WriteAllText(files.Image, "second");

        var renamed = files.RenameTo("Game (Europe)");

        Assert.Equal("Game (Europe) (2)", renamed.Name);
        Assert.Equal("first", File.ReadAllText(taken.Image));
        Assert.Equal("second", File.ReadAllText(renamed.Image));
    }
}
