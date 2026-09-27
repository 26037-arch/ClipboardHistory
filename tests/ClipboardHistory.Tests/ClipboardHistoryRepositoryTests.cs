using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Tests;

public sealed class ClipboardHistoryRepositoryTests
{
    [Fact]
    public async Task EmptyQuery_ReturnsNewestOneHundredRecords()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        await SeedAsync(databasePath, Enumerable.Range(1, 120).Select(index => $"value-{index}"));

        ClipboardHistoryRepository repository = new(databasePath);
        IReadOnlyList<ClipboardHistoryEntry> results = await repository.SearchAsync(string.Empty);

        Assert.Equal(100, results.Count);
        Assert.Equal(Enumerable.Range(21, 100).Reverse().Select(index => (long)index), results.Select(x => x.Id));
    }

    [Fact]
    public async Task Query_ReturnsOnlyMatchesInExactStartContainsOrder()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        await SeedAsync(databasePath, [
            "Hello",
            "hello begins here",
            "prefix hello suffix",
            "unrelated",
        ]);

        ClipboardHistoryRepository repository = new(databasePath);
        IReadOnlyList<ClipboardHistoryEntry> results = await repository.SearchAsync("HELLO");

        Assert.Equal([1L, 2L, 3L], results.Select(x => x.Id));
    }

    [Fact]
    public async Task Query_NormalizesKoreanEmojiCrlfAndTabs()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        await SeedAsync(databasePath, [
            "hello\r\nworld",
            "가나다\t😀 세계",
            "다른 값",
        ]);

        ClipboardHistoryRepository repository = new(databasePath);

        IReadOnlyList<ClipboardHistoryEntry> whitespace = await repository.SearchAsync("hello world");
        IReadOnlyList<ClipboardHistoryEntry> unicode = await repository.SearchAsync("가나다   😀");

        Assert.Single(whitespace);
        Assert.Equal("hello\r\nworld", whitespace[0].Content);
        Assert.Single(unicode);
        Assert.Equal("가나다\t😀 세계", unicode[0].Content);
    }

    [Fact]
    public async Task Query_TreatsSqlPatternAndQuoteCharactersLiterally()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        await SeedAsync(databasePath, [
            "진행률 100%",
            "value = a_b",
            "print('test')",
            "plain value",
        ]);

        ClipboardHistoryRepository repository = new(databasePath);

        Assert.Equal("진행률 100%", Assert.Single(await repository.SearchAsync("100%")).Content);
        Assert.Equal("value = a_b", Assert.Single(await repository.SearchAsync("a_b")).Content);
        Assert.Equal("print('test')", Assert.Single(await repository.SearchAsync("'test'")).Content);
    }

    [Fact]
    public async Task MatchesInSameRankAreNewestFirst()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        await SeedAsync(databasePath, ["x match one", "x match two", "x match three"]);

        ClipboardHistoryRepository repository = new(databasePath);
        IReadOnlyList<ClipboardHistoryEntry> results = await repository.SearchAsync("match");

        Assert.Equal([3L, 2L, 1L], results.Select(x => x.Id));
    }

    [Fact]
    public async Task BundledSqlite_SupportsMaterializedCteUsedBySearch()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string databasePath = Path.Combine(temporaryDirectory.Path, "clipboard.db");
        await SeedAsync(databasePath, ["materialized-check"]);

        ClipboardHistoryRepository repository = new(databasePath);
        ClipboardHistoryEntry result = Assert.Single(await repository.SearchAsync("materialized"));

        Assert.Equal("materialized-check", result.Content);
    }

    private static async Task SeedAsync(string databasePath, IEnumerable<string> contents)
    {
        await using SqliteConnection connection =
            await DatabaseInitializer.OpenInitializedConnectionAsync(databasePath, CancellationToken.None);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clipboard_history (content, content_hash, copied_at)
            VALUES ($content, $content_hash, $copied_at);
            """;
        command.Parameters.Add("$content", SqliteType.Text);
        command.Parameters.Add("$content_hash", SqliteType.Text);
        command.Parameters.Add("$copied_at", SqliteType.Text);

        int index = 0;
        foreach (string content in contents)
        {
            command.Parameters["$content"].Value = content;
            command.Parameters["$content_hash"].Value = DatabaseWriter.ComputeContentHash(content);
            command.Parameters["$copied_at"].Value = DateTimeOffset.UtcNow
                .AddSeconds(index++)
                .ToString("O", CultureInfo.InvariantCulture);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
