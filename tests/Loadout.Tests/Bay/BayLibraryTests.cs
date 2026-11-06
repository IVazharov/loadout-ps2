using System.Text;
using Loadout.Fakes;
using Loadout.Library;

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
        Assert.Equal(MemoryCardStatus.None, library.Games[0].MemoryCard);
        Assert.Null(library.MemoryCardError);
        // The memory cards: OPL's VMC and CFG folders, with no settings file to read for Game One.
        Assert.Equal(
            [BayWire.CommandHello, BayWire.CommandFreeSpace, BayWire.CommandListGames, BayWire.CommandFileList, BayWire.CommandFileList],
            bay.Commands);
    }

    [Fact]
    public async Task EachGameGetsTheCardItsSettingsNameAsPresentSharedOrMissing()
    {
        using var bay = new FakeLoadoutBay(
            disk: Disk,
            games:
            [
                ("PP.SLES-53886.HDL.Game_One", BayGameTests.Synthetic("Game One", "SLES_538.86")),
                ("PP.SLUS-20297.HDL.Game_Two", BayGameTests.Synthetic("Game Two", "SLUS_202.97")),
                ("PP.SLUS-20675.HDL.Game_Three", BayGameTests.Synthetic("Game Three", "SLUS_206.75")),
                ("PP.SLUS-20062.HDL.Game_Four", BayGameTests.Synthetic("Game Four", "SLUS_200.62")),
            ]);
        bay.Files[(BayFolder.Vmc, "SLES_538.86_0.bin")] = new byte[8 << 20];
        bay.Files[(BayFolder.Vmc, "Shared.bin")] = new byte[16 << 20];
        bay.Files[(BayFolder.Cfg, "SLES_538.86.cfg")] = Encoding.UTF8.GetBytes("$VMC_0=SLES_538.86_0\r\n");
        bay.Files[(BayFolder.Cfg, "SLUS_202.97.cfg")] = Encoding.UTF8.GetBytes("$DMA=7\r\n$VMC_0=Shared\r\n");
        bay.Files[(BayFolder.Cfg, "SLUS_206.75.cfg")] = Encoding.UTF8.GetBytes("$VMC_0=Shared\r\n");
        bay.Files[(BayFolder.Cfg, "SLUS_200.62.cfg")] = Encoding.UTF8.GetBytes("$VMC_0=Gone\r\n");

        var library = await BayLibrary.ReadAsync("127.0.0.1", bay.Port);
        var cards = library.Games.ToDictionary(game => game.Title, game => game.MemoryCard!);

        Assert.Equal(new MemoryCardStatus(MemoryCardKind.Present, "SLES_538.86_0", 8 << 20), cards["Game One"]);
        Assert.Equal("8 MB", cards["Game One"].Label);
        Assert.Equal("16 MB, shared", cards["Game Two"].Label);
        Assert.Equal(["Game Three"], cards["Game Two"].SharedWith!);
        Assert.Equal(["Game Two"], cards["Game Three"].SharedWith!);
        Assert.Equal(MemoryCardKind.Missing, cards["Game Four"].Kind);
        Assert.Equal("Gone", cards["Game Four"].Card);
        Assert.Equal("Missing", cards["Game Four"].Label);
        Assert.False(library.Games.Single(game => game.Title == "Game One").CanGetMemoryCard);
        Assert.True(library.Games.Single(game => game.Title == "Game Four").CanGetMemoryCard);
    }

    [Fact]
    public async Task WithoutOplsPartitionTheListIsWholeAndSaysWhyTheCardsWereNotRead()
    {
        using var bay = new FakeLoadoutBay(
            disk: Disk,
            games: [("PP.SLES-53886.HDL.Game_One", BayGameTests.Synthetic("Game One", "SLES_538.86"))]);
        bay.HasOplPartition = false;

        var library = await BayLibrary.ReadAsync("127.0.0.1", bay.Port);

        Assert.Equal("Game One", Assert.Single(library.Games).Title);
        Assert.Null(library.Games[0].MemoryCard);
        Assert.Equal("", library.Games[0].MemoryCardLabel);
        Assert.Contains("no OPL partition", library.MemoryCardError, StringComparison.Ordinal);
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
