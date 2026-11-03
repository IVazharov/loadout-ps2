using Microsoft.Data.Sqlite;

namespace Loadout.Persistence;

/// <summary>
/// Connection setup shared by the stores that live in the app's one SQLite file.
/// Pooling is off because Windows keeps a pooled handle open, which blocks a later
/// reader of the same file. Every connection waits up to two seconds on a lock.
/// </summary>
internal static class SqliteFile
{
    /// <summary>
    /// Validates the path, creates its folder, runs <paramref name="schema"/>, and
    /// returns the connection string for <see cref="Open"/>.
    /// </summary>
    public static string Prepare(string databasePath, string schema)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString;
        using var connection = Open(connectionString);
        Execute(connection, schema);
        return connectionString;
    }

    public static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 2000;");
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs <paramref name="update"/>, and <paramref name="insert"/> when the update changed
    /// no row, in one immediate transaction. The SQLite in older Windows 10 builds predates
    /// <c>ON CONFLICT ... DO UPDATE</c> (3.24), so the stores do not use that syntax.
    /// </summary>
    public static void Upsert(string connectionString, string update, string insert, Action<SqliteParameterCollection> bind)
    {
        using var connection = Open(connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        bind(command.Parameters);
        command.CommandText = update;
        if (command.ExecuteNonQuery() == 0)
        {
            command.CommandText = insert;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Adds a column that an older file was created without. Returns true when the
    /// column was added now, so the caller can fill it for existing rows.
    /// </summary>
    public static bool AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        return true;
    }
}
