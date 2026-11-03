
namespace Loadout.Core;

public class DiscIdTests
{
    [Theory]
    [InlineData("SLES_538.86")]
    [InlineData("SLES-53886")]
    [InlineData("SLES_538.86;1")]
    [InlineData("sles_538.86")]
    [InlineData("  SLES-53886  ")]
    public void KnownFormsShareOneCanonicalId(string text)
    {
        Assert.True(DiscId.TryParse(text, out var discId));
        Assert.Equal("SLES", discId.Region);
        Assert.Equal("53886", discId.Digits);
        Assert.Equal("SLES-53886", discId.Canonical);
        Assert.Equal("SLES_538.86", discId.OplFileStem);
        Assert.Equal("SLES_538.86", discId.StartupFileName);
    }

    [Fact]
    public void InstallStartupIsElevenCharactersAndStopsBeforeTheSemicolon()
    {
        Assert.True(DiscId.TryParse("SLUS-20745", out var gnome));
        Assert.Equal("SLUS_207.45", gnome.StartupFileName);
        Assert.Equal(11, gnome.StartupFileName.Length);
        Assert.Equal(gnome.OplFileStem, gnome.StartupFileName);
        Assert.DoesNotContain(';', gnome.StartupFileName);
    }

    [Fact]
    public void DifferentRegionIsNotTheSameGame()
    {
        Assert.False(DiscId.AreSameGame("SLES_538.86", "SLUS_538.86"));
        Assert.False(DiscId.AreSameGame("SLES-53886", "SLUS-53886"));
        Assert.True(DiscId.AreSameGame("SLUS_538.86", "slus-53886"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SLES_538.8")]
    [InlineData("SLES_53886")]
    [InlineData("SLES-538860")]
    [InlineData("SLES-5388")]
    [InlineData("SLES_538.86;2")]
    [InlineData("SLES-53886;1")]
    [InlineData("SLE_538.86")]
    [InlineData("SLES 53886")]
    [InlineData("1234_538.86")]
    [InlineData("SLES_53A.86")]
    [InlineData("SLES-5388A")]
    public void InvalidTextDoesNotParse(string? text)
    {
        Assert.False(DiscId.TryParse(text, out _));
    }

    [Fact]
    public void UnparseableTextIsNotADuplicate()
    {
        Assert.False(DiscId.AreSameGame(null, null));
        Assert.False(DiscId.AreSameGame("not-a-disc", "not-a-disc"));
        Assert.False(DiscId.AreSameGame("SLES_538.86", "not-a-disc"));
    }
}
