using Loadout.Core;
using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayGameChangesTests
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    [Fact]
    public async Task RemoveTakesWholeGamesAndIncompleteInstallsAndReportsWhatWasRefused()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games:
        [
            ("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[1024]),
            ("PP.SLUS-20675.HDL.Mythic_s_Keep_", BayGameTests.Synthetic("Mythic's Keep - Deep Crossing 2", "SLUS_206.75")),
            ("PP.SLUS-20043.HDL.Moon_Wars___Su", BayGameTests.Synthetic("Moon Wars - Super Rocket Racing", "SLUS_200.43")),
        ]);
        var rows = (await BayLibrary.ReadAsync("127.0.0.1", bay.Port)).Games;
        bay.Games = bay.Games.Where(g => g.Partition != "PP.SLUS-20043.HDL.Moon_Wars___Su").ToList();

        var removal = await BayGameChanges.RemoveAsync("127.0.0.1", rows, bay.Port);

        Assert.Equal(["PP.SLUS-20035.HDL.Mythic_s_Keep_", "PP.SLUS-20675.HDL.Mythic_s_Keep_"], removal.Removed.Select(g => g.PartitionName));
        var failure = Assert.Single(removal.Failed);
        Assert.Equal("PP.SLUS-20043.HDL.Moon_Wars___Su", failure.Game.PartitionName);
        Assert.Equal(BayWire.DescribeStatus(BayWire.StatusNotFound), failure.Reason);
        Assert.Empty(bay.Games);
        Assert.Equal(
            [BayWire.CommandHello, BayWire.CommandFreeSpace, BayWire.CommandListGames, BayWire.CommandHello, BayWire.CommandRemoveIncomplete, BayWire.CommandDeleteGame, BayWire.CommandDeleteGame],
            bay.Commands);
    }

    [Fact]
    public async Task AnEditStartsFromTheHeaderAsThePs2HoldsItNow()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games:
            [("PP.SLUS-20675.HDL.Mythic_s_Keep_", BayGameTests.Synthetic("Mythic's Keep - Deep Crossing 2", "SLUS_206.75"))]);

        var row = await BayGameChanges.EditAsync("127.0.0.1", "PP.SLUS-20675.HDL.Mythic_s_Keep_", edit => edit with { CompatibilityModeFlags = 0x81 }, bay.Port);

        Assert.Equal("Mythic's Keep - Deep Crossing 2", row.Title);
        Assert.Equal(0x81, row.CompatibilityModeFlags);
        Assert.Equal(HdlDiscTypes.Ps2Dvd, row.DiscType);
        Assert.Equal([BayWire.CommandHello, BayWire.CommandReadEntry, BayWire.CommandUpdateGame], bay.Commands);
    }

    [Fact]
    public void OnlyGamesLoadoutBayCanTakeAreOfferedForRemoveAndEdit()
    {
        var whole = new BayGame("PP.SLUS-20675.HDL.Mythic_s_Keep_", HdlGameHeader.TryRead(BayGameTests.Synthetic("Game", "SLUS_206.75"))).ToInstalledGameRow();
        var incomplete = new BayGame("PP.SLUS-20035.HDL.Mythic_s_Keep_", null, Incomplete: true).ToInstalledGameRow();
        var unreadable = new BayGame("PP.SLUS-20043.HDL.Moon_Wars___Su", null).ToInstalledGameRow();
        var homebrew = new BayGame("PP.HOMEBREW", HdlGameHeader.TryRead(BayGameTests.Synthetic("Homebrew", "BOOT.ELF"))).ToInstalledGameRow();

        Assert.True(BayGameChanges.CanRemove(whole) && BayGameChanges.CanEdit(whole));
        Assert.True(BayGameChanges.CanRemove(incomplete) && !BayGameChanges.CanEdit(incomplete));
        Assert.False(BayGameChanges.CanRemove(unreadable) || BayGameChanges.CanEdit(unreadable));
        Assert.False(BayGameChanges.CanRemove(homebrew) || BayGameChanges.CanEdit(homebrew));
    }

    [Fact]
    public async Task CleaningUpDeletesOnlyTheseGamesArtAndSettings()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        bay.Files[(BayFolder.Art, "SLUS_202.97_COV.png")] = [1];
        bay.Files[(BayFolder.Art, "SLUS_202.97_SCR1.png")] = [2];
        bay.Files[(BayFolder.Art, "SLUS_202.970_COV.png")] = [3];
        bay.Files[(BayFolder.Art, "SCUS_973.99_COV.png")] = [4];
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = [5];
        bay.Files[(BayFolder.Cfg, "SCUS_973.99.cfg")] = [6];
        Assert.True(DiscId.TryParse("SLUS-20297", out var rtcw));

        var removal = await BayGameChanges.RemoveOplFilesAsync("127.0.0.1", [rtcw], bay.Port);

        Assert.Equal(["ART/SLUS_202.97_COV.png", "ART/SLUS_202.97_SCR1.png", "CFG/SLUS_202.97.cfg"], removal.Deleted.Order(StringComparer.Ordinal));
        Assert.Empty(removal.Failed);
        Assert.Equal(["SCUS_973.99.cfg", "SCUS_973.99_COV.png", "SLUS_202.970_COV.png"], bay.Files.Keys.Select(key => key.Name).Order(StringComparer.Ordinal));
    }
}
