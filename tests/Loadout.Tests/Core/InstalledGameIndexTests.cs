
namespace Loadout.Core;

public class InstalledGameIndexTests
{
    [Fact]
    public void DuplicateDiscIdIsRefusedAndTheExistingRecordStays()
    {
        var index = new InstalledGameIndex();

        Assert.Equal(AddGameResult.Added, index.TryAdd("SLES_538.86", "first.iso"));
        Assert.Equal(AddGameResult.Duplicate, index.TryAdd("SLES-53886", "second.iso"));
        Assert.Equal(AddGameResult.Duplicate, index.TryAdd("SLES_538.86;1", "third.bin"));

        Assert.True(index.TryGet("SLES-53886", out var record));
        Assert.NotNull(record);
        Assert.Equal("SLES-53886", record.DiscId.Canonical);
        Assert.Equal("first.iso", record.SourceFileName);
        Assert.Single(index.Games);
    }

    [Fact]
    public void SameDigitsInAnotherRegionAreNotADuplicate()
    {
        var index = new InstalledGameIndex();

        Assert.Equal(AddGameResult.Added, index.TryAdd("SLES_538.86", "pal.iso"));
        Assert.Equal(AddGameResult.Added, index.TryAdd("SLUS_538.86", "ntsc.iso"));

        Assert.Equal(2, index.Games.Count);
        Assert.True(index.TryGet("SLUS-53886", out var ntsc));
        Assert.Equal("ntsc.iso", ntsc!.SourceFileName);
    }

    [Fact]
    public void InvalidDiscIdIsNotStored()
    {
        var index = new InstalledGameIndex();

        Assert.Equal(AddGameResult.InvalidDiscId, index.TryAdd("filename-only.iso", "filename-only.iso"));
        Assert.Equal(AddGameResult.InvalidDiscId, index.TryAdd(null, "missing.iso"));
        Assert.Empty(index.Games);
    }

    [Fact]
    public void FileNameIsNotTheDuplicateKey()
    {
        var index = new InstalledGameIndex();

        Assert.Equal(AddGameResult.Added, index.TryAdd("SLES_538.86", "shared-name.iso"));
        Assert.Equal(AddGameResult.Added, index.TryAdd("SCES_538.86", "shared-name.iso"));

        Assert.Equal(2, index.Games.Count);
    }
}
