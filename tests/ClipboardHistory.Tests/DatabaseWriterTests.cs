using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Tests;

public sealed class DatabaseWriterTests
{
    [Fact]
    public async Task Writer_PreservesTextAndSkipsOnlyConsecutiveDuplicates()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "data", "clipboard.db");
        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));
        ClipboardQueue queue = new(capacity: 16);
        DatabaseWriter writer = new(queue, databasePath, logger);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task writerTask = Task.Run(() => writer.RunAsync(cancellation.Token));
        await writer.Ready.WaitAsync(cancellation.Token);

        DateTimeOffset copiedAt = new(2026, 8, 9, 1, 2, 3, TimeSpan.Zero);
        const string first = "첫째 줄\r\n둘째 줄\t😀  ";
        const string second = "다른 텍스트";

        Assert.True(queue.TryEnqueue(new ClipboardRecord(first, copiedAt)));
        Assert.True(queue.TryEnqueue(new ClipboardRecord(first, copiedAt.AddMilliseconds(1))));
        Assert.True(queue.TryEnqueue(new ClipboardRecord(second, copiedAt.AddSeconds(1))));
        Assert.True(queue.TryEnqueue(new ClipboardRecord(first, copiedAt.AddSeconds(2))));
        queue.Complete();

        await writerTask.WaitAsync(cancellation.Token);

        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync(cancellation.Token);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT content, content_hash, copied_at FROM clipboard_history ORDER BY id;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellation.Token);

        List<(string Content, string Hash, string CopiedAt)> rows = [];
        while (await reader.ReadAsync(cancellation.Token))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(first, rows[0].Content);
        Assert.Equal(second, rows[1].Content);
        Assert.Equal(first, rows[2].Content);
        Assert.Equal(DatabaseWriter.ComputeContentHash(first), rows[0].Hash);
        Assert.Equal(copiedAt, DateTimeOffset.Parse(rows[0].CopiedAt));
        Assert.EndsWith("+00:00", rows[0].CopiedAt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initializer_CreatesMissingDatabaseAndEnablesWal()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "new", "nested", "clipboard.db");

        await using SqliteConnection connection =
            await DatabaseInitializer.OpenInitializedConnectionAsync(databasePath, CancellationToken.None);

        Assert.True(File.Exists(databasePath));

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        string? journalMode = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.Equal("wal", journalMode, ignoreCase: true);
    }

    [Fact]
    public async Task Writer_LoadsLatestHashAndDeduplicatesAcrossRestart()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));

        await RunWriterAsync(databasePath, logger, ["persisted value"]);
        await RunWriterAsync(databasePath, logger, ["persisted value"]);

        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clipboard_history;";

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Writer_PersistsRapidBurstWithinChannelCapacity()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));
        string[] records = Enumerable.Range(0, 100).Select(index => $"text-{index}").ToArray();

        await RunWriterAsync(databasePath, logger, records);

        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clipboard_history;";

        Assert.Equal(100L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Initializer_FailsWhenDatabaseDirectoryCannotBeCreated()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string blockingFile = Path.Combine(temporaryDirectory.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "blocking file");

        string databasePath = Path.Combine(blockingFile, "clipboard.db");
        await Assert.ThrowsAnyAsync<IOException>(
            () => DatabaseInitializer.OpenInitializedConnectionAsync(
                databasePath,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void ComputeContentHash_UsesUtf8Sha256(string content, string expected) =>
        Assert.Equal(expected, DatabaseWriter.ComputeContentHash(content));

    private static async Task RunWriterAsync(
        string databasePath,
        AppLogger logger,
        IReadOnlyCollection<string> contents)
    {
        ClipboardQueue queue = new(capacity: Math.Max(1, contents.Count));
        DatabaseWriter writer = new(queue, databasePath, logger);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task writerTask = Task.Run(() => writer.RunAsync(cancellation.Token));
        await writer.Ready.WaitAsync(cancellation.Token);

        foreach (string content in contents)
        {
            Assert.True(queue.TryEnqueue(new ClipboardRecord(content, DateTimeOffset.UtcNow)));
        }

        queue.Complete();
        await writerTask.WaitAsync(cancellation.Token);
    }
}
