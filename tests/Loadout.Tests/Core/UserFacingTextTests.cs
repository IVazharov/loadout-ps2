using Loadout.Images;

namespace Loadout.Core;

public class UserFacingTextTests
{
    [Fact]
    public void KnownFailuresUseOneSentenceAndDropAStack()
    {
        Assert.Equal(UserFacingText.UnsupportedMultiTrack, UserFacingText.Describe(ImageLimits.MultiTrack));

        var log = new OperationLog();
        log.Add("System.IO.IOException: Unsupported multi-track image.\n   at Loadout.Images.Scan.Run()\n   at Scan.Run()");
        var line = Assert.Single(log.Recent);
        Assert.Equal(UserFacingText.UnsupportedMultiTrack, line);
        Assert.DoesNotContain("at ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFailureKeepsItsFirstLineUpTo300Characters()
    {
        Assert.Equal("The operation finished with no detail.", UserFacingText.Describe("  "));
        Assert.Equal("Disk full.", UserFacingText.Describe("Disk full.\r\nsecond line"));
        Assert.Equal(300, UserFacingText.Describe(new string('x', 400)).Length);
    }
}
