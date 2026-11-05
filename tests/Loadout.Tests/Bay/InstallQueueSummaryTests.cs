using System.Globalization;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class InstallQueueSummaryTests : IDisposable
{
    // 731712 sectors of 2048 bytes is 1.40 GB, and 37486592 of 512 is 17.9 GB.
    private const uint GameSectors = 731712;
    private static readonly BayFreeSpace Roomy = new(TotalSectors: 83886080, FreeSectors: 37486592);
    private static readonly BayFreeSpace Tight = new(TotalSectors: 83886080, FreeSectors: 4000000);

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public InstallQueueSummaryTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Fact]
    public void AGameShowsItsSizeOnThePs2AndNothingWhileUnknown()
    {
        Assert.Equal("1.40 GB", Game(QueueItemStatus.Ready).SizeLabel);
        Assert.Equal(1498546176, Game(QueueItemStatus.Ready).InstallBytes);
        Assert.Equal("–", (Game(QueueItemStatus.NeedsConversion) with { Layer0Sectors = 0 }).SizeLabel);
    }

    [Fact]
    public void ADualLayerGameCountsBothLayers()
    {
        var game = Game(QueueItemStatus.Ready) with { Layer1Sectors = GameSectors };

        Assert.Equal("2.79 GB", game.SizeLabel);
    }

    [Fact]
    public void AnEmptyQueueHasNoSummary()
    {
        Assert.Equal(string.Empty, InstallQueueSummary.Describe([], Roomy));
    }

    [Fact]
    public void WithNothingReadyThereIsNoTotal()
    {
        Assert.Equal("1 IN QUEUE · 0 READY", InstallQueueSummary.Describe([Game(QueueItemStatus.NeedsConversion)], Roomy));
    }

    [Fact]
    public void BeforeAConnectOnlyTheTotalShows()
    {
        Assert.Equal("2 IN QUEUE · 2 READY · 2.8 GB", InstallQueueSummary.Describe([Game(QueueItemStatus.Ready), Game(QueueItemStatus.Ready)], null));
    }

    [Fact]
    public void TheTotalOfTheReadyGamesIsSetAgainstTheFreeSpace()
    {
        var queue = new[] { Game(QueueItemStatus.Ready), Game(QueueItemStatus.Ready), Game(QueueItemStatus.Duplicate) };

        Assert.Equal("3 IN QUEUE · 2 READY · 2.8 GB OF 17.9 GB FREE", InstallQueueSummary.Describe(queue, Roomy));
    }

    [Fact]
    public void AQueueThatDoesNotFitSaysSo()
    {
        var queue = new[] { Game(QueueItemStatus.Ready), Game(QueueItemStatus.Ready) };

        Assert.Equal("2 IN QUEUE · 2 READY · 2.8 GB, MORE THAN THE 1.9 GB FREE", InstallQueueSummary.Describe(queue, Tight));
    }

    [Fact]
    public void DuringABatchTheFreeSpaceGoesDownByWhatWasSent()
    {
        var queue = new[] { Game(QueueItemStatus.Installed), Game(QueueItemStatus.Ready) };

        Assert.Equal("2 IN QUEUE · 1 READY · 1.4 GB OF 16.5 GB FREE", InstallQueueSummary.Describe(queue, Roomy, (ulong)Game(QueueItemStatus.Ready).InstallBytes));
        Assert.Equal("2 IN QUEUE · 1 READY · 1.4 GB, MORE THAN THE 0.0 GB FREE", InstallQueueSummary.Describe(queue, Tight, 10UL << 30));
    }

    private static QueuePreviewItem Game(QueueItemStatus status) => new()
    {
        SourceFile = "game.iso",
        Layer0Sectors = GameSectors,
        Status = status,
    };
}
