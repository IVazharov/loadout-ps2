using Loadout.Core;

namespace Loadout.Vmc;

public sealed class OplVmcGroupsTests
{
    [Fact]
    public void ATitleInNoGroupGetsACardOfItsOwn()
    {
        Assert.Equal("SLUS_202.97_0", OplVmcGroups.CardName(Id("SLUS-20297")));
        Assert.Equal("SLUS_202.97_1", OplVmcGroups.CardName(Id("SLUS-20297"), slot: 1));
    }

    [Fact]
    public void ATitleInAGroupSharesTheGroupsCard()
    {
        Assert.Equal("XEBP_100.01_0", OplVmcGroups.CardName(Id("SCES-53286")));
        Assert.Equal("XEBP_100.01_0", OplVmcGroups.CardName(Id("SCUS-97429")));
    }

    [Fact]
    public void ATitleInTwoGroupsBelongsToTheFirst()
    {
        // SLPM_625.25 is listed under XEBP_100.71 and again under XEBP_100.72.
        Assert.Equal("XEBP_100.71", OplVmcGroups.GroupOf("SLPM_625.25"));
    }

    [Fact]
    public void OnlyOplsTwoSlotsHaveNames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OplVmcGroups.CardName(Id("SLUS-20297"), slot: 2));
    }

    private static DiscId Id(string text) => DiscId.TryParse(text, out var id) ? id : throw new ArgumentException(text);
}
