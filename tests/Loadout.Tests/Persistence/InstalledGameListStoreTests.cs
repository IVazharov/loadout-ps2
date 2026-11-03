using Microsoft.Data.Sqlite;
using Loadout.Library;

namespace Loadout.Persistence;

public sealed class InstalledGameListStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loadout-gamelist-").FullName;

    private string Database => Path.Combine(_root, "queue.sqlite");

    public void Dispose() => TempDirectory.Delete(_root);

    [Fact]
    public void NothingIsLoadedBeforeAListWasSaved()
    {
        Assert.Null(new InstalledGameListStore(Database).Load());
    }

    [Fact]
    public void EveryShownFieldComesBackInOrderInANewStore()
    {
        var savedAt = new DateTimeOffset(2026, 10, 15, 16, 42, 0, TimeSpan.FromHours(3));
        var gnome = new InstalledGameRow("PP.SLUS-20745.HDL.Gnome_2", "Gnome 2", "SLUS-20745", 0x81, 0x40, 4, 0x14, 2_100_000);
        var goal = new InstalledGameRow("PP.SLES-55665.HDL.Goal_2013", "Goal 2013", "SLES-55665", 0x05, 0x20, 2, 0x14, uint.MaxValue);
        new InstalledGameListStore(Database).Replace([gnome, goal], savedAt);

        var loaded = new InstalledGameListStore(Database).Load();

        Assert.NotNull(loaded);
        Assert.Equal([gnome, goal], loaded.Games);
        Assert.Equal(savedAt, loaded.SavedAt);
    }

    [Fact]
    public void AnIncompleteInstallIsStillMarkedAfterARestart()
    {
        var cut = new InstalledGameRow("PP.SLUS-20035.HDL.Mythic_s_Keep_", "Mythic's Keep - Deep Crossing", "SLUS-20035", 0, 0, 0, 0, 0, Incomplete: true);
        var unreadable = new InstalledGameRow("PP.BROKEN", "(unreadable game header)", "", 0, 0, 0, 0, 0);
        new InstalledGameListStore(Database).Replace([cut, unreadable], DateTimeOffset.Now);

        var loaded = new InstalledGameListStore(Database).Load();

        Assert.NotNull(loaded);
        Assert.Equal([cut, unreadable], loaded.Games);
    }

    [Fact]
    public void ALaterListReplacesTheEarlierOneIncludingAnEmptyOne()
    {
        var store = new InstalledGameListStore(Database);
        store.Replace([Row("SLUS-20745")], DateTimeOffset.UtcNow);
        store.Replace([Row("SLUS-21376")], DateTimeOffset.UtcNow);
        Assert.Equal("SLUS-21376", Assert.Single(store.Load()!.Games).DiscId);

        store.Replace([], DateTimeOffset.UtcNow);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Games);
    }

    [Fact]
    public void AListSavedByAnEarlierLayoutIsDroppedAndTheNextConnectCanSave()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE installed_game_rows (position INTEGER PRIMARY KEY, disc_id TEXT NOT NULL, osd_line1 TEXT NOT NULL);
                INSERT INTO installed_game_rows VALUES (0, 'SLUS-20745', 'Gnome 2');
                CREATE TABLE installed_games_saved (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), saved_at_utc TEXT NOT NULL);
                INSERT INTO installed_games_saved VALUES (1, '2026-10-15T13:42:00.0000000Z');
                """;
            command.ExecuteNonQuery();
        }

        var store = new InstalledGameListStore(Database);

        Assert.Null(store.Load());
        store.Replace([Row("SLUS-21376")], DateTimeOffset.UtcNow);
        Assert.Equal("SLUS-21376", Assert.Single(store.Load()!.Games).DiscId);
    }

    private static InstalledGameRow Row(string discId)
        => new("PP." + discId + ".HDL.Game", "Game", discId, 0, 0x40, 4, 0x14, 10);
}
