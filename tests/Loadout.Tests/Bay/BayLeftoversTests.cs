using Loadout.Fakes;

namespace Loadout.Bay;

public sealed class BayLeftoversTests
{
    [Fact]
    public async Task EveryPartFileInOplsFoldersIsDeletedAndNothingElse()
    {
        using var bay = new FakeLoadoutBay();
        bay.Files[(BayFolder.Art, "SLES_502.14_COV.png")] = [1];
        bay.Files[(BayFolder.Art, "SLES_502.14_BG.png.part")] = new byte[300];
        bay.Files[(BayFolder.Cfg, "SLES_502.14.cfg.part")] = new byte[20];
        bay.Files[(BayFolder.App, "loadout-bay.elf")] = [2];
        bay.Files[(BayFolder.App, "loadout-bay.elf.part")] = new byte[40];
        bay.Files[(BayFolder.Vmc, "SLUS_200.43_0.bin")] = [3];

        // As on the user's console: half a card that a full partition cut short, which VMC's names can't reach.
        bay.Files[(BayFolder.Vmc, "SLES_537.96_0.bin.part")] = new byte[4775936];

        var cleared = await BayLeftovers.ClearAsync("127.0.0.1", bay.Port);

        Assert.Equal(
            ["ART/SLES_502.14_BG.png.part", "CFG/SLES_502.14.cfg.part", "VMC/SLES_537.96_0.bin.part", "APPS/Loadout-Bay/loadout-bay.elf.part"],
            cleared.Select(file => file.Path));
        Assert.Equal(4775936, cleared[2].Bytes);
        Assert.Equal(
            [(BayFolder.Art, "SLES_502.14_COV.png"), (BayFolder.Vmc, "SLUS_200.43_0.bin"), (BayFolder.App, "loadout-bay.elf")],
            bay.Files.Keys.OrderBy(key => key.Folder).ThenBy(key => key.Name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACardWithTheLeftoversNameKeepsItsSavesAndTheLeftoverStays()
    {
        using var bay = new FakeLoadoutBay();
        bay.Files[(BayFolder.Vmc, "SLUS_200.43_0.bin")] = [7, 7, 7];
        bay.Files[(BayFolder.Vmc, "SLUS_200.43_0.bin.part")] = new byte[10];

        var cleared = await BayLeftovers.ClearAsync("127.0.0.1", bay.Port);

        Assert.Empty(cleared);
        Assert.Equal([7, 7, 7], bay.Files[(BayFolder.Vmc, "SLUS_200.43_0.bin")]);
        Assert.True(bay.Files.ContainsKey((BayFolder.Vmc, "SLUS_200.43_0.bin.part")));
    }

    [Fact]
    public async Task WithoutOplsPartitionThereIsNothingToClear()
    {
        using var bay = new FakeLoadoutBay { HasOplPartition = false };

        Assert.Empty(await BayLeftovers.ClearAsync("127.0.0.1", bay.Port));
    }

    [Fact]
    public async Task AFileThatFindsNoRoomIsToldAsOplsPartitionsAndLeavesNoPart()
    {
        using var bay = new FakeLoadoutBay { FileWriteStatus = BayWire.StatusNoSpace };
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.WriteFileAsync(BayFolder.Vmc, "SLES_537.96_0.bin", new byte[100]));

        Assert.Equal("OPL's partition on the PS2 has no room for VMC/SLES_537.96_0.bin.", error.Message);
        Assert.Empty(bay.Files);
    }
}
