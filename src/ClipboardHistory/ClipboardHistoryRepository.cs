using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory;

internal sealed record ClipboardHistoryEntry(
    long Id,
    DateTimeOffset CopiedAt,
    ClipboardRecordType RecordType,
    string Content,
    string? ImageSha256,
    string? ImageRelativePath,
    long ImageBytes,
    int ImageWidth,
    int ImageHeight,
    string? OcrText,
    string? OcrStatus,
    string? OcrErrorCode,
    DateTimeOffset? DeletedAtUtc)
{
    public string Preview => RecordType == ClipboardRecordType.Text
        ? ClipboardSearchText.CreatePreview(Content)
        : OcrStatus switch
        {
            ClipboardHistory.OcrStatus.Pending or ClipboardHistory.OcrStatus.Processing =>
                "텍스트 인식 중…",
            ClipboardHistory.OcrStatus.Failed => "OCR 실패",
            ClipboardHistory.OcrStatus.Unavailable => "OCR 사용 불가",
            _ when string.IsNullOrWhiteSpace(OcrText) => "인식된 텍스트 없음",
            _ => ClipboardSearchText.CreatePreview(OcrText),
        };
}

internal sealed class ClipboardHistoryRepository
{
    public const int MaximumResults = 100;
    public const int HistoryPageSize = 100;

    private const string SelectColumns = """
        id, copied_at, record_type, content,
        image_sha256, image_relative_path, COALESCE(image_bytes, 0) AS image_bytes,
        COALESCE(image_width, 0) AS image_width, COALESCE(image_height, 0) AS image_height,
        ocr_text, ocr_status, ocr_error_code, deleted_at_utc
        """;

    private readonly string _databasePath;

    public ClipboardHistoryRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<ClipboardHistoryEntry>> SearchAsync(
        string? query,
        int limit = MaximumResults,
        CancellationToken cancellationToken = default)
    {
        int effectiveLimit = Math.Clamp(limit, 1, MaximumResults);
        string normalizedQuery = ClipboardSearchText.NormalizeForSearch(query);

        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        connection.CreateFunction<string?, string>(
            "normalize_search",
            value => ClipboardSearchText.NormalizeForSearch(value ?? string.Empty),
            isDeterministic: true);

        await using SqliteCommand command = connection.CreateCommand();
        if (normalizedQuery.Length == 0)
        {
            command.CommandText = $"""
                SELECT {SelectColumns}
                FROM clipboard_history
                WHERE deleted_at_utc IS NULL
                ORDER BY id DESC
                LIMIT $limit;
                """;
        }
        else
        {
            command.CommandText = $"""
                WITH searchable AS MATERIALIZED
                (
                    SELECT
                        {SelectColumns},
                        normalize_search(
                            CASE record_type
                                WHEN 'image' THEN COALESCE(ocr_text, '')
                                ELSE content
                            END) AS normalized_content
                    FROM clipboard_history
                    WHERE deleted_at_utc IS NULL
                )
                SELECT {SelectColumns}
                FROM searchable
                WHERE instr(normalized_content, $query) > 0
                ORDER BY
                    CASE
                        WHEN normalized_content = $query THEN 0
                        WHEN instr(normalized_content, $query) = 1 THEN 1
                        ELSE 2
                    END,
                    id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", normalizedQuery);
        }

        command.Parameters.AddWithValue("$limit", effectiveLimit);
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingOcrRecord>> GetPendingOcrAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, image_relative_path
            FROM clipboard_history
            WHERE record_type = 'image'
              AND ocr_status IN ('pending', 'processing')
              AND image_relative_path IS NOT NULL
            ORDER BY id;
            """;

        List<PendingOcrRecord> records = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new PendingOcrRecord(reader.GetInt64(0), reader.GetString(1)));
        }

