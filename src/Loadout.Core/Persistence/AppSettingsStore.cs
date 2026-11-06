using Loadout.Core;
using Microsoft.Data.Sqlite;

namespace Loadout.Persistence;

/// <summary>How Loadout looks: System follows Windows' app mode, including changes while it runs.</summary>
public enum ThemeChoice
{
    System,
    Dark,
    Light,
}

/// <summary>
/// One settings row in the app's SQLite file. A missing row uses the defaults.
/// Nothing here opens a socket.
/// </summary>
public sealed record AppSettings(
    string Ps2Host,
    ThemeChoice Theme,
    bool CheckInstalls = true)
{
    public static AppSettings Defaults { get; } = new(
        string.Empty,
        ThemeChoice.System);

    /// <summary>The address is an IPv4 address or empty (none set yet); host names are not kept.</summary>
    public void EnsureValid()
    {
        if (Ps2Host.Length > 0 && !Ipv4Text.IsValid(Ps2Host))
        {
            throw new ArgumentException("The PS2 address must be four numbers from 0 to 255, such as 192.168.1.50.");
        }
    }
}

public sealed class AppSettingsStore
{
    private readonly string _connectionString;

    public AppSettingsStore(string databasePath)
    {
        _connectionString = SqliteFile.Prepare(
            databasePath,
            """
            CREATE TABLE IF NOT EXISTS app_settings (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                ps2_host TEXT NOT NULL,
                -- Unused, and always written empty: older files have them NOT NULL.
                chdman_path TEXT NOT NULL,
                cache_directory TEXT NOT NULL,
                dark_mode INTEGER NOT NULL
            );
            """);

        // Added after the table's first form: an older row gets the check on.
        using var connection = Open();
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "check_installs", "INTEGER NOT NULL DEFAULT 1");

        // The theme was a dark_mode flag before. A row saved then has no theme and keeps what it showed:
        // 1 is Dark, 0 is Light. Only a new install starts on System.
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "theme", "TEXT NULL");
    }

    public AppSettings Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ps2_host, dark_mode, check_installs, theme
            FROM app_settings
            WHERE id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return AppSettings.Defaults;
        }

        return new AppSettings(
            reader.GetString(0),
            ReadTheme(reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(1) != 0),
            reader.GetInt32(2) != 0);
    }

    public bool HasRow()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_settings WHERE id = 1;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnsureValid();
        SqliteFile.Upsert(
            _connectionString,
            """
            UPDATE app_settings SET
                ps2_host = $host,
                dark_mode = $dark,
                check_installs = $check,
                theme = $theme
            WHERE id = 1;
            """,
            """
            INSERT INTO app_settings (
                id, ps2_host, chdman_path, cache_directory, dark_mode, check_installs, theme)
            VALUES (1, $host, '', '', $dark, $check, $theme);
            """,
            parameters =>
            {
                parameters.AddWithValue("$host", settings.Ps2Host.Trim());
                // Kept for an older Loadout opening the same file.
                parameters.AddWithValue("$dark", settings.Theme == ThemeChoice.Light ? 0 : 1);
                parameters.AddWithValue("$check", settings.CheckInstalls ? 1 : 0);
                parameters.AddWithValue("$theme", settings.Theme.ToString());
            });
    }

    private static ThemeChoice ReadTheme(string? stored, bool darkMode) =>
        Enum.TryParse<ThemeChoice>(stored, ignoreCase: true, out var theme) && Enum.IsDefined(theme)
            ? theme
            : darkMode ? ThemeChoice.Dark : ThemeChoice.Light;

    private SqliteConnection Open() => SqliteFile.Open(_connectionString);
}
