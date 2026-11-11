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
        Assert.True(store.Load().CheckInstalls);

        store.Save(AppSettings.Defaults with { CheckInstalls = false });

        Assert.False(new AppSettingsStore(Database).Load().CheckInstalls);
    }

    [Fact]
    public void SettingsSavedBeforeTheInstallCheckLoadWithItOn()
    {
        CreateOldRow(darkMode: 1);
        var loaded = new AppSettingsStore(Database).Load();

        Assert.Equal("10.0.0.55", loaded.Ps2Host);
        Assert.True(loaded.CheckInstalls);
        Assert.True(loaded.CheckForUpdates);
    }

    [Fact]
    public void TheUpdateCheckIsOnByDefaultAndKeptOnceTurnedOff()
    {
        var store = new AppSettingsStore(Database);
        Assert.True(store.Load().CheckForUpdates);

        store.Save(AppSettings.Defaults with { CheckForUpdates = false });

        var loaded = new AppSettingsStore(Database).Load();
        Assert.False(loaded.CheckForUpdates);
        Assert.True(loaded.CheckInstalls);
    }

    [Theory]
    [InlineData(1, ThemeChoice.Dark)]
    [InlineData(0, ThemeChoice.Light)]
    public void SettingsSavedBeforeTheThemeKeepTheirDarkModeChoice(int darkMode, ThemeChoice expected)
    {
        CreateOldRow(darkMode);
        var store = new AppSettingsStore(Database);

        Assert.Equal(expected, store.Load().Theme);
        store.Save(store.Load() with { CheckInstalls = true });
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
