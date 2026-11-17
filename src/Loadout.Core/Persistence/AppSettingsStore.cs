using Loadout.Core;
using Loadout.Vmc;
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
    bool CheckForUpdates = true,
    string BackupFolder = "")
{
    public static AppSettings Defaults { get; } = new(
        string.Empty,
        ThemeChoice.System);

    /// <summary>The Install page's ticks for its installs.</summary>
    public InstallOptions InstallPage { get; init; } = InstallOptions.Defaults;

    /// <summary>Back up discs' own ticks, for Also install on PS2 and Install on PS2 from its list.</summary>
    public InstallOptions BackupPage { get; init; } = InstallOptions.Defaults;

    /// <summary>How Back up discs keeps its copies.</summary>
    public BackupFormats BackupFormats { get; init; } = BackupFormats.Defaults;

    /// <summary>
    /// Whether compressing and converting copies that closing Loadout stopped, or left waiting, goes on by itself when
    /// Loadout starts. On from the start.
    /// </summary>
    public bool ResumeConversions { get; init; } = true;

    /// <summary>The address is an IPv4 address or empty (none set yet); host names are not kept.</summary>
    public void EnsureValid()
    {
        if (Ps2Host.Length > 0 && !Ipv4Text.IsValid(Ps2Host))
        {
            throw new ArgumentException("The PS2 address must be four numbers from 0 to 255, such as 192.168.1.50.");
        }
    }
}

/// <summary>
/// What a page's installs bring along: each game's artwork, a memory card of a size for each game, and the check,
/// in which Loadout Bay reads the game back and compares it before it counts as installed.
/// </summary>
public sealed record InstallOptions(bool SendArtwork = true, bool MemoryCards = false, int MemoryCardSize = VmcImage.DefaultSize, bool Check = true)
{
    public static InstallOptions Defaults { get; } = new();

    // A size OPL doesn't make is read as the usual one.
    internal InstallOptions Valid() => VmcImage.Sizes.Contains(MemoryCardSize) ? this : this with { MemoryCardSize = VmcImage.DefaultSize };
}

/// <summary>
/// How Back up discs keeps its copies: a DVD as a chd (from the start) or an iso, a CD as a .bin and .cue (from the
/// start) or a chd. A copy is an iso or a bin while it is made either way.
/// </summary>
public sealed record BackupFormats(bool DvdAsChd = true, bool CdAsChd = false)
{
    public static BackupFormats Defaults { get; } = new();
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

