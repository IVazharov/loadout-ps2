using System.IO.Compression;
using System.Text;

namespace Loadout.Discs;

public class RedumpDatTests
{
    // Made-up discs in the form of Redump's PS2 list (Logiqx XML with its DOCTYPE).
    internal const string Fixture = """
        <?xml version="1.0"?>
        <!DOCTYPE datafile PUBLIC "-//Logiqx//DTD ROM Management Datafile//EN" "http://www.logiqx.com/Dats/datafile.dtd">
        <datafile>
        	<header>
        		<name>Sony - PlayStation 2</name>
        		<description>Sony - PlayStation 2 - Discs (4) (2026-01-02 03-04-05)</description>
        		<version>2026-01-02 03-04-05</version>
        		<date>2026-01-02 03-04-05</date>
        		<author>redump.org</author>
        	</header>
        	<game name="Test Racer (Europe, Australia)">
        		<category>Games</category>
        		<description>Test Racer (Europe, Australia)</description>
        		<rom name="Test Racer (Europe, Australia).iso" size="8192" crc="0badf00d" md5="00112233445566778899aabbccddeeff" sha1="1111111111111111111111111111111111111111"/>
        	</game>
        	<game name="Test Racer (Europe) (En,Nl)">
        		<category>Games</category>
        		<rom name="Test Racer (Europe) (En,Nl).iso" size="8192" crc="0badf00e" md5="00112233445566778899aabbccddeef0" sha1="2222222222222222222222222222222222222222"/>
        	</game>
        	<game name="Hoops (Europe) (En,Fr)">
        		<category>Games</category>
        		<description>Hoops (Europe) (En,Fr)</description>
        		<rom name="Hoops (Europe) (En,Fr).cue" size="93" crc="dbfdef6d" md5="c2214e7335a6569801d7467fe10f98bf" sha1="3333333333333333333333333333333333333333"/>
        		<rom name="Hoops (Europe) (En,Fr).bin" size="2352000" crc="e2aca172" md5="ecc443a925fc21d2d174ea4d57e159ac" sha1="4444444444444444444444444444444444444444"/>
        	</game>
        	<game name="Broken Entry (USA)">
        		<category>Demos</category>
        		<rom name="Broken Entry (USA).iso" size="4096" crc="00000000"/>
        	</game>
        </datafile>
        """;

    [Fact]
    public void TheListIsReadWithEveryDiscAndItsFiles()
    {
        var dat = Parse(Fixture);

        Assert.Equal("2026-01-02 03-04-05", dat.Version);
        Assert.Equal(["Test Racer (Europe, Australia)", "Test Racer (Europe) (En,Nl)", "Hoops (Europe) (En,Fr)", "Broken Entry (USA)"], dat.Games.Select(g => g.Name));
        Assert.Single(dat.Games[0].Roms);
        Assert.Equal(new RedumpRom("Test Racer (Europe) (En,Nl).iso", 8192, "0badf00e", "00112233445566778899aabbccddeef0", "2222222222222222222222222222222222222222"), Assert.Single(dat.Games[1].Roms));
        Assert.Equal(2, dat.Games[2].Roms.Count);
        Assert.Empty(dat.Games[3].Roms);
        Assert.Equal("Demos", dat.Games[3].Category);
    }

    [Fact]
    public void ACopyWithTheSameSizeAndSha1Matches()
    {
        var check = Parse(Fixture).Check(8192, "2222222222222222222222222222222222222222");

        Assert.Equal(RedumpVerdict.Matches, check.Verdict);
        Assert.Equal("Test Racer (Europe) (En,Nl)", check.Game?.Name);
    }

    [Fact]
    public void UppercaseHashesMatchToo()
    {
        Assert.Equal(RedumpVerdict.Matches, Parse(Fixture).Check(8192, "1111111111111111111111111111111111111111".ToUpperInvariant()).Verdict);
    }

    [Fact]
    public void ACopyOfAListedSizeWithOtherContentsDoesNotMatch()
    {
        var check = Parse(Fixture).Check(8192, "9999999999999999999999999999999999999999");

        Assert.Equal(RedumpVerdict.DoesNotMatch, check.Verdict);
        Assert.Null(check.Game);
        Assert.Equal(["Test Racer (Europe, Australia)", "Test Racer (Europe) (En,Nl)"], check.SameSize.Select(g => g.Name));
    }

    [Fact]
    public void ACopyOfASizeRedumpDoesNotListIsNotInRedump()
    {
        var check = Parse(Fixture).Check(12_288, "9999999999999999999999999999999999999999");

        Assert.Equal(RedumpVerdict.NotInRedump, check.Verdict);
        Assert.Empty(check.SameSize);
    }

    [Fact]
    public void ACdIsMatchedByItsBinNotItsCue()
    {
        var dat = Parse(Fixture);

        Assert.Equal(RedumpVerdict.Matches, dat.Check(2_352_000, "4444444444444444444444444444444444444444").Verdict);
        Assert.Equal(RedumpVerdict.NotInRedump, dat.Check(93, "3333333333333333333333333333333333333333").Verdict);
    }

    [Fact]
    public void TheListIsReadFromRedumpsZip()
    {
        using var zip = Zip(("Sony - PlayStation 2 - Datfile (4) (2026-01-02 03-04-05).dat", Fixture));

        Assert.Equal(4, RedumpDat.ReadZip(zip).Games.Count);
    }

    [Fact]
    public void AZipWithoutAListIsRefused()
    {
        using var zip = Zip(("readme.txt", "hello"));

        Assert.Throws<InvalidDataException>(() => RedumpDat.ReadZip(zip));
    }

    [Fact]
    public void NotAZipIsRefused()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("<html>Service unavailable</html>"));

        Assert.Throws<InvalidDataException>(() => RedumpDat.ReadZip(stream));
    }

    [Theory]
    [InlineData("<html><body>Service unavailable</body></html>")]
    [InlineData("<datafile><header><version>1</version></header></datafile>")]
    [InlineData("<datafile><game name=\"x\">")]
    [InlineData("not xml at all")]
    public void SomethingElseIsNotTheList(string text) => Assert.Throws<InvalidDataException>(() => Parse(text));

    internal static RedumpDat Parse(string xml) => RedumpDat.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    internal static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
