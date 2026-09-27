using System.Drawing;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Tests;

public sealed class ClipboardImageStoreTests
{
    [Fact]
    public async Task Save_ProducesReadablePngWithSha256FileName()
    {
        using TemporaryDirectory temporary = new();
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        using Bitmap bitmap = CreateBitmap();

        StoredImageArtifact artifact = await store.SaveAsync(bitmap);
        byte[] bytes = await File.ReadAllBytesAsync(artifact.FullPath);
        using Bitmap restored = await store.LoadBitmapAsync(artifact.RelativePath);

        Assert.Equal(DatabaseWriter.ComputeHash(bytes), artifact.Sha256);
        Assert.Equal($"{artifact.Sha256}.png", artifact.RelativePath);
        Assert.Equal(bytes.LongLength, artifact.ByteLength);
        Assert.Equal(bitmap.Size, restored.Size);
        Assert.Equal(bitmap.GetPixel(2, 3).ToArgb(), restored.GetPixel(2, 3).ToArgb());
    }

    [Fact]
    public async Task Save_ReusesIdenticalImageFile()
    {
        using TemporaryDirectory temporary = new();
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        using Bitmap bitmap = CreateBitmap();

        StoredImageArtifact first = await store.SaveAsync(bitmap);
        StoredImageArtifact second = await store.SaveAsync(bitmap);

        Assert.True(first.NewlyCreated);
        Assert.False(second.NewlyCreated);
        Assert.Equal(first.FullPath, second.FullPath);
        Assert.Single(Directory.GetFiles(store.RootPath, "*.png"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("..\\outside.png")]
    [InlineData("C:\\outside.png")]
    [InlineData("not-a-hash.png")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png\\x")]
    public void ResolveManagedPath_RejectsTraversalAndNonHashNames(string path)
    {
        using TemporaryDirectory temporary = new();
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));

        Assert.Throws<InvalidDataException>(() => store.ResolveManagedPath(path));
    }

    [Fact]
    public async Task DatabaseFailureCleanup_DeletesOnlyNewArtifact()
    {
        using TemporaryDirectory temporary = new();
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        using Bitmap bitmap = CreateBitmap();

        StoredImageArtifact created = await store.SaveAsync(bitmap);
        store.DeleteNewArtifactAfterDatabaseFailure(created);
        Assert.False(File.Exists(created.FullPath));

        StoredImageArtifact recreated = await store.SaveAsync(bitmap);
        StoredImageArtifact reused = await store.SaveAsync(bitmap);
        store.DeleteNewArtifactAfterDatabaseFailure(reused);
        Assert.True(File.Exists(recreated.FullPath));
    }

    [Fact]
    public async Task PermanentDeletion_KeepsSharedFileUntilLastReferenceIsRemoved()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        using Bitmap bitmap = CreateBitmap();
        StoredImageArtifact artifact = await store.SaveAsync(bitmap);
        DateTimeOffset copiedAt = DateTimeOffset.UtcNow.AddDays(-10);
        StoredImageRecord record = new(
            artifact.Sha256,
            artifact.RelativePath,
            artifact.ByteLength,
            artifact.Width,
            artifact.Height,
            copiedAt,
            OcrStatus.Unavailable);
        long first = await host.Writer.InsertImageAsync(record);
        long second = await host.Writer.InsertImageAsync(record with { CopiedAt = copiedAt.AddSeconds(1) });

        await host.Writer.SoftDeleteIdsAsync([first], DateTimeOffset.UtcNow.AddDays(-8));
        IReadOnlyList<ImageFileReference> firstPurge = await host.Writer
            .PermanentlyDeleteExpiredAsync(DateTimeOffset.UtcNow);
        Assert.Empty(firstPurge);
        Assert.True(File.Exists(artifact.FullPath));

        await host.Writer.SoftDeleteIdsAsync([second], DateTimeOffset.UtcNow.AddDays(-8));
        ImageFileReference last = Assert.Single(await host.Writer
            .PermanentlyDeleteExpiredAsync(DateTimeOffset.UtcNow));
        Assert.Equal(artifact.RelativePath, last.RelativePath);
        Assert.True(store.TryDelete(last.RelativePath));
        Assert.False(File.Exists(artifact.FullPath));
    }

    [Fact]
    public async Task Initializer_MigratesLegacyTableWithoutLosingTextRows()
    {
        using TemporaryDirectory temporary = new();
        string databasePath = Path.Combine(temporary.Path, "clipboard.db");
        await using (SqliteConnection legacy = new($"Data Source={databasePath}"))
        {
            await legacy.OpenAsync();
            await using SqliteCommand create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE clipboard_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    copied_at TEXT NOT NULL
                );
                INSERT INTO clipboard_history(content, content_hash, copied_at)
                VALUES ('기존 기록', 'hash', '2026-08-09T00:00:00.0000000+00:00');
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using SqliteConnection migrated = await DatabaseInitializer
            .OpenInitializedConnectionAsync(databasePath, CancellationToken.None);
        await using SqliteCommand command = migrated.CreateCommand();
        command.CommandText = """
            SELECT content, record_type, deleted_at_utc
            FROM clipboard_history;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("기존 기록", reader.GetString(0));
        Assert.Equal("text", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
    }

    private static Bitmap CreateBitmap()
    {
        Bitmap bitmap = new(8, 6);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.CornflowerBlue);
        bitmap.SetPixel(2, 3, Color.OrangeRed);
        return bitmap;
    }
}