        return records;
    }

    public async Task<bool> HasImageReferenceAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM clipboard_history
                WHERE image_relative_path = $path
            );
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) != 0;
    }

    public async Task<HashSet<string>> GetReferencedImagePathsAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT image_relative_path
            FROM clipboard_history
            WHERE record_type = 'image' AND image_relative_path IS NOT NULL;
            """;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public async Task<IReadOnlyList<HistoryGroupSummary>> GetYearGroupsAsync(
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        (DateTimeOffset? minimum, DateTimeOffset? maximum) = await GetActiveBoundsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (minimum is null || maximum is null)
        {
            return [];
        }

        int firstYear = TimeZoneInfo.ConvertTime(minimum.Value, timeZone).Year;
        int lastYear = TimeZoneInfo.ConvertTime(maximum.Value, timeZone).Year;
        List<HistoryGroupSummary> groups = [];
        for (int year = lastYear; year >= firstYear; year--)
        {
            HistoryDateRange range = HistoryDateRangeCalculator.ForYear(year, timeZone);
            HistoryGroupSummary summary = await GetSummaryAsync(
                range,
                year,
                $"{year}년",
                cancellationToken).ConfigureAwait(false);
            if (summary.RecordCount > 0)
            {
                groups.Add(summary);
            }
        }

        return groups;
    }

    public async Task<IReadOnlyList<HistoryGroupSummary>> GetMonthGroupsAsync(
        int year,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        List<HistoryGroupSummary> groups = [];
        for (int month = 12; month >= 1; month--)
        {
            HistoryDateRange range = HistoryDateRangeCalculator.ForMonth(year, month, timeZone);
            HistoryGroupSummary summary = await GetSummaryAsync(
                range,
                month,
                $"{month}월",
                cancellationToken).ConfigureAwait(false);
            if (summary.RecordCount > 0)
            {
                groups.Add(summary);
            }
        }

        return groups;
    }

    public async Task<IReadOnlyList<HistoryGroupSummary>> GetDayGroupsAsync(
        int year,
        int month,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        List<HistoryGroupSummary> groups = [];
        for (int day = DateTime.DaysInMonth(year, month); day >= 1; day--)
        {
            HistoryDateRange range = HistoryDateRangeCalculator.ForDay(year, month, day, timeZone);
            HistoryGroupSummary summary = await GetSummaryAsync(
                range,
                day,
                $"{day}일",
                cancellationToken).ConfigureAwait(false);
            if (summary.RecordCount > 0)
            {
                groups.Add(summary);
            }
        }

        return groups;
    }

    public async Task<IReadOnlyList<ClipboardHistoryEntry>> GetActiveRecordsAsync(
        HistoryDateRange range,
        int offset,
        int limit = HistoryPageSize,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM clipboard_history
            WHERE deleted_at_utc IS NULL
              AND copied_at >= $start AND copied_at < $end
            ORDER BY copied_at DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddRangeParameters(command, range);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, HistoryPageSize));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardHistoryEntry>> GetTrashAsync(
        int offset,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM clipboard_history
            WHERE deleted_at_utc IS NOT NULL
            ORDER BY deleted_at_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public Task<DeletionSummary> GetDeletionSummaryAsync(
        HistoryDateRange range,
        CancellationToken cancellationToken = default) =>
        GetDeletionSummaryWhereAsync(
            "deleted_at_utc IS NULL AND copied_at >= $start AND copied_at < $end",
            command => AddRangeParameters(command, range),
            range.DisplayName,
            cancellationToken);

    public Task<DeletionSummary> GetOlderThanSummaryAsync(
        DateTimeOffset cutoffUtc,
        string displayName,
        CancellationToken cancellationToken = default) =>
        GetDeletionSummaryWhereAsync(
            "deleted_at_utc IS NULL AND copied_at < $cutoff",
            command => command.Parameters.AddWithValue("$cutoff", FormatUtc(cutoffUtc)),
            displayName,
            cancellationToken);

    public Task<DeletionSummary> GetTrashSummaryAsync(
        CancellationToken cancellationToken = default) =>
        GetDeletionSummaryWhereAsync(
            "deleted_at_utc IS NOT NULL",
            _ => { },
            "휴지통 전체",
            cancellationToken);

    private async Task<HistoryGroupSummary> GetSummaryAsync(
        HistoryDateRange range,
        int value,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN record_type = 'image' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN record_type = 'image' THEN image_bytes ELSE 0 END), 0)
            FROM clipboard_history
            WHERE deleted_at_utc IS NULL
              AND copied_at >= $start AND copied_at < $end;
            """;
        AddRangeParameters(command, range);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new HistoryGroupSummary(
            value,
            displayName,
            range.StartUtc,
            range.EndUtc,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private async Task<(DateTimeOffset? Minimum, DateTimeOffset? Maximum)> GetActiveBoundsAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(copied_at), MAX(copied_at)
            FROM clipboard_history
            WHERE deleted_at_utc IS NULL;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return (null, null);
        }

        return (ParseDate(reader.GetString(0)), ParseDate(reader.GetString(1)));
    }

    private async Task<DeletionSummary> GetDeletionSummaryWhereAsync(
        string whereClause,
        Action<SqliteCommand> addParameters,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN record_type = 'image' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN record_type = 'image' THEN image_bytes ELSE 0 END), 0)
            FROM clipboard_history
            WHERE {whereClause};
            """;
        addParameters(command);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DeletionSummary(
            displayName,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 2,
        };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 2000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ClipboardHistoryEntry>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<ClipboardHistoryEntry> entries = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static ClipboardHistoryEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ParseDate(reader.GetString(1)),
        string.Equals(reader.GetString(2), "image", StringComparison.OrdinalIgnoreCase)
            ? ClipboardRecordType.Image
            : ClipboardRecordType.Text,
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt64(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : ParseDate(reader.GetString(12)));

    private static void AddRangeParameters(SqliteCommand command, HistoryDateRange range)
    {
        command.Parameters.AddWithValue("$start", FormatUtc(range.StartUtc));
        command.Parameters.AddWithValue("$end", FormatUtc(range.EndUtc));
    }

    private static string FormatUtc(DateTimeOffset value) => value
        .ToUniversalTime()
        .ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind,
        out DateTimeOffset result)
        ? result
        : DateTimeOffset.MinValue;
}
