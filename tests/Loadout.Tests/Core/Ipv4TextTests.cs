namespace Loadout.Core;

public class Ipv4TextTests
{
    [Theory]
    [InlineData("10.0.0.55", true)]
    [InlineData(" 192.168.1.5 ", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("10.1", false)]
    [InlineData("10.0.0.300", false)]
    [InlineData("10.0.0.", false)]
    [InlineData("10.0.0.-1", false)]
    [InlineData("10.0.0.0005", false)]
    [InlineData("10.0.0 .5", false)]
    [InlineData("ps2.local", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyFourPartsOfZeroTo255AreAnAddress(string? text, bool valid)
    {
        Assert.Equal(valid, Ipv4Text.IsValid(text));
    }

    [Fact]
    public void AParsedAddressGivesItsFourParts()
    {
        Assert.True(Ipv4Text.TryParse("192.168.001.5", out var octets));
        Assert.Equal([192, 168, 1, 5], octets);
        Assert.Equal("192.168.1.5", Ipv4Text.Format(octets));
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("30", "30")]
    [InlineData("300", "255")]
    [InlineData("2555", "255")]
    [InlineData("999", "255")]
    [InlineData("007", "7")]
    [InlineData("00", "0")]
    [InlineData("1a2", "12")]
    [InlineData("abc", "")]
    [InlineData("", "")]
    public void APartAsTypedStaysBetweenZeroAnd255(string typed, string expected)
    {
        Assert.Equal(expected, Ipv4Text.Octet(typed));
    }

    [Theory]
    [InlineData("0", -1, "0")]
    [InlineData("255", 1, "255")]
    [InlineData("250", 10, "255")]
    [InlineData("5", -10, "0")]
    [InlineData("", 1, "1")]
    [InlineData("41", 1, "42")]
    public void SteppingStopsAtZeroAnd255(string octet, int delta, string expected)
    {
        Assert.Equal(expected, Ipv4Text.Step(octet, delta));
    }
}
