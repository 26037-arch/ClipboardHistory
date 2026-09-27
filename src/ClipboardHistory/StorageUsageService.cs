using Microsoft.Data.Sqlite;

namespace ClipboardHistory;

internal sealed class StorageUsageService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    private readonly string _databasePath;
    private readonly ClipboardImageStore _imageStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StorageUsage? _cached;
    private DateTimeOffset _cacheExpiresAt;

    public StorageUsageService(string databasePath, ClipboardImageStore imageStore)
    {
        _databasePath = databasePath;
        _imageStore = imageStore;
    }

    public void Invalidate() => _cacheExpiresAt = DateTimeOffset.MinValue;

    public async Task<StorageUsage> CalculateAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!force && _cached is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _cached is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cached;
            }

            StorageUsage usage = await Task.Run(
                () => CalculateCoreAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
            _cached = usage;
            _cacheExpiresAt = DateTimeOffset.UtcNow + CacheLifetime;
            return usage;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StorageUsage> CalculateCoreAsync(CancellationToken cancellationToken)
    {
        long databaseBytes = GetFileLength(_databasePath) +
                             GetFileLength(_databasePath + "-wal") +
                             GetFileLength(_databasePath + "-shm");
        long activeBytes = 0;
        long trashBytes = 0;
        long recordCount;

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        await using (SqliteConnection connection = new(builder.ToString()))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM clipboard_history;";
                recordCount = (long)(await count.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false))!;
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    image_relative_path,
                    MAX(COALESCE(image_bytes, 0)),
                    MAX(CASE WHEN deleted_at_utc IS NULL THEN 1 ELSE 0 END),
                    MAX(CASE WHEN deleted_at_utc IS NOT NULL THEN 1 ELSE 0 END)
                FROM clipboard_history
                WHERE record_type = 'image' AND image_relative_path IS NOT NULL
                GROUP BY image_relative_path;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = reader.GetString(0);
                long fallbackBytes = reader.GetInt64(1);
                long actualBytes = GetManagedFileLength(relativePath, fallbackBytes);
                bool active = reader.GetInt64(2) != 0;
                bool trash = reader.GetInt64(3) != 0;
                if (active)
                {
                    activeBytes += actualBytes;
                }
                else if (trash)
                {
                    trashBytes += actualBytes;
                }
            }
        }

        long allImageBytes = 0;
        if (Directory.Exists(_imageStore.RootPath))
        {
            foreach (string file in Directory.EnumerateFiles(
                         _imageStore.RootPath,
                         "*.png",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string relative = Path.GetFileName(file);
                    _ = _imageStore.ResolveManagedPath(relative);
                    allImageBytes += GetFileLength(file);
                }
                catch (InvalidDataException)
                {
                    // Only hash-named managed PNG files are part of app storage.
                }
            }
        }

        return new StorageUsage(
            databaseBytes + allImageBytes,
            activeBytes,
            trashBytes,
            databaseBytes,
            recordCount);
    }

    private long GetManagedFileLength(string relativePath, long fallback)
    {
        try
        {
            string fullPath = _imageStore.ResolveManagedPath(relativePath);
            return File.Exists(fullPath) ? GetFileLength(fullPath) : Math.Max(0, fallback);
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
