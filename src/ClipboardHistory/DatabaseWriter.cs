using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory;

internal sealed class DatabaseWriter
{
    public const int MutationQueueCapacity = 512;

    private const string LatestTextHashSql = """
        SELECT content_hash
        FROM clipboard_history
        WHERE record_type = 'text'
        ORDER BY id DESC
        LIMIT 1;
        """;

    private readonly ClipboardQueue _textQueue;
    private readonly string _databasePath;
    private readonly AppLogger _logger;
    private readonly bool _completeWithTextQueue;
    private readonly Channel<DatabaseMutation> _mutations;
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _textPumpTask;
    private int _completionStarted;

    public DatabaseWriter(
        ClipboardQueue queue,
        string databasePath,
        AppLogger logger,
        bool completeWithTextQueue = true)
    {
        _textQueue = queue;
        _databasePath = databasePath;
        _logger = logger;
        _completeWithTextQueue = completeWithTextQueue;
        _mutations = Channel.CreateBounded<DatabaseMutation>(new BoundedChannelOptions(
            MutationQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public Task Ready => _ready.Task;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool initialized = false;

        try
        {
            await using SqliteConnection connection =
                await DatabaseInitializer.OpenInitializedConnectionAsync(
                    _databasePath,
                    cancellationToken).ConfigureAwait(false);

            string? latestTextHash = await ReadLatestTextHashAsync(
                connection,
                cancellationToken).ConfigureAwait(false);

            initialized = true;
            _textPumpTask = PumpTextQueueAsync(cancellationToken);
            _ready.TrySetResult();

            await foreach (DatabaseMutation mutation in _mutations.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                try
                {
                    if (mutation.TextRecord is ClipboardRecord textRecord)
                    {
                        string contentHash = ComputeContentHash(textRecord.Content);
                        if (!string.Equals(contentHash, latestTextHash, StringComparison.Ordinal))
                        {
                            await InsertTextAsync(
                                connection,
                                textRecord,
                                contentHash,
                                cancellationToken).ConfigureAwait(false);
                            latestTextHash = contentHash;
                        }

                        mutation.TrySetResult(null);
                    }
                    else
                    {
                        object? result = await mutation.Operation!(connection, cancellationToken)
                            .ConfigureAwait(false);
                        mutation.TrySetResult(result);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    mutation.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception exception)
                {
                    mutation.TrySetException(exception);
                    if (mutation.Completion is null)
                    {
                        _logger.LogUnhandledException("database_mutation", exception);
                    }
                }
            }

            if (_textPumpTask is not null)
            {
                await _textPumpTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!initialized)
            {
                _ready.TrySetCanceled(cancellationToken);
            }

            CancelPendingMutations(cancellationToken);
        }
        catch (Exception exception)
        {
            if (!initialized)
            {
                _ready.TrySetException(exception);
            }
            else
            {
                _logger.LogUnhandledException("database_writer", exception);
            }

            FailPendingMutations(exception);
        }
        finally
        {
            if (!initialized && !_ready.Task.IsCompleted)
            {
                _ready.TrySetException(new InvalidOperationException(
                    "The database writer stopped before initialization completed."));
            }
        }
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completionStarted, 1) != 0)
        {
            return;
        }

        Task? textPump = _textPumpTask;
        if (textPump is not null)
        {
            await textPump.ConfigureAwait(false);
        }

        _mutations.Writer.TryComplete();
    }

    public Task<long> InsertImageAsync(
        StoredImageRecord image,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<long>(async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clipboard_history
                (
                    content, content_hash, copied_at, record_type,
                    image_sha256, image_relative_path, image_bytes,
                    image_width, image_height, ocr_status
                )
                VALUES
                (
                    '', $hash, $copied_at, 'image',
                    $hash, $relative_path, $bytes,
                    $width, $height, $ocr_status
                );
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$hash", image.Sha256);
            command.Parameters.AddWithValue("$copied_at", FormatUtc(image.CopiedAt));
            command.Parameters.AddWithValue("$relative_path", image.RelativePath);
            command.Parameters.AddWithValue("$bytes", image.ByteLength);
            command.Parameters.AddWithValue("$width", image.Width);
            command.Parameters.AddWithValue("$height", image.Height);
            command.Parameters.AddWithValue("$ocr_status", image.InitialOcrStatus);
            object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task UpdateOcrAsync(
        long recordId,
        string status,
        string? text,
        string? errorCode,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE clipboard_history
                SET ocr_status = $status,
                    ocr_text = $ocr_text,
                    ocr_error_code = $error_code
                WHERE id = $id AND record_type = 'image';
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$ocr_text", (object?)text ?? DBNull.Value);
            command.Parameters.AddWithValue("$error_code", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", recordId);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task ResetInterruptedOcrAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE clipboard_history
                SET ocr_status = 'pending', ocr_error_code = NULL
                WHERE record_type = 'image' AND ocr_status = 'processing';
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task<long> SoftDeleteRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<long>((connection, token) => SoftDeleteWhereAsync(
            connection,
            "copied_at >= $start AND copied_at < $end",
            command =>
            {
                command.Parameters.AddWithValue("$start", FormatUtc(startUtc));
                command.Parameters.AddWithValue("$end", FormatUtc(endUtc));
            },
            deletedAtUtc,
            token), cancellationToken);

    public Task<long> SoftDeleteOlderThanAsync(
        DateTimeOffset cutoffUtc,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<long>((connection, token) => SoftDeleteWhereAsync(
            connection,
            "copied_at < $cutoff",
            command => command.Parameters.AddWithValue("$cutoff", FormatUtc(cutoffUtc)),
            deletedAtUtc,
            token), cancellationToken);

    public Task<long> SoftDeleteIdsAsync(
        IReadOnlyCollection<long> recordIds,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteForIdsAsync(recordIds, """
            UPDATE clipboard_history
            SET deleted_at_utc = $timestamp
            WHERE deleted_at_utc IS NULL AND id IN ({0});
            """, deletedAtUtc, cancellationToken);

    public Task<long> RestoreIdsAsync(
        IReadOnlyCollection<long> recordIds,
        CancellationToken cancellationToken = default) =>
        ExecuteForIdsAsync(recordIds, """
            UPDATE clipboard_history
            SET deleted_at_utc = NULL
            WHERE deleted_at_utc IS NOT NULL AND id IN ({0});
            """, null, cancellationToken);

    public Task SaveSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (connection, token) =>
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await UpsertSettingAsync(
                connection,
                transaction,
                "automatic_deletion_enabled",
                settings.AutomaticDeletionEnabled ? "true" : "false",
                token).ConfigureAwait(false);
            await UpsertSettingAsync(
                connection,
                transaction,
                "retention_period_months",
                ((int)settings.RetentionPeriod).ToString(CultureInfo.InvariantCulture),
                token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task<IReadOnlyList<ImageFileReference>> PermanentlyDeleteExpiredAsync(
        DateTimeOffset deletedBeforeUtc,
        CancellationToken cancellationToken = default) =>
        PermanentlyDeleteAsync("deleted_at_utc IS NOT NULL AND deleted_at_utc <= $cutoff", command =>
        {
            command.Parameters.AddWithValue("$cutoff", FormatUtc(deletedBeforeUtc));
        }, cancellationToken);

    public Task<IReadOnlyList<ImageFileReference>> EmptyTrashAsync(
        CancellationToken cancellationToken = default) =>
        PermanentlyDeleteAsync("deleted_at_utc IS NOT NULL", _ => { }, cancellationToken);

    public Task MarkOrphanAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO orphan_image_files(relative_path, discovered_at_utc)
                VALUES ($path, $timestamp)
                ON CONFLICT(relative_path) DO UPDATE SET discovered_at_utc = excluded.discovered_at_utc;
                """;
            command.Parameters.AddWithValue("$path", relativePath);
            command.Parameters.AddWithValue("$timestamp", FormatUtc(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task ClearOrphanAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM orphan_image_files WHERE relative_path = $path;";
            command.Parameters.AddWithValue("$path", relativePath);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    internal static string ComputeContentHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ComputeHash(Encoding.UTF8.GetBytes(content));
    }

    internal static string ComputeHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task PumpTextQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ClipboardRecord record in _textQueue.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await _mutations.Writer.WriteAsync(
                    DatabaseMutation.ForText(record),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_completeWithTextQueue)
            {
                _mutations.Writer.TryComplete();
            }
        }
    }

    private async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<object?> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DatabaseMutation mutation = DatabaseMutation.ForOperation(
            async (connection, token) => await operation(connection, token).ConfigureAwait(false),
            completion);

        await _mutations.Writer.WriteAsync(mutation, cancellationToken).ConfigureAwait(false);
        object? result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (T)result!;
    }

    private Task<long> ExecuteForIdsAsync(
        IReadOnlyCollection<long> recordIds,
        string sqlTemplate,
        DateTimeOffset? timestamp,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return Task.FromResult(0L);
        }

        return ExecuteAsync<long>(async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();
            List<string> names = [];
            int index = 0;
            foreach (long id in recordIds.Distinct())
            {
                string name = $"$id{index++}";
                names.Add(name);
                command.Parameters.AddWithValue(name, id);
            }

            command.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                sqlTemplate,
                string.Join(",", names));
            if (timestamp.HasValue)
            {
                command.Parameters.AddWithValue("$timestamp", FormatUtc(timestamp.Value));
            }

            return (long)await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ImageFileReference>> PermanentlyDeleteAsync(
        string whereClause,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<ImageFileReference>>(async (connection, token) =>
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();
            List<ImageFileReference> candidates = [];

            await using (SqliteCommand select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"""
                    SELECT DISTINCT image_sha256, image_relative_path
                    FROM clipboard_history
                    WHERE {whereClause}
                      AND record_type = 'image'
                      AND image_relative_path IS NOT NULL;
                    """;
                addParameters(select);
                await using SqliteDataReader reader = await select
                    .ExecuteReaderAsync(token)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    candidates.Add(new ImageFileReference(
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.GetString(1)));
                }
            }

            await using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM clipboard_history WHERE {whereClause};";
                addParameters(delete);
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            List<ImageFileReference> unreferenced = [];
            foreach (ImageFileReference candidate in candidates)
            {
                await using SqliteCommand references = connection.CreateCommand();
                references.Transaction = transaction;
                references.CommandText = """
                    SELECT COUNT(*)
                    FROM clipboard_history
                    WHERE image_relative_path = $path
                       OR ($hash <> '' AND image_sha256 = $hash);
                    """;
                references.Parameters.AddWithValue("$path", candidate.RelativePath);
                references.Parameters.AddWithValue("$hash", candidate.Sha256);
                long count = (long)(await references.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                if (count == 0)
                {
                    unreferenced.Add(candidate);
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return unreferenced;
        }, cancellationToken);

    private static async Task<long> SoftDeleteWhereAsync(
        SqliteConnection connection,
        string whereClause,
        Action<SqliteCommand> addParameters,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE clipboard_history
            SET deleted_at_utc = $deleted_at
            WHERE deleted_at_utc IS NULL AND {whereClause};
            """;
        command.Parameters.AddWithValue("$deleted_at", FormatUtc(deletedAtUtc));
        addParameters(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings(setting_key, setting_value)
            VALUES ($key, $value)
            ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLatestTextHashAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = LatestTextHashSql;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task InsertTextAsync(
        SqliteConnection connection,
        ClipboardRecord record,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clipboard_history
                (content, content_hash, copied_at, record_type)
            VALUES ($content, $content_hash, $copied_at, 'text');
            """;
        command.Parameters.AddWithValue("$content", record.Content);
        command.Parameters.AddWithValue("$content_hash", contentHash);
        command.Parameters.AddWithValue("$copied_at", FormatUtc(record.CopiedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatUtc(DateTimeOffset value) => value
        .ToUniversalTime()
        .ToString("O", CultureInfo.InvariantCulture);

    private void CancelPendingMutations(CancellationToken cancellationToken)
    {
        while (_mutations.Reader.TryRead(out DatabaseMutation? mutation))
        {
            mutation.TrySetCanceled(cancellationToken);
        }
    }

    private void FailPendingMutations(Exception exception)
    {
        while (_mutations.Reader.TryRead(out DatabaseMutation? mutation))
        {
            mutation.TrySetException(exception);
        }
    }

    private sealed class DatabaseMutation
    {
        private DatabaseMutation(
            ClipboardRecord? textRecord,
            Func<SqliteConnection, CancellationToken, Task<object?>>? operation,
            TaskCompletionSource<object?>? completion)
        {
            TextRecord = textRecord;
            Operation = operation;
            Completion = completion;
        }

        public ClipboardRecord? TextRecord { get; }

        public Func<SqliteConnection, CancellationToken, Task<object?>>? Operation { get; }

        public TaskCompletionSource<object?>? Completion { get; }

        public static DatabaseMutation ForText(ClipboardRecord record) => new(record, null, null);

        public static DatabaseMutation ForOperation(
            Func<SqliteConnection, CancellationToken, Task<object?>> operation,
            TaskCompletionSource<object?> completion) => new(null, operation, completion);

        public void TrySetResult(object? result) => Completion?.TrySetResult(result);

        public void TrySetException(Exception exception) => Completion?.TrySetException(exception);

        public void TrySetCanceled(CancellationToken cancellationToken) =>
            Completion?.TrySetCanceled(cancellationToken);
    }
}
