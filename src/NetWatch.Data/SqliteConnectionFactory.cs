using Microsoft.Data.Sqlite;

namespace NetWatch.Data;

/// <summary>Builds SQLite connection strings with the settings every NetWatch database uses.</summary>
public static class SqliteConnectionFactory
{
    /// <summary>
    /// Connection string with shared cache disabled, a generous busy timeout (Microsoft.Data.Sqlite retries
    /// SQLITE_BUSY until the command timeout) and pooling enabled.
    /// </summary>
    public static string Build(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    /// <summary>
    /// Switches the database to WAL mode (persistent setting) and applies durability/performance pragmas.
    /// </summary>
    public static void ConfigureDatabaseFile(string path)
    {
        using var connection = new SqliteConnection(Build(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA wal_autocheckpoint = 4000;
            """;
        command.ExecuteNonQuery();
    }
}
