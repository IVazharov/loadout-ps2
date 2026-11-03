namespace Loadout.Persistence;

public sealed class UiStateStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loadout-uistate-").FullName;

    private string Database => Path.Combine(_root, "loadout.sqlite");

    public void Dispose() => TempDirectory.Delete(_root);

    [Fact]
    public void AMissingKeyGivesNull() => Assert.Null(new UiStateStore(Database).Get("table.installed"));

    [Fact]
    public void ASecondSetReplacesTheFirstAndSurvivesReopening()
    {
        var store = new UiStateStore(Database);
        store.Set("table.installed", "{}");
        store.Set("table.installed", """{"order":["art"]}""");

        Assert.Equal("""{"order":["art"]}""", new UiStateStore(Database).Get("table.installed"));
    }

    [Fact]
    public void DeleteRemovesOnlyThatKey()
    {
        var store = new UiStateStore(Database);
        store.Set("table.installed", "a");
        store.Set("table.queue", "b");

        store.Delete("table.installed");

        Assert.Null(store.Get("table.installed"));
        Assert.Equal("b", store.Get("table.queue"));
    }
}
