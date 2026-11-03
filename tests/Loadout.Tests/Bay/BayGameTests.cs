using System.Buffers.Binary;
using System.Text;
using Loadout.Fakes;
using Loadout.Images;

namespace Loadout.Bay;

public sealed class BayGameTests
{
    // Headers as HDLoader tools write them, with invented titles and trailing zeros trimmed.
    private const string MoonWarsCd =
        "7f6t3gAAAQBNb29uIFdhcnMgLSBTdXBlciBSb2NrZXQgUmFjaW5nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABABFNMVVNfMjAwLjQzAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAASAAAAAQAAAAAAAAAAIEgAAJCYDA==";

    private const string MythicsKeepDvd =
        "7f6t3gAAAQBNeXRoaWMncyBLZWVwIC0gRGVlcCBDcm9zc2luZyAyAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABABFNMVVNfMjA2Ljc1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUAAAABgAAAAAAAAAAIFAAAADAHwD4AwAEAGAAAPj/H//3BwAEAHAAAPj/H/73CwAEAIAAAPj/H/33DwAEAJAAAPj/H/z3EwAEAKAAACBJDg==";

    private static readonly BayFreeSpace Disk = new(TotalSectors: 0x04FFFFFF, FreeSectors: 0x04000000);

    [Fact]
    public void ACdHeaderReadsLikeHdlGameInstallersList()
    {
        var row = new BayGame("PP.SLUS-20043.HDL.Moon_Wars___Su", HdlGameHeader.TryRead(Header(MoonWarsCd))).ToInstalledGameRow();

        Assert.Equal("Moon Wars - Super Rocket Racing", row.Title);
        Assert.Equal("SLUS-20043", row.DiscId);
        Assert.Equal(HdlDiscTypes.Ps2Cd, row.DiscType);
        Assert.Equal(103186u, row.Sectors);
        Assert.Equal(0x40, row.TransferType);
        Assert.Equal(4, row.TransferMode);
        Assert.Equal(0, row.CompatibilityModeFlags);
    }

    [Fact]
    public void ADvdSplitOverSubPartitionsCountsEveryPart()
    {
        var header = HdlGameHeader.TryRead(Header(MythicsKeepDvd));

        Assert.NotNull(header);
        Assert.Equal("Mythic's Keep - Deep Crossing 2", header.Title);
        Assert.Equal("SLUS_206.75", header.StartupFileName);
        Assert.Equal(HdlDiscTypes.Ps2Dvd, header.DiscType);
        Assert.Equal(1425696u, header.Sectors);
        Assert.Equal(6, header.Parts.Count);
        Assert.Equal(new HdlGamePart(0, 0x502000, 260096 * 2048), header.Parts[0]);
        Assert.Equal(new HdlGamePart(260096, 0x600004, 262143 * 2048), header.Parts[1]);
        Assert.Equal(new HdlGamePart(1308668, 0xA00004, 117028 * 2048), header.Parts[5]);
    }

    [Fact]
    public void AHeaderWithoutTheMagicNumberIsListedAsUnreadable()
    {
        var bytes = Header(MoonWarsCd);
        bytes[0] = 0;

        var row = new BayGame("PP.BROKEN", HdlGameHeader.TryRead(bytes)).ToInstalledGameRow();

        Assert.Equal("PP.BROKEN", row.PartitionName);
        Assert.Equal(BayGame.UnreadableTitle, row.Title);
        Assert.Equal("", row.DiscId);
    }

