using Microsoft.Data.Sqlite;

namespace ClipboardHistory;

internal static class DatabaseInitializer
{
    private const string BaseSchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS clipboard_history (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            content             TEXT NOT NULL,
            content_hash        TEXT NOT NULL,
            copied_at           TEXT NOT NULL,
            record_type         TEXT NOT NULL DEFAULT 'text',
            image_sha256        TEXT NULL,
            image_relative_path TEXT NULL,
            image_bytes         INTEGER NULL,
            image_width         INTEGER NULL,
            image_height        INTEGER NULL,
            ocr_text            TEXT NULL,
            ocr_status          TEXT NULL,
            ocr_error_code      TEXT NULL,
            deleted_at_utc      TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS app_settings (
            setting_key   TEXT PRIMARY KEY,
            setting_value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS orphan_image_files (
            relative_path    TEXT PRIMARY KEY,
            discovered_at_utc TEXT NOT NULL
        );
        """;

    private static readonly (string Name, string Definition)[] RequiredColumns =
    [
        ("record_type", "TEXT NOT NULL DEFAULT 'text'"),
        ("image_sha256", "TEXT NULL"),
        ("image_relative_path", "TEXT NULL"),
        ("image_bytes", "INTEGER NULL"),
        ("image_width", "INTEGER NULL"),
        ("image_height", "INTEGER NULL"),
        ("ocr_text", "TEXT NULL"),
        ("ocr_status", "TEXT NULL"),
        ("ocr_error_code", "TEXT NULL"),
        ("deleted_at_utc", "TEXT NULL"),
    ];

    private const string IndexSql = """
        CREATE INDEX IF NOT EXISTS idx_clipboard_history_copied_at
            ON clipboard_history(copied_at DESC);

        CREATE INDEX IF NOT EXISTS idx_clipboard_history_content_hash
            ON clipboard_history(content_hash);

        CREATE INDEX IF NOT EXISTS idx_clipboard_history_active_copied
            ON clipboard_history(deleted_at_utc, copied_at DESC);

        CREATE INDEX IF NOT EXISTS idx_clipboard_history_image_path
            ON clipboard_history(image_relative_path);

        CREATE INDEX IF NOT EXISTS idx_clipboard_history_ocr_status
            ON clipboard_history(record_type, ocr_status);
        """;

    public static async Task<SqliteConnection> OpenInitializedConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The database path must contain a directory.", nameof(databasePath));
        }

        Directory.CreateDirectory(directory);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        };

        SqliteConnection connection = new(builder.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, BaseSchemaSql, cancellationToken).ConfigureAwait(false);
            await AddMissingColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, IndexSql, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task AddMissingColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> existingColumns = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(clipboard_history);";
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach ((string name, string definition) in RequiredColumns)
        {
            if (existingColumns.Contains(name))
            {
                continue;
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE clipboard_history ADD COLUMN {name} {definition};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
