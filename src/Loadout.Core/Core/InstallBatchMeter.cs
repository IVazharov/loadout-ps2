namespace Loadout.Core;

public readonly record struct BatchMegabytes(
    double Confirmed,
    double Total,
    double Percent,
    double AveragePerSecond,
    double CurrentPerSecond,
    TimeSpan? Eta);

/// <summary>
/// Batch progress in megabytes. Confirmed megabytes are the sum of finished games
/// plus the game currently transferring.
/// </summary>
public static class InstallBatchMeter
{
    public const double BytesPerMegabyte = 1024d * 1024d;

    private const double RateSettleSeconds = 2;

    /// <summary>
    /// The average speed so far, or null in the first seconds: the clock starts at the first
    /// report, which already has data behind it, so an early figure would be wildly high.
    /// </summary>
    public static double? SettledMegabytesPerSecond(double bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds >= RateSettleSeconds ? bytes / BytesPerMegabyte / elapsed.TotalSeconds : null;

    public static BatchMegabytes Measure(
        long completedBytes,
        long currentConfirmedBytes,
        long batchTotalBytes,
        double currentBytesPerSecond,
        TimeSpan elapsed)
    {
        if (completedBytes < 0)
        {
            completedBytes = 0;
        }

        if (currentConfirmedBytes < 0)
        {
            currentConfirmedBytes = 0;
        }

        if (batchTotalBytes < 0)
        {
            batchTotalBytes = 0;
        }

        var confirmed = completedBytes + currentConfirmedBytes;
        if (batchTotalBytes > 0 && confirmed > batchTotalBytes)
        {
            confirmed = batchTotalBytes;
        }

        var percent = batchTotalBytes == 0 ? 0d : confirmed * 100d / batchTotalBytes;
        var seconds = elapsed.TotalSeconds;
        var average = seconds > 0 ? confirmed / seconds : 0d;
        TimeSpan? eta = average > 0 && confirmed < batchTotalBytes
            ? TimeSpan.FromSeconds((batchTotalBytes - confirmed) / average)
            : TimeSpan.Zero;
        return new BatchMegabytes(
            confirmed / BytesPerMegabyte,
            batchTotalBytes / BytesPerMegabyte,
            percent,
            average / BytesPerMegabyte,
            currentBytesPerSecond / BytesPerMegabyte,
            eta);
    }
}

public sealed record InstallBatchUpdate(
    string CurrentName,
    double CurrentMegabytes,
    double CurrentTotalMegabytes,
    double CurrentPercent,
    double TotalMegabytes,
    double BatchMegabytes,
    double TotalPercent,
    double AverageMegabytesPerSecond,
    double CurrentMegabytesPerSecond,
    TimeSpan? Eta);
