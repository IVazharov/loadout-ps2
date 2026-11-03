namespace Loadout.Persistence;

/// <summary>
/// Each store writes a row, then writes the same key again. The second write must
/// replace the first without adding a row. The stores do this without
/// <c>ON CONFLICT ... DO UPDATE</c>, which older Windows 10 SQLite builds lack.
/// </summary>
public sealed class UpsertTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loadout-upsert-").FullName;

    private string Database => Path.Combine(_root, "queue.sqlite");

    public void Dispose() => TempDirectory.Delete(_root);

    [Fact]
    public void SavingSettingsTwiceKeepsOneRowWithTheSecondValues()
    {
        var store = new AppSettingsStore(Database);
        store.Save(AppSettings.Defaults with { Ps2Host = "192.168.0.10", Theme = ThemeChoice.Dark });
        store.Save(AppSettings.Defaults with { Ps2Host = "192.168.0.11", Theme = ThemeChoice.Light });

        var loaded = new AppSettingsStore(Database).Load();

        Assert.Equal("192.168.0.11", loaded.Ps2Host);
        Assert.Equal(ThemeChoice.Light, loaded.Theme);
        Assert.True(store.HasRow());
    }

    [Fact]
    public void StoringATitleTwiceKeepsTheSecondAnswer()
    {
        var cache = new DiscTitleCache(Database);
        cache.Store(new DiscTitleEntry("SLUS-20745", null, Found: false));
        cache.Store(new DiscTitleEntry("SLUS-20745", "Gnome 2", Found: true));

        var entry = new DiscTitleCache(Database).TryGet("SLUS-20745");

        Assert.NotNull(entry);
        Assert.True(entry.Found);
        Assert.Equal("Gnome 2", entry.Title);
    }
}
