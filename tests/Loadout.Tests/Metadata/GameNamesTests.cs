using Loadout.Discs;

namespace Loadout.Metadata;

public sealed class GameNamesTests
{
    [Theory]
    [InlineData("King of Fighters XI, The (Europe)", "The King of Fighters XI")]
    [InlineData("King of Fighters, The - Maximum Impact 2 (Europe, Australia) (En,Fr,De,Es,It)", "The King of Fighters - Maximum Impact 2")]
    [InlineData("Lord of the Rings, The - The Two Towers (USA)", "The Lord of the Rings - The Two Towers")]
    [InlineData("Odyssee d'Abe, L' (France)", "L'Odyssee d'Abe")]
    [InlineData("Monde de Narnia, Le - Chapitre 1 (France)", "Le Monde de Narnia - Chapitre 1")]
    [InlineData("Need for Speed ProStreet [SLES-55002]", "Need for Speed ProStreet")]
    [InlineData("FIFA Street 2 (Europe) (2)", "FIFA Street 2")]
    [InlineData("Xenosaga Episode III - Also sprach Zarathustra (USA) (Disc 2)", "Xenosaga Episode III - Also sprach Zarathustra (Disc 2)")]
    [InlineData("Dark_Alliance_II", "Dark Alliance II")]
    [InlineData("  Shrek 2  ", "Shrek 2")]
    [InlineData("Theme Park, A Tale", "Theme Park, A Tale")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ANameIsTidiedIntoATitle(string? name, string title) => Assert.Equal(title, GameNames.Tidy(name));

    [Theory]
    [InlineData("1", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    [InlineData("KF", true)]
    [InlineData("SLES_544.37", true)]
    [InlineData("SLES-54437", true)]
    [InlineData("sles 544.37", true)]
    [InlineData("SLUS_200.01", true)]
    [InlineData("The King of Fighters XI", false)]
    [InlineData("KOF", false)]
    [InlineData("DARK_ALLIANCE", false)]
    [InlineData("SLES_538.86 USA", false)]
    public void ALabelThatIsNoNameIsTold(string? title, bool junk) => Assert.Equal(junk, GameNames.IsJunk(title));

    [Fact]
    public void TheFirstCandidateThatIsANameWins()
    {
        Assert.Equal("The King of Fighters XI", GameNames.FirstName("The King of Fighters XI", "1"));
        Assert.Equal("DARK_ALLIANCE", GameNames.FirstName("SLUS-20035", " DARK_ALLIANCE "));
        Assert.Null(GameNames.FirstName(null, "SLES-54437", "1"));
    }

    [Fact]
    public void RedumpNamesADiscOfItsSizeWhenAllDiscsOfThatSizeAreOneGame()
    {
        // Two printings of Test Racer have the same size; Hoops is a CD, so its bin's size counts.
        var list = RedumpDatTests.Parse(RedumpDatTests.Fixture);

        Assert.Equal("Test Racer", GameNames.FromRedump(list, 8192));
        Assert.Equal("Hoops", GameNames.FromRedump(list, 2352000));
        Assert.Null(GameNames.FromRedump(list, 93));
        Assert.Null(GameNames.FromRedump(list, 12345));
        Assert.Null(GameNames.FromRedump(null, 8192));
    }

    [Fact]
    public void TwoGamesOfOneSizeGiveNoName()
    {
        var list = RedumpDatTests.Parse("""
            <datafile>
            	<game name="Metal Gear Solid 2 - Sons of Liberty (Europe)">
            		<rom name="a.iso" size="4096" crc="0" md5="0" sha1="1111111111111111111111111111111111111111"/>
            	</game>
            	<game name="Metal Gear Solid 3 - Snake Eater (Europe)">
            		<rom name="b.iso" size="4096" crc="0" md5="0" sha1="2222222222222222222222222222222222222222"/>
            	</game>
            </datafile>
            """);

        Assert.Equal(2, list.OfSize(4096).Count);
        Assert.Null(GameNames.FromRedump(list, 4096));
    }
}