        // Added with the update check: an older row gets it on.
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "check_updates", "INTEGER NOT NULL DEFAULT 1");

        // Added with disc backups: empty until the first backup asks for a folder.
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_folder", "TEXT NOT NULL DEFAULT ''");

        // Added in 0.11.0, when each page got its own ticks and the check left Settings: the Install page's artwork
        // and memory cards are remembered now, and Back up discs starts with the check as it was set.
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "install_artwork", "INTEGER NOT NULL DEFAULT 1");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "install_cards", "INTEGER NOT NULL DEFAULT 0");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "install_card_size", $"INTEGER NOT NULL DEFAULT {VmcImage.DefaultSize}");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_artwork", "INTEGER NOT NULL DEFAULT 1");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_cards", "INTEGER NOT NULL DEFAULT 0");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_card_size", $"INTEGER NOT NULL DEFAULT {VmcImage.DefaultSize}");
        if (SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_check", "INTEGER NOT NULL DEFAULT 1"))
        {
            using var carry = connection.CreateCommand();
            carry.CommandText = "UPDATE app_settings SET backup_check = check_installs;";
            carry.ExecuteNonQuery();
        }

        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_dvd_chd", "INTEGER NOT NULL DEFAULT 1");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_cd_chd", "INTEGER NOT NULL DEFAULT 0");
        SqliteFile.AddColumnIfMissing(connection, "app_settings", "backup_resume", "INTEGER NOT NULL DEFAULT 1");
    }

    public AppSettings Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ps2_host, dark_mode, check_installs, theme, check_updates, backup_folder,
                   install_artwork, install_cards, install_card_size, backup_artwork, backup_cards, backup_card_size, backup_check,
                   backup_dvd_chd, backup_cd_chd, backup_resume
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
            reader.GetInt32(4) != 0,
            reader.GetString(5))
        {
            InstallPage = new InstallOptions(reader.GetInt32(6) != 0, reader.GetInt32(7) != 0, reader.GetInt32(8), reader.GetInt32(2) != 0).Valid(),
            BackupPage = new InstallOptions(reader.GetInt32(9) != 0, reader.GetInt32(10) != 0, reader.GetInt32(11), reader.GetInt32(12) != 0).Valid(),
            BackupFormats = new BackupFormats(reader.GetInt32(13) != 0, reader.GetInt32(14) != 0),
            ResumeConversions = reader.GetInt32(15) != 0,
        };
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
                theme = $theme,
                check_updates = $updates,
                backup_folder = $backups,
                install_artwork = $installArtwork,
                install_cards = $installCards,
                install_card_size = $installCardSize,
                backup_artwork = $backupArtwork,
                backup_cards = $backupCards,
                backup_card_size = $backupCardSize,
                backup_check = $backupCheck,
                backup_dvd_chd = $dvdChd,
                backup_cd_chd = $cdChd,
                backup_resume = $resume
            WHERE id = 1;
            """,
            """
            INSERT INTO app_settings (
                id, ps2_host, chdman_path, cache_directory, dark_mode, check_installs, theme, check_updates, backup_folder,
                install_artwork, install_cards, install_card_size, backup_artwork, backup_cards, backup_card_size, backup_check,
                backup_dvd_chd, backup_cd_chd, backup_resume)
            VALUES (1, $host, '', '', $dark, $check, $theme, $updates, $backups,
                $installArtwork, $installCards, $installCardSize, $backupArtwork, $backupCards, $backupCardSize, $backupCheck,
                $dvdChd, $cdChd, $resume);
            """,
            parameters =>
            {
                parameters.AddWithValue("$host", settings.Ps2Host.Trim());
                // Kept for an older Loadout opening the same file.
                parameters.AddWithValue("$dark", settings.Theme == ThemeChoice.Light ? 0 : 1);
                // The Install page's check, where Settings kept it before 0.11.0.
                parameters.AddWithValue("$check", settings.InstallPage.Check ? 1 : 0);
                parameters.AddWithValue("$theme", settings.Theme.ToString());
                parameters.AddWithValue("$updates", settings.CheckForUpdates ? 1 : 0);
                parameters.AddWithValue("$backups", settings.BackupFolder.Trim());
                var install = settings.InstallPage.Valid();
                var backup = settings.BackupPage.Valid();
                parameters.AddWithValue("$installArtwork", install.SendArtwork ? 1 : 0);
                parameters.AddWithValue("$installCards", install.MemoryCards ? 1 : 0);
                parameters.AddWithValue("$installCardSize", install.MemoryCardSize);
                parameters.AddWithValue("$backupArtwork", backup.SendArtwork ? 1 : 0);
                parameters.AddWithValue("$backupCards", backup.MemoryCards ? 1 : 0);
                parameters.AddWithValue("$backupCardSize", backup.MemoryCardSize);
                parameters.AddWithValue("$backupCheck", backup.Check ? 1 : 0);
                parameters.AddWithValue("$dvdChd", settings.BackupFormats.DvdAsChd ? 1 : 0);
                parameters.AddWithValue("$cdChd", settings.BackupFormats.CdAsChd ? 1 : 0);
                parameters.AddWithValue("$resume", settings.ResumeConversions ? 1 : 0);
            });
    }

    private static ThemeChoice ReadTheme(string? stored, bool darkMode) =>
        Enum.TryParse<ThemeChoice>(stored, ignoreCase: true, out var theme) && Enum.IsDefined(theme)
            ? theme
            : darkMode ? ThemeChoice.Dark : ThemeChoice.Light;

    private SqliteConnection Open() => SqliteFile.Open(_connectionString);
}
