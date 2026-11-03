using Microsoft.Data.Sqlite;

namespace Loadout.Persistence;

/// <summary>
/// Parsed game titles cached by canonical disc ID. The CFG body is not stored.
/// </summary>
public sealed record DiscTitleEntry(string CanonicalDiscId, string? Title, bool Found);

public sealed class DiscTitleCache
{
    private readonly string _connectionString;

    public DiscTitleCache(string databasePath)
    {
        _connectionString = SqliteFile.Prepare(
            databasePath,
            """
            CREATE TABLE IF NOT EXISTS disc_titles (
                canonical_disc_id TEXT NOT NULL PRIMARY KEY,
                title TEXT NOT NULL,
                found INTEGER NOT NULL
            );
            """);
    }

    public DiscTitleEntry? TryGet(string canonicalDiscId)
    {
        if (string.IsNullOrWhiteSpace(canonicalDiscId))
        {
            return null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT title, found
            FROM disc_titles
            WHERE canonical_disc_id = $disc;
            """;
        command.Parameters.AddWithValue("$disc", canonicalDiscId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var found = reader.GetInt32(1) != 0;
        var title = reader.GetString(0);
        return new DiscTitleEntry(canonicalDiscId, found ? title : null, found);
    }

    public void Store(DiscTitleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.CanonicalDiscId))
        {
            throw new ArgumentException("A disc ID is required.", nameof(entry));
        }

        SqliteFile.Upsert(
            _connectionString,
            """
            UPDATE disc_titles SET title = $title, found = $found
            WHERE canonical_disc_id = $disc;
            """,
            """
            INSERT INTO disc_titles (canonical_disc_id, title, found)
            VALUES ($disc, $title, $found);
            """,
            parameters =>
            {
                parameters.AddWithValue("$disc", entry.CanonicalDiscId);
                parameters.AddWithValue("$title", entry.Found ? entry.Title ?? string.Empty : string.Empty);
                parameters.AddWithValue("$found", entry.Found ? 1 : 0);
            });
    }

    private SqliteConnection Open() => SqliteFile.Open(_connectionString);
}
