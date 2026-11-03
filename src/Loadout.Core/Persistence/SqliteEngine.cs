using Microsoft.Data.Sqlite;

namespace Loadout.Persistence;

/// <summary>
/// The app uses the SQLite that Windows includes (<c>winsqlite3.dll</c>). A Windows image
/// with that file removed cannot run the app, so the app checks once at startup.
/// </summary>
public static class SqliteEngine
{
    /// <summary>Opens an in-memory database and returns the SQLite version. Throws when SQLite cannot be loaded.</summary>
    public static string Version()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