    [Fact]
    public void AGameNameWithAHeaderOfZerosIsAnIncompleteInstall()
    {
        var game = BayWire.ReadGame(Entry("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[HdlGameHeader.Bytes]));
        var row = game.ToInstalledGameRow();

        Assert.True(game.Incomplete);
        Assert.True(row.Incomplete);
        Assert.Equal("Mythic s Keep", row.Title);
        Assert.Equal("SLUS-20035", row.DiscId);
    }

    [Fact]
    public void AHeaderThatCouldNotBeReadIsNotTakenForAnIncompleteInstall()
    {
        var entry = Entry("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[HdlGameHeader.Bytes]);
        entry[BayWire.EntryFlagsOffset] = BayWire.EntryFlagUnreadable;

        var row = BayWire.ReadGame(entry).ToInstalledGameRow();

        Assert.False(row.Incomplete);
        Assert.Equal(BayGame.UnreadableTitle, row.Title);
    }

    [Fact]
    public void OnlyAGameNameCanBeAnIncompleteInstall()
    {
        Assert.False(BayWire.ReadGame(Entry("PP.HOMEBREW", new byte[HdlGameHeader.Bytes])).Incomplete);
        Assert.False(BayWire.ReadGame(Entry("PP.SLUS-20035.HDL.Mythic_s_Keep_", Header(MoonWarsCd))).Incomplete);
        Assert.Null(BayGame.DiscIdOfName("XX.SLUS-20035.HDL.Game"));
        Assert.Equal("Moon Wars Su", BayGame.TitleOfName("PP.SLUS-20043.HDL.Moon_Wars___Su"));
        Assert.Equal("PP.SLUS-20043.HDL.___", BayGame.TitleOfName("PP.SLUS-20043.HDL.___"));
        Assert.Equal("SLUS-20035", BayGame.DiscIdOfName("PP.SLUS-20035.HDL."));
    }

    [Fact]
    public async Task AnIncompleteInstallIsRemovedAndAWholeGameIsNot()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games:
        [
            ("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[HdlGameHeader.Bytes]),
            ("PP.SLUS-20675.HDL.Mythic_s_Keep_", Header(MythicsKeepDvd)),
        ]);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        await client.RemoveIncompleteAsync("PP.SLUS-20035.HDL.Mythic_s_Keep_");
        var refused = await Assert.ThrowsAsync<LoadoutBayException>(() => client.RemoveIncompleteAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_"));

        Assert.Equal(BayWire.StatusNotPermitted, refused.Status);
        Assert.Equal(["PP.SLUS-20675.HDL.Mythic_s_Keep_"], (await client.ListGamesAsync()).Select(g => g.PartitionName));
    }

    [Fact]
    public async Task AWholeGameIsDeletedAndAnIncompleteInstallIsNot()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games:
        [
            ("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[HdlGameHeader.Bytes]),
            ("PP.SLUS-20675.HDL.Mythic_s_Keep_", Header(MythicsKeepDvd)),
        ]);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        await client.DeleteGameAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_");
        var refused = await Assert.ThrowsAsync<LoadoutBayException>(() => client.DeleteGameAsync("PP.SLUS-20035.HDL.Mythic_s_Keep_"));
        var missing = await Assert.ThrowsAsync<LoadoutBayException>(() => client.DeleteGameAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_"));

        Assert.Equal(BayWire.StatusNotPermitted, refused.Status);
        Assert.Equal(BayWire.StatusNotFound, missing.Status);
        Assert.Equal(["PP.SLUS-20035.HDL.Mythic_s_Keep_"], (await client.ListGamesAsync()).Select(g => g.PartitionName));
    }

    [Fact]
    public async Task AnEditChangesItsFieldsAndKeepsTheRestOfTheHeader()
    {
        var before = Header(MythicsKeepDvd);
        before[168] = 0x01;
        using var bay = new FakeLoadoutBay(disk: Disk, games: [("PP.SLUS-20675.HDL.Mythic_s_Keep_", before)]);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        var original = HdlGameHeader.TryRead(before)!;

        var game = await client.UpdateGameAsync(
            "PP.SLUS-20675.HDL.Mythic_s_Keep_",
            new BayGameEdit("Mythic's Keep II", 0x05, 0x20, 0, HdlDiscTypes.Ps2Cd));

        Assert.Equal(original with
        {
            Title = "Mythic's Keep II",
            CompatibilityModeFlags = 0x05,
            TransferType = 0x20,
            TransferMode = 0,
            DiscType = HdlDiscTypes.Ps2Cd,
        }, game.Header);
        Assert.Equal(0x01, game.Header!.HdlCompatibilityFlags);
        Assert.Equal(game.Header, (await client.ReadGameAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_")).Header);
    }

    [Fact]
    public async Task AnEditIsRefusedForAnIncompleteInstallAnEmptyTitleOrAnUnknownDiscType()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games:
        [
            ("PP.SLUS-20035.HDL.Mythic_s_Keep_", new byte[HdlGameHeader.Bytes]),
            ("PP.SLUS-20675.HDL.Mythic_s_Keep_", Header(MythicsKeepDvd)),
        ]);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);
        var edit = BayGameEdit.Of(HdlGameHeader.TryRead(Header(MythicsKeepDvd))!);

        var incomplete = await Assert.ThrowsAsync<LoadoutBayException>(() => client.UpdateGameAsync("PP.SLUS-20035.HDL.Mythic_s_Keep_", edit));
        var discType = await Assert.ThrowsAsync<LoadoutBayException>(() => client.UpdateGameAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_", edit with { DiscType = 0x15 }));

        Assert.Equal(BayWire.StatusNotPermitted, incomplete.Status);
        Assert.Equal(BayWire.StatusInvalid, discType.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateGameAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_", edit with { Title = " " }));
    }

    [Fact]
    public void AnEditsTitleEndsInsideItsFieldOnACharacterBoundary()
    {
        var payload = BayWire.WriteUpdateGame("PP.SLUS-20675.HDL.Mythic_s_Keep_", new BayGameEdit(new string('é', 100), 1, 0x40, 4, HdlDiscTypes.Ps2Dvd));
        var title = payload.AsSpan(BayWire.EntryNameBytes, BayWire.TitleBytes);

        Assert.Equal(BayWire.UpdateGameRequestBytes, payload.Length);
        Assert.Equal("PP.SLUS-20675.HDL.Mythic_s_Keep_", Encoding.UTF8.GetString(payload, 0, BayWire.EntryNameBytes).TrimEnd('\0'));
        Assert.Equal(new string('é', 79), Encoding.UTF8.GetString(title).TrimEnd('\0'));
        Assert.Equal(0, title[^1]);
        Assert.Equal([1, 0x40, 4, HdlDiscTypes.Ps2Dvd], payload[(BayWire.EntryNameBytes + BayWire.TitleBytes)..]);
    }

    [Fact]
    public void AStartupNameThatIsNotADiscIdLeavesTheDiscIdEmpty()
    {
        var row = new BayGame("PP.HOMEBREW", HdlGameHeader.TryRead(Synthetic("Homebrew", "BOOT.ELF"))).ToInstalledGameRow();

        Assert.Equal("Homebrew", row.Title);
        Assert.Equal("", row.DiscId);
    }

    [Fact]
    public async Task AListLongerThanAPageArrivesWholeAndInOrder()
    {
        var games = Enumerable.Range(0, 130).Select(i => ($"PP.GAME{i:D3}", Synthetic($"Game {i}", "SLES_538.86"))).ToList();
        using var bay = new FakeLoadoutBay(disk: Disk, games: games);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var listed = await client.ListGamesAsync();

        Assert.Equal(games.Select(g => g.Item1), listed.Select(g => g.PartitionName));
        Assert.Equal("Game 129", listed[^1].Header?.Title);
        Assert.Equal(3, bay.Commands.Count(c => c == BayWire.CommandListGames));
    }

    [Fact]
    public async Task AnEmptyDiskListsNoGames()
    {
        using var bay = new FakeLoadoutBay(disk: Disk);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        Assert.Empty(await client.ListGamesAsync());
    }

    [Fact]
    public async Task AListThatChangesWhileItIsReadIsRefused()
    {
        var games = Enumerable.Range(0, 70).Select(i => ($"PP.GAME{i:D3}", Synthetic($"Game {i}", "SLES_538.86"))).ToList();
        using var bay = new FakeLoadoutBay(disk: Disk, games: games);
        bay.AfterListPage = () => bay.Games = games.Take(69).ToList();
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.ListGamesAsync());

        Assert.Contains("changed while the list was read", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneGameIsReadByItsPartitionName()
    {
        using var bay = new FakeLoadoutBay(disk: Disk, games: [("PP.SLUS-20675.HDL.Mythic_s_Keep_", Header(MythicsKeepDvd))]);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var game = await client.ReadGameAsync("PP.SLUS-20675.HDL.Mythic_s_Keep_");
        var missing = await Assert.ThrowsAsync<LoadoutBayException>(() => client.ReadGameAsync("PP.NOT.THERE"));

        Assert.Equal("Mythic's Keep - Deep Crossing 2", game.Header?.Title);
        Assert.Equal(BayWire.StatusNotFound, missing.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadGameAsync("PP.NAME,PASSWORD"));
    }

    [Fact]
    public async Task ListingWithoutAHardDiskSaysSo()
    {
        using var bay = new FakeLoadoutBay(disk: null);
        using var client = await LoadoutBayClient.ConnectAsync("127.0.0.1", bay.Port);

        var error = await Assert.ThrowsAsync<LoadoutBayException>(() => client.ListGamesAsync());

        Assert.Equal(BayWire.StatusNoDevice, error.Status);
    }

    private static byte[] Header(string trimmedBase64)
    {
        var header = new byte[HdlGameHeader.Bytes];
        Convert.FromBase64String(trimmedBase64).CopyTo(header, 0);
        return header;
    }

    private static byte[] Entry(string partition, byte[] header)
    {
        var entry = new byte[BayWire.EntryBytes];
        Encoding.UTF8.GetBytes(partition).CopyTo(entry, 0);
        header.CopyTo(entry, BayWire.EntryNameBytes);
        return entry;
    }

    internal static byte[] Synthetic(string title, string startup)
    {
        var header = new byte[HdlGameHeader.Bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, HdlGameHeader.Magic);
        Encoding.UTF8.GetBytes(title).CopyTo(header, 8);
        Encoding.UTF8.GetBytes(startup).CopyTo(header, 172);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(236), HdlDiscTypes.Ps2Dvd);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(240), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(252), 10 * 2048);
        return header;
    }
}
