namespace Loadout.Persistence;

/// <summary>
/// Small JSON values the window keeps between runs, such as <c>table.installed</c> for a table's
/// column order and widths. Callers ignore failures: losing a layout only costs a re-fit.
/// </summary>
public sealed class UiStateStore
{
    private readonly string _connectionString;

    public UiStateStore(string databasePath)
    {
        _connectionString = SqliteFile.Prepare(
            databasePath,
            "CREATE TABLE IF NOT EXISTS ui_state (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);");
    }

    /// <summary>Null when nothing is stored under the key.</summary>
    public string? Get(string key)
    {
        using var connection = SqliteFile.Open(_connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ui_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value) =>
        SqliteFile.Upsert(
            _connectionString,
            "UPDATE ui_state SET value = $value WHERE key = $key;",
            "INSERT INTO ui_state (key, value) VALUES ($key, $value);",
            parameters =>
            {
                parameters.AddWithValue("$key", key);
                parameters.AddWithValue("$value", value);
            });

    public void Delete(string key)
    {
        using var connection = SqliteFile.Open(_connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ui_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }
}
