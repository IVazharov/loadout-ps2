namespace Loadout.Core;

public class InstallBatchMeterTests
{
    [Fact]
    public void TheSpeedStaysUnknownUntilTheClockHasRunForTwoSeconds()
    {
        var twoMegabytes = 2 * InstallBatchMeter.BytesPerMegabyte;

        Assert.Null(InstallBatchMeter.SettledMegabytesPerSecond(twoMegabytes, TimeSpan.FromTicks(5)));
        Assert.Null(InstallBatchMeter.SettledMegabytesPerSecond(twoMegabytes, TimeSpan.FromSeconds(1.9)));
        Assert.Equal(1, InstallBatchMeter.SettledMegabytesPerSecond(twoMegabytes, TimeSpan.FromSeconds(2)));
        Assert.Equal(6.5, InstallBatchMeter.SettledMegabytesPerSecond(650 * InstallBatchMeter.BytesPerMegabyte, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void TotalProgressIsTheSumOfTheBatchInMegabytes()
    {
        var megabyte = (long)InstallBatchMeter.BytesPerMegabyte;
        var meter = InstallBatchMeter.Measure(
            completedBytes: 10 * megabyte,
            currentConfirmedBytes: (long)(2.5 * megabyte),
            batchTotalBytes: 30 * megabyte,
            currentBytesPerSecond: megabyte,
            elapsed: TimeSpan.FromSeconds(12.5));

        Assert.Equal(12.5, meter.Confirmed, precision: 3);
        Assert.Equal(30.0, meter.Total, precision: 3);
        Assert.Equal(1.0, meter.AveragePerSecond, precision: 3);
        Assert.Equal(1.0, meter.CurrentPerSecond, precision: 3);
    }
}
