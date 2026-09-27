using System.Drawing;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory.Tests;

public sealed class HistoryManagementTests
{
    [Fact]
    public async Task Search_FindsKoreanAndEnglishOcrWithWhitespaceNormalization()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        long id = await InsertImageAsync(host, DateTimeOffset.UtcNow, 1234);
        await host.Writer.UpdateOcrAsync(
            id,
            OcrStatus.Completed,
            "한글\r\nEnglish   Text",
            null);

        Assert.Equal(id, Assert.Single(await host.Repository.SearchAsync("한글 english")).Id);
        Assert.Equal(id, Assert.Single(await host.Repository.SearchAsync("ENGLISH text")).Id);
    }

    [Fact]
    public async Task Search_ExcludesTrashAndRestoreReturnsRecordToOriginalDate()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        DateTimeOffset copiedAt = new(2026, 8, 9, 2, 3, 4, TimeSpan.Zero);
        long id = await InsertImageAsync(host, copiedAt, 10);
        await host.Writer.UpdateOcrAsync(id, OcrStatus.Completed, "복원 검색", null);

        await host.Writer.SoftDeleteIdsAsync([id], DateTimeOffset.UtcNow);
        Assert.Empty(await host.Repository.SearchAsync("복원"));

        await host.Writer.RestoreIdsAsync([id]);
        ClipboardHistoryEntry restored = Assert.Single(await host.Repository.SearchAsync("복원"));
        Assert.Equal(copiedAt, restored.CopiedAt);
        Assert.Null(restored.DeletedAtUtc);
    }

    [Fact]
    public void ImageSuppression_MatchesOnceRejectsDifferentHashAndExpires()
    {
        ImageClipboardSelfWriteSuppressor suppressor = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string first = new('a', 64);
        string second = new('b', 64);

        suppressor.ArmHash(first, now);
        Assert.False(suppressor.TryConsumeHash(second, now.AddMilliseconds(1)));
        Assert.False(suppressor.TryConsumeHash(first, now.AddMilliseconds(2)));

        suppressor.ArmHash(first, now, TimeSpan.FromMilliseconds(50));
        Assert.False(suppressor.TryConsumeHash(first, now.AddMilliseconds(50)));

        suppressor.ArmHash(first, now);
        Assert.True(suppressor.TryConsumeHash(first, now.AddMilliseconds(1)));
        Assert.False(suppressor.TryConsumeHash(first, now.AddMilliseconds(2)));
    }

    [Fact]
    public async Task ImageRestore_LoadsOriginalPngAndArmsOneSuppressionToken()
    {
        using TemporaryDirectory temporary = new();
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        using Bitmap source = new(9, 7);
        source.SetPixel(4, 3, Color.LimeGreen);
        StoredImageArtifact artifact = await store.SaveAsync(source);
        ImageClipboardSelfWriteSuppressor imageSuppressor = new();
        CapturingClipboardWriter clipboard = new();
        ClipboardRestoreService restore = new(
            store,
            new ClipboardSelfWriteSuppressor(),
            imageSuppressor,
            clipboard);
        ClipboardHistoryEntry entry = CreateImageEntry(1, artifact, DateTimeOffset.UtcNow);

        await restore.RestoreAsync(entry);

        Assert.NotNull(clipboard.Image);
        Assert.Equal(source.Size, clipboard.Image!.Size);
        Assert.Equal(source.GetPixel(4, 3).ToArgb(), clipboard.Image.GetPixel(4, 3).ToArgb());
        Assert.True(imageSuppressor.TryConsumeHash(artifact.Sha256, DateTimeOffset.UtcNow));
        Assert.False(imageSuppressor.TryConsumeHash(artifact.Sha256, DateTimeOffset.UtcNow));
        clipboard.Dispose();
    }

    [Fact]
    public void DateRanges_UseLocalCalendarBoundariesAndUtcParameters()
    {
        TimeZoneInfo korea = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Korea",
            TimeSpan.FromHours(9),
            "Korea",
            "Korea");

        HistoryDateRange day = HistoryDateRangeCalculator.ForDay(2026, 8, 9, korea);
        HistoryDateRange month = HistoryDateRangeCalculator.ForMonth(2026, 8, korea);

        Assert.Equal(new DateTimeOffset(2026, 8, 8, 15, 0, 0, TimeSpan.Zero), day.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 15, 0, 0, TimeSpan.Zero), day.EndUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 15, 0, 0, TimeSpan.Zero), month.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.Zero), month.EndUtc);
    }

    [Fact]
    public async Task DateGroups_ReportNewestFirstCountsAndImageBytes()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        await InsertImageAsync(host, new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero), 100);
        await InsertImageAsync(host, new DateTimeOffset(2026, 8, 9, 11, 0, 0, TimeSpan.Zero), 200);
        await InsertImageAsync(host, new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero), 300);

        IReadOnlyList<HistoryGroupSummary> years = await host.Repository.GetYearGroupsAsync(utc);
        IReadOnlyList<HistoryGroupSummary> months = await host.Repository.GetMonthGroupsAsync(2026, utc);
        IReadOnlyList<HistoryGroupSummary> days = await host.Repository.GetDayGroupsAsync(2026, 8, utc);

        Assert.Equal([2026, 2025], years.Select(group => group.Value));
        Assert.Equal(2, years[0].RecordCount);
        Assert.Equal(300, years[0].ImageBytes);
        Assert.Equal(8, Assert.Single(months).Value);
        Assert.Equal(9, Assert.Single(days).Value);
        Assert.Equal(2, days[0].ImageCount);
    }

    [Fact]
    public async Task SoftDeleteSupportsDayMonthYearRangesAndRestores()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        long target = await InsertImageAsync(
            host,
            new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
            100);
        long other = await InsertImageAsync(
            host,
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            100);
        HistoryDateRange day = HistoryDateRangeCalculator.ForDay(2026, 8, 9, TimeZoneInfo.Utc);

        Assert.Equal(1, await host.Writer.SoftDeleteRangeAsync(
            day.StartUtc,
            day.EndUtc,
            DateTimeOffset.UtcNow));
        Assert.Equal(target, Assert.Single(await host.Repository.GetTrashAsync(0)).Id);
        Assert.Equal(other, Assert.Single(await host.Repository.GetActiveRecordsAsync(
            HistoryDateRangeCalculator.ForDay(2026, 8, 10, TimeZoneInfo.Utc),
            0)).Id);

        await host.Writer.RestoreIdsAsync([target]);
        Assert.Empty(await host.Repository.GetTrashAsync(0));
        Assert.Equal(target, Assert.Single(await host.Repository.GetActiveRecordsAsync(day, 0)).Id);
    }

    [Fact]
    public async Task TrashKeepsRecordsBeforeSevenDaysAndPurgesAfterBoundary()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        long id = await InsertImageAsync(host, DateTimeOffset.UtcNow.AddMonths(-2), 100);
        DateTimeOffset deletedAt = DateTimeOffset.UtcNow.AddDays(-6);
        await host.Writer.SoftDeleteIdsAsync([id], deletedAt);

        Assert.Empty(await host.Writer.PermanentlyDeleteExpiredAsync(
            DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Single(await host.Repository.GetTrashAsync(0));

        Assert.Single(await host.Writer.PermanentlyDeleteExpiredAsync(DateTimeOffset.UtcNow));
        Assert.Empty(await host.Repository.GetTrashAsync(0));
    }

    [Fact]
    public void RetentionCutoff_UsesAddMonthsAndAddYearsIncludingLeapDay()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        DateTimeOffset march31 = new(2024, 3, 31, 12, 30, 0, TimeSpan.Zero);
        DateTimeOffset leapDay = new(2024, 2, 29, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2024, 2, 29, 12, 30, 0, TimeSpan.Zero),
            HistoryDateRangeCalculator.CalculateRetentionCutoffUtc(
                march31,
                utc,
                RetentionPeriod.OneMonth));
        Assert.Equal(new DateTimeOffset(2023, 12, 31, 12, 30, 0, TimeSpan.Zero),
            HistoryDateRangeCalculator.CalculateRetentionCutoffUtc(
                march31,
                utc,
                RetentionPeriod.ThreeMonths));
        Assert.Equal(new DateTimeOffset(2023, 2, 28, 8, 0, 0, TimeSpan.Zero),
            HistoryDateRangeCalculator.CalculateRetentionCutoffUtc(
                leapDay,
                utc,
                RetentionPeriod.OneYear));
    }

    [Fact]
    public async Task Settings_DefaultToDisabledOneYearAndPersistChanges()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        AppSettingsRepository settings = new(host.DatabasePath, host.Writer);

        Assert.Equal(AppSettings.Default, await settings.LoadAsync());

        AppSettings changed = new(true, RetentionPeriod.ThreeMonths);
        await settings.SaveAsync(changed);
        Assert.Equal(changed, await settings.LoadAsync());
    }

    [Fact]
    public async Task AutomaticDeletionMovesOnlyOlderActiveRecordsToTrash()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMonths(-1);
        long older = await InsertImageAsync(host, cutoff.AddTicks(-1), 100);
        long boundary = await InsertImageAsync(host, cutoff, 100);

        await host.Writer.SoftDeleteOlderThanAsync(cutoff, DateTimeOffset.UtcNow);

        Assert.Equal(older, Assert.Single(await host.Repository.GetTrashAsync(0)).Id);
        await using SqliteConnection connection = new($"Data Source={host.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT deleted_at_utc FROM clipboard_history WHERE id=$id;";
        command.Parameters.AddWithValue("$id", boundary);
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task StorageUsageCountsDbActiveTrashAndActualManagedImageFiles()
    {
        using TemporaryDirectory temporary = new();
        await using DatabaseTestHost host = await DatabaseTestHost.CreateAsync(temporary.Path);
        ClipboardImageStore store = new(Path.Combine(temporary.Path, "images"));
        using Bitmap firstBitmap = new(8, 8);
        using Bitmap secondBitmap = new(9, 9);
        StoredImageArtifact first = await store.SaveAsync(firstBitmap);
        StoredImageArtifact second = await store.SaveAsync(secondBitmap);
        long active = await host.Writer.InsertImageAsync(CreateRecord(first, DateTimeOffset.UtcNow));
        long trash = await host.Writer.InsertImageAsync(CreateRecord(second, DateTimeOffset.UtcNow));
        await host.Writer.SoftDeleteIdsAsync([trash], DateTimeOffset.UtcNow);
        StorageUsageService usageService = new(host.DatabasePath, store);

        StorageUsage usage = await usageService.CalculateAsync(force: true);

        Assert.True(usage.DatabaseBytes > 0);
        Assert.Equal(first.ByteLength, usage.ActiveImageBytes);
        Assert.Equal(second.ByteLength, usage.TrashImageBytes);
        Assert.Equal(2, usage.TotalRecordCount);
        Assert.True(usage.TotalBytes >= usage.DatabaseBytes + first.ByteLength + second.ByteLength);
        Assert.NotEqual(active, trash);
    }

    private static async Task<long> InsertImageAsync(
        DatabaseTestHost host,
        DateTimeOffset copiedAt,
        long bytes)
    {
        string hash = Guid.NewGuid().ToString("N").PadRight(64, 'a');
        return await host.Writer.InsertImageAsync(new StoredImageRecord(
            hash,
            $"{hash}.png",
            bytes,
            10,
            10,
            copiedAt,
            OcrStatus.Pending));
    }

    private static StoredImageRecord CreateRecord(
        StoredImageArtifact artifact,
        DateTimeOffset copiedAt) => new(
        artifact.Sha256,
        artifact.RelativePath,
        artifact.ByteLength,
        artifact.Width,
        artifact.Height,
        copiedAt,
        OcrStatus.Unavailable);

    private static ClipboardHistoryEntry CreateImageEntry(
        long id,
        StoredImageArtifact artifact,
        DateTimeOffset copiedAt) => new(
        id,
        copiedAt,
        ClipboardRecordType.Image,
        string.Empty,
        artifact.Sha256,
        artifact.RelativePath,
        artifact.ByteLength,
        artifact.Width,
        artifact.Height,
        null,
        OcrStatus.Completed,
        null,
        null);

    private sealed class CapturingClipboardWriter : IClipboardWriter, IDisposable
    {
        public Bitmap? Image { get; private set; }

        public string? Text { get; private set; }

        public void SetText(string text) => Text = text;

        public void SetImage(Image image)
        {
            Image?.Dispose();
            Image = new Bitmap(image);
        }

        public void Dispose() => Image?.Dispose();
    }
}
