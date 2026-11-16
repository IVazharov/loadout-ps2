namespace Loadout.Persistence;

public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loadout-settings-").FullName;

    private string Database => Path.Combine(_root, "loadout.sqlite");

    public void Dispose() => TempDirectory.Delete(_root);

    [Fact]
    public void SettingsDefaultWhenTheDatabaseHasNoRowAndDoNotDial()
    {
        var store = new AppSettingsStore(Database);
        Assert.False(store.HasRow());
        var settings = store.Load();

        Assert.Equal(string.Empty, settings.Ps2Host);
        Assert.Equal(ThemeChoice.System, settings.Theme);
        Assert.False(store.HasRow());
    }

    [Fact]
    public void SavedSettingsAreVisibleToANewStore()
    {
        var store = new AppSettingsStore(Database);
        store.Save(AppSettings.Defaults with
        {
            Ps2Host = "127.0.0.1",
            Theme = ThemeChoice.Dark,
        });

        var again = new AppSettingsStore(Database).Load();
        Assert.Equal("127.0.0.1", again.Ps2Host);
        Assert.Equal(ThemeChoice.Dark, again.Theme);

        store.Save(again with { Theme = ThemeChoice.Light });
        Assert.Equal(ThemeChoice.Light, new AppSettingsStore(Database).Load().Theme);
        store.Save(again with { Theme = ThemeChoice.System });
        Assert.Equal(ThemeChoice.System, new AppSettingsStore(Database).Load().Theme);
    }

    [Fact]
    public void TheBackupFolderIsEmptyUntilChosenAndKeptOnceChosen()
    {
        var store = new AppSettingsStore(Database);
        Assert.Equal(string.Empty, store.Load().BackupFolder);

        store.Save(AppSettings.Defaults with { BackupFolder = @"C:\Users\someone\Documents\PS2 Backups " });

        Assert.Equal(@"C:\Users\someone\Documents\PS2 Backups", new AppSettingsStore(Database).Load().BackupFolder);
    }

    [Fact]
    public void TheInstallCheckIsOnByDefaultAndKeptOnceChosen()
    {
        var store = new AppSettingsStore(Database);
        Assert.True(store.Load().InstallPage.Check);

        store.Save(AppSettings.Defaults with { InstallPage = InstallOptions.Defaults with { Check = false } });

        Assert.False(new AppSettingsStore(Database).Load().InstallPage.Check);
    }

    [Fact]
    public void SettingsSavedBeforeTheInstallCheckLoadWithItOn()
    {
        CreateOldRow(darkMode: 1);
        var loaded = new AppSettingsStore(Database).Load();

        Assert.Equal("10.0.0.55", loaded.Ps2Host);
        Assert.True(loaded.InstallPage.Check);
        Assert.True(loaded.BackupPage.Check);
        Assert.True(loaded.CheckForUpdates);
    }

    [Fact]
    public void EachPageKeepsItsOwnInstallOptions()
    {
        var store = new AppSettingsStore(Database);
        Assert.Equal(InstallOptions.Defaults, store.Load().InstallPage);
        Assert.Equal(InstallOptions.Defaults, store.Load().BackupPage);

        store.Save(AppSettings.Defaults with
        {
            InstallPage = new InstallOptions(SendArtwork: false, MemoryCards: true, MemoryCardSize: 16, Check: true),
            BackupPage = new InstallOptions(SendArtwork: true, MemoryCards: false, MemoryCardSize: 32, Check: false),
        });

        var loaded = new AppSettingsStore(Database).Load();
        Assert.Equal(new InstallOptions(false, true, 16, true), loaded.InstallPage);
        Assert.Equal(new InstallOptions(true, false, 32, false), loaded.BackupPage);
    }

    [Fact]
    public void BackupsAreKeptAsChdsForDvdsAndBinsForCdsUntilChosenOtherwise()
    {
        var store = new AppSettingsStore(Database);
        Assert.Equal(new BackupFormats(DvdAsChd: true, CdAsChd: false), store.Load().BackupFormats);

        store.Save(AppSettings.Defaults with { BackupFormats = new BackupFormats(DvdAsChd: false, CdAsChd: true) });

        Assert.Equal(new BackupFormats(false, true), new AppSettingsStore(Database).Load().BackupFormats);
    }

    [Fact]
    public void AnInstallCheckTurnedOffBefore0110StaysOffOnBothPages()
    {
        // As Loadout 0.10.0 left the row: the check in check_installs, and no page options yet.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Database))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE app_settings (
                    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), ps2_host TEXT NOT NULL, chdman_path TEXT NOT NULL,
                    cache_directory TEXT NOT NULL, dark_mode INTEGER NOT NULL, check_installs INTEGER NOT NULL DEFAULT 1,
                    theme TEXT NULL, check_updates INTEGER NOT NULL DEFAULT 1, backup_folder TEXT NOT NULL DEFAULT '');
                INSERT INTO app_settings VALUES (1, '10.0.0.55', '', '', 1, 0, 'Dark', 1, '');
                """;
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var loaded = new AppSettingsStore(Database).Load();

        Assert.False(loaded.InstallPage.Check);
        Assert.False(loaded.BackupPage.Check);
        Assert.True(loaded.BackupPage.SendArtwork);
        Assert.False(loaded.InstallPage.MemoryCards);
    }

    [Fact]
    public void TheUpdateCheckIsOnByDefaultAndKeptOnceTurnedOff()
    {
        var store = new AppSettingsStore(Database);
        Assert.True(store.Load().CheckForUpdates);

        store.Save(AppSettings.Defaults with { CheckForUpdates = false });

        var loaded = new AppSettingsStore(Database).Load();
        Assert.False(loaded.CheckForUpdates);
        Assert.True(loaded.InstallPage.Check);
    }

    [Theory]
    [InlineData(1, ThemeChoice.Dark)]
    [InlineData(0, ThemeChoice.Light)]
    public void SettingsSavedBeforeTheThemeKeepTheirDarkModeChoice(int darkMode, ThemeChoice expected)
    {
        CreateOldRow(darkMode);
        var store = new AppSettingsStore(Database);

        Assert.Equal(expected, store.Load().Theme);
        store.Save(store.Load() with { CheckForUpdates = true });
        Assert.Equal(expected, new AppSettingsStore(Database).Load().Theme);
    }

    private void CreateOldRow(int darkMode)
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Database))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE app_settings (
                    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                    ps2_host TEXT NOT NULL,
                    chdman_path TEXT NOT NULL,
                    cache_directory TEXT NOT NULL,
                    dark_mode INTEGER NOT NULL
                );
                INSERT INTO app_settings VALUES (1, '10.0.0.55', '', '', $dark);
                """;
            command.Parameters.AddWithValue("$dark", darkMode);
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("10.0.0 .5")]
    [InlineData("10.1")]
    [InlineData("10.0.0.300")]
    [InlineData("ps2.local")]
    public void AnAddressThatIsNotIpv4IsRejectedAndNotSaved(string host)
    {
        var settings = AppSettings.Defaults with { Ps2Host = host };
        Assert.Throws<ArgumentException>(() => settings.EnsureValid());
        var store = new AppSettingsStore(Database);
        Assert.Throws<ArgumentException>(() => store.Save(settings));
        Assert.False(store.HasRow());
    }

    [Fact]
    public void NoAddressCanBeSaved()
    {
        var store = new AppSettingsStore(Database);
        store.Save(AppSettings.Defaults with { Ps2Host = string.Empty });

        Assert.Equal(string.Empty, new AppSettingsStore(Database).Load().Ps2Host);
    }

    [Fact]
    public void SettingsAndDataResolveNextToTheExe()
    {
        var exe = Path.Combine(_root, "exe");
        Directory.CreateDirectory(exe);

        var layout = PortableData.Resolve(exe);

        Assert.Equal(Path.Combine(exe, "Loadout.settings.json"), layout.SettingsFile);
        Assert.True(File.Exists(layout.SettingsFile));
        Assert.Equal(Path.Combine(exe, "data", "loadout.sqlite"), layout.DatabaseFile);
        Assert.Equal(Path.Combine(exe, "data", "artwork-cache"), layout.ArtworkCache);
        Assert.True(Directory.Exists(Path.Combine(exe, "data")));
    }
}
