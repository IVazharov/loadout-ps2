using System.Globalization;
using Loadout.Library;
using Microsoft.Data.Sqlite;

namespace Loadout.Persistence;

public sealed record SavedInstalledGames(IReadOnlyList<InstalledGameRow> Games, DateTimeOffset SavedAt);

/// <summary>
/// The last installed-game list the PS2 returned, with every field the Installed Games page shows.
/// The app shows this list at startup, before a Connect. The PS2 stays the authority: every list
/// it returns replaces this one, and nothing is written to the console from it without a Connect first.
/// </summary>
public sealed class InstalledGameListStore
{
    private readonly string _connectionString;

    public InstalledGameListStore(string databasePath)
    {
        _connectionString = SqliteFile.Prepare(
            databasePath,
            // The list is only a copy of the PS2's, so tables in an older layout go, and Connect fills these.
            """
            DROP TABLE IF EXISTS installed_games;
            DROP TABLE IF EXISTS installed_game_rows;
            DROP TABLE IF EXISTS installed_games_saved;
            CREATE TABLE IF NOT EXISTS saved_game_rows (
                position INTEGER NOT NULL PRIMARY KEY,
                partition_name TEXT NOT NULL,
                title TEXT NOT NULL,
                disc_id TEXT NOT NULL,
                compatibility_flags INTEGER NOT NULL,
                transfer_type INTEGER NOT NULL,
                transfer_mode INTEGER NOT NULL,
                disc_type INTEGER NOT NULL,
                sectors INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS saved_game_list (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                saved_at_utc TEXT NOT NULL
            );
            """);
    }

    public void Replace(IReadOnlyList<InstalledGameRow> games, DateTimeOffset savedAt)
    {
        ArgumentNullException.ThrowIfNull(games);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM saved_game_rows; DELETE FROM saved_game_list;";
        command.ExecuteNonQuery();

        command.CommandText =
            """
            INSERT INTO saved_game_rows (
                position, partition_name, title, disc_id, compatibility_flags, transfer_type,
                transfer_mode, disc_type, sectors)
            VALUES ($position, $partition, $title, $disc, $flags, $type, $mode, $discType, $sectors);
            """;
        var position = command.Parameters.Add("$position", SqliteType.Integer);
        var partition = command.Parameters.Add("$partition", SqliteType.Text);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var disc = command.Parameters.Add("$disc", SqliteType.Text);
        var flags = command.Parameters.Add("$flags", SqliteType.Integer);
        var type = command.Parameters.Add("$type", SqliteType.Integer);
        var mode = command.Parameters.Add("$mode", SqliteType.Integer);
        var discType = command.Parameters.Add("$discType", SqliteType.Integer);
        var sectors = command.Parameters.Add("$sectors", SqliteType.Integer);
        for (var i = 0; i < games.Count; i++)
        {
            var game = games[i];
            position.Value = i;
            partition.Value = game.PartitionName;
            title.Value = game.Title;
            disc.Value = game.DiscId;
            flags.Value = game.CompatibilityModeFlags;
            type.Value = game.TransferType;
            mode.Value = game.TransferMode;
            discType.Value = game.DiscType;
            sectors.Value = (long)game.Sectors;
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO saved_game_list (id, saved_at_utc) VALUES (1, $saved);";
        command.Parameters.AddWithValue("$saved", savedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Null when no list was ever saved.</summary>
    public SavedInstalledGames? Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT saved_at_utc FROM saved_game_list WHERE id = 1;";
        if (command.ExecuteScalar() is not string savedText
            || !DateTimeOffset.TryParse(savedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var savedAt))
        {
            return null;
        }

        command.CommandText =
            """
            SELECT partition_name, title, disc_id, compatibility_flags, transfer_type, transfer_mode,
                disc_type, sectors
            FROM saved_game_rows
            ORDER BY position;
            """;
        var games = new List<InstalledGameRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            games.Add(new InstalledGameRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (byte)reader.GetInt32(3),
                (byte)reader.GetInt32(4),
                (byte)reader.GetInt32(5),
                (byte)reader.GetInt32(6),
                (uint)reader.GetInt64(7),
                // Only an incomplete install has a disc ID and no sectors: it has no header.
                Incomplete: reader.GetInt64(7) == 0 && reader.GetString(2).Length > 0));
        }

        return new SavedInstalledGames(games, savedAt);
    }

    private SqliteConnection Open() => SqliteFile.Open(_connectionString);
}
