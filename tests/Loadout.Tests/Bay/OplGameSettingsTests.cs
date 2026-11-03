using System.Text;
using Loadout.Core;
using Loadout.Fakes;

namespace Loadout.Bay;

public sealed class OplGameSettingsTests
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    private static readonly DiscId Rtcw = DiscId.TryParse("SLUS-20297", out var id) ? id : default;

    [Fact]
    public void ModesAndTransferModeAreReadAndEveryOtherLineIsKept()
    {
        var settings = OplGameSettings.Parse("$ConfigSource=1\r\n$Compatibility=5\r\n$DMA=4\r\n$VMC_0=generic\r\n"u8);

        Assert.Equal(5, settings.CompatibilityModes);
        Assert.Equal(4, settings.Dma);
        Assert.Equal("$ConfigSource=1\r\n$Compatibility=129\r\n$DMA=2\r\n$VMC_0=generic\r\n", Encoding.UTF8.GetString(settings.With(0x81, 2)));
    }

    [Fact]
    public void TheMemoryCardIsReadAndSetWithEveryOtherLineKept()
    {
        var settings = OplGameSettings.Parse("$ConfigSource=1\r\n$VMC_0=SLUS_202.97_0\r\n$DMA=4\r\n"u8);

        Assert.Equal("SLUS_202.97_0", settings.MemoryCard);
        Assert.Equal("$ConfigSource=1\r\n$VMC_0=mine\r\n$DMA=4\r\n", Encoding.UTF8.GetString(settings.WithMemoryCard("mine")));
        Assert.Null(OplGameSettings.Empty.MemoryCard);
        Assert.Null(OplGameSettings.Parse("$VMC_0=\r\n"u8).MemoryCard);
        Assert.Equal("$VMC_0=SLUS_202.97_0\r\n", Encoding.UTF8.GetString(OplGameSettings.Empty.WithMemoryCard("SLUS_202.97_0")));
    }

    [Fact]
    public void DefaultsAreLeftOutAsOplLeavesThemOut()
    {
        var settings = OplGameSettings.Parse("$Compatibility=5\n$DMA=4\n$GSM=1\n"u8);

        Assert.Equal("$GSM=1\r\n", Encoding.UTF8.GetString(settings.With(0, OplGameSettings.DefaultDma)));
        Assert.Equal("$Compatibility=2\r\n", Encoding.UTF8.GetString(OplGameSettings.Empty.With(2, OplGameSettings.DefaultDma)));
        Assert.Equal(0, OplGameSettings.Empty.CompatibilityModes);
        Assert.Equal(OplGameSettings.DefaultDma, OplGameSettings.Parse("$DMA=nonsense\r\n"u8).Dma);
        Assert.Equal("SLUS_202.97.cfg", OplGameSettings.FileName(Rtcw));
    }

    [Fact]
    public async Task SavingWritesOverTheFileAsThePs2HoldsIt()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = "Title=Return to Castle Ravensworth\r\n$DMA=3\r\n"u8.ToArray();

        var before = await BayGameChanges.ReadSettingsAsync("127.0.0.1", Rtcw, bay.Port);
        var after = await BayGameChanges.SaveSettingsAsync("127.0.0.1", Rtcw, 0x01, 3, bay.Port);

        Assert.Equal((0, 3), (before.CompatibilityModes, before.Dma));
        Assert.Equal((1, 3), (after.CompatibilityModes, after.Dma));
        Assert.Equal("Title=Return to Castle Ravensworth\r\n$DMA=3\r\n$Compatibility=1\r\n", Encoding.UTF8.GetString(bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")]));
    }

    [Fact]
    public async Task AGameWithoutSettingsReadsAsDefaultsAndDefaultsWriteNothing()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);

        var settings = await BayGameChanges.ReadSettingsAsync("127.0.0.1", Rtcw, bay.Port);
        await BayGameChanges.SaveSettingsAsync("127.0.0.1", Rtcw, 0, OplGameSettings.DefaultDma, bay.Port);

        Assert.Same(OplGameSettings.Empty, settings);
        Assert.Empty(bay.Files);
        Assert.DoesNotContain(BayWire.CommandFileWrite, bay.Commands);
    }

    [Fact]
    public async Task SettingsBackToTheDefaultsRemoveAFileThatHeldNothingElse()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = "$Compatibility=1\r\n"u8.ToArray();

        await BayGameChanges.SaveSettingsAsync("127.0.0.1", Rtcw, 0, OplGameSettings.DefaultDma, bay.Port);

        Assert.Empty(bay.Files);
    }

    [Fact]
    public async Task WithoutOplsPartitionReadingSettingsIsAnError()
    {
        using var bay = new FakeLoadoutBay(disk: Disk) { HasOplPartition = false };

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => BayGameChanges.ReadSettingsAsync("127.0.0.1", Rtcw, bay.Port));

        Assert.Equal(BayWire.StatusNotFound, error.Status);
    }
}
