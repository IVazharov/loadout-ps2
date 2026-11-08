using Loadout.Bay;

namespace Loadout.Updates;

public class BayVersionsTests
{
    [Theory]
    [InlineData(8u, "0.10.0-dev.2", "0.10.0-dev.3", 8u, true)]
    [InlineData(8u, "0.9.3", "0.10.0", 8u, true)]
    [InlineData(7u, "0.9.3", "0.10.0", 8u, true)]
    [InlineData(7u, "9.9.9", "0.10.0", 8u, true)]
    [InlineData(8u, "0.10.0", "0.10.0-dev.3", 8u, false)]
    [InlineData(8u, "0.10.1", "0.10.0", 8u, false)]
    [InlineData(9u, "0.9.0", "0.10.0", 8u, false)]
    [InlineData(8u, "0.10.0", "0.10.0", 8u, false)]
    [InlineData(8u, "unknown", "0.10.0", 8u, false)]
    public void OnlyAnOlderProtocolOrALowerVersionIsOlder(uint bayProtocol, string bayVersion, string loadoutVersion, uint loadoutProtocol, bool older)
    {
        var bay = new BayHello(bayProtocol, bayVersion, HasHardDisk: true);

        Assert.Equal(older, BayVersions.IsOlder(bay, loadoutVersion, loadoutProtocol));
    }
}
