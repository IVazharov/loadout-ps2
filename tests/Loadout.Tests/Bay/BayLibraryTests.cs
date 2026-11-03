using Loadout.Fakes;

namespace Loadout.Bay;

public sealed class BayLibraryTests
{
    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x05000000, FreeSectors: 0x043C0000);

    [Fact]
    public async Task OneReadGivesTheSpaceAndEveryGameAsInstalledGamesRows()
    {
        using var bay = new FakeLoadoutBay(
            disk: Disk,
            games:
            [
                ("PP.SLES-53886.HDL.Game_One", BayGameTests.Synthetic("Game One", "SLES_538.86")),
                ("PP.BROKEN", new byte[HdlGameHeader.Bytes]),
            ]);

        var library = await BayLibrary.ReadAsync("127.0.0.1", bay.Port);

        Assert.Equal(bay.Version, library.Hello.Version);
        Assert.Equal(Disk, library.Space);
        Assert.Equal(["PP.SLES-53886.HDL.Game_One", "PP.BROKEN"], library.Games.Select(g => g.PartitionName));
        Assert.Equal("SLES-53886", library.Games[0].DiscId);
        Assert.Equal(BayGame.UnreadableTitle, library.Games[1].Title);
        Assert.Equal([BayWire.CommandHello, BayWire.CommandFreeSpace, BayWire.CommandListGames], bay.Commands);
    }

    [Fact]
    public async Task ALoadoutBayWithoutAHardDiskIsReportedBeforeAnythingElseIsAsked()
    {
        using var bay = new FakeLoadoutBay(disk: null);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => BayLibrary.ReadAsync("127.0.0.1", bay.Port));

        Assert.Equal(BayWire.StatusNoDevice, error.Status);
        Assert.Equal([BayWire.CommandHello], bay.Commands);
    }

    [Fact]
    public async Task ItSaysSoOnceConnectedBeforeTheGamesAreRead()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games: [("PP.SLES-53886.HDL.Game_One", BayGameTests.Synthetic("Game One", "SLES_538.86"))]);
        var heard = new Heard(bay);

        await BayLibrary.ReadAsync("127.0.0.1", bay.Port, heard);

        Assert.Equal(bay.Version, heard.Hello?.Version);
        Assert.Equal([BayWire.CommandHello], heard.CommandsSoFar);
    }

    [Fact]
    public async Task WithoutAHardDiskItDoesNotSayItIsReadingGames()
    {
        using var bay = new FakeLoadoutBay(disk: null);
        var heard = new Heard(bay);

        await Assert.ThrowsAsync<LoadoutBayException>(() => BayLibrary.ReadAsync("127.0.0.1", bay.Port, heard));

        Assert.Null(heard.Hello);
    }

    private sealed class Heard(FakeLoadoutBay bay) : IProgress<BayHello>
    {
        public BayHello? Hello { get; private set; }

        public IReadOnlyList<uint> CommandsSoFar { get; private set; } = [];

        public void Report(BayHello value)
        {
            Hello = value;
            CommandsSoFar = bay.Commands.ToList();
        }
    }
}
