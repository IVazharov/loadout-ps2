using System.Buffers.Binary;
using System.Globalization;

namespace Loadout.Discs;

public class DriveSpeedsTests
{
    [Fact]
    public void TheDrivesCurveIsReadFromItsAnswer()
    {
        // What the Slimtype eTAU108 answered for NBA Street: one stretch, 10x at the start to 24x at the end.
        var answer = new byte[8 + 16];
        BinaryPrimitives.WriteInt32BigEndian(answer, 20);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(12), 1764);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(16), 218_349);
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(20), 4234);

        var stretches = DriveSpeeds.ParsePerformance(answer);
        var speeds = new DriveSpeeds(DiscMedia.Cd, 4234, stretches);

        Assert.Equal([new DriveSpeedStretch(0, 1764, 218_349, 4234)], stretches);
        Assert.Equal("10x–24x", speeds.Range(CultureInfo.InvariantCulture));
        Assert.Equal(1764, speeds.KbPerSecondAt(0)!.Value, 1);
        Assert.Equal(4234, speeds.KbPerSecondAt(218_349)!.Value, 1);
        Assert.InRange(speeds.KbPerSecondAt(109_000)!.Value, 3000, 3200);
        Assert.Null(speeds.KbPerSecondAt(300_000));
    }

    [Fact]
    public void ADriveThatOnlyGivesItsMaximumShowsThat()
    {
        var speeds = new DriveSpeeds(DiscMedia.Dvd, 11_080, []);

        Assert.Equal("8x", speeds.Range(CultureInfo.InvariantCulture));
        Assert.Null(speeds.KbPerSecondAt(0));
    }

    [Fact]
    public void AShortOrEmptyAnswerHasNoCurve()
    {
        Assert.Empty(DriveSpeeds.ParsePerformance(new byte[4]));
        Assert.Empty(DriveSpeeds.ParsePerformance(new byte[8]));
    }
}
