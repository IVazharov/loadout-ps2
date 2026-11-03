using System.Globalization;
using Loadout.Library;

namespace Loadout.Core;

public class InstalledGameRowLabelTests
{
    [Theory]
    [InlineData(0x00, "–")]
    [InlineData(0x01, "1")]
    [InlineData(0x05, "1, 3")]
    [InlineData(0x80, "8")]
    [InlineData(0xFF, "1, 2, 3, 4, 5, 6, 7, 8")]
    public void ModesAreTheSetBitsAsOplModeNumbers(int flags, string expected) =>
        Assert.Equal(expected, InstalledGameRow.Modes((byte)flags));

    [Theory]
    [InlineData(0x40, 4, "UDMA 4")]
    [InlineData(0x40, 0, "UDMA 0")]
    [InlineData(0x20, 2, "MDMA 2")]
    [InlineData(0x00, 4, "0x00 / 4")]
    [InlineData(0x41, 3, "0x41 / 3")]
    public void TransferNamesMdmaAndUdmaAndKeepsOtherTypesRaw(int type, int mode, string expected) =>
        Assert.Equal(expected, InstalledGameRow.Transfer((byte)type, (byte)mode));

    [Fact]
    public void SizeIsSectorsInGigabytesWithTwoDecimals() =>
        Assert.Equal(4.08.ToString("0.00", CultureInfo.CurrentCulture) + " GB", InstalledGameRow.Size(2140832));

    [Fact]
    public void AnIncompleteInstallHasADashForItsSize() => Assert.Equal("–", InstalledGameRow.Size(0));

    [Fact]
    public void TheRowShowsItsOwnFieldsThroughTheLabels()
    {
        var row = new InstalledGameRow("PP.SLUS-20745.HDL.Gnome_2", "Gnome 2", "SLUS_207.45", 0x05, 0x40, 4, 0x14, 2140832);

        Assert.Equal("1, 3", row.ModesLabel);
        Assert.Equal("UDMA 4", row.TransferLabel);
        Assert.Equal(InstalledGameRow.Size(2140832), row.SizeLabel);
    }
}
