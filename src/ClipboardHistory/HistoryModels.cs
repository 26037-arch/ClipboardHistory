namespace ClipboardHistory;

internal enum HistoryGroupLevel
{
    All,
    Year,
    Month,
    Day,
}

internal sealed record HistoryDateRange(
    HistoryGroupLevel Level,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string DisplayName);

internal sealed record HistoryGroupSummary(
    int Value,
    string DisplayName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long RecordCount,
    long ImageCount,
    long ImageBytes);

internal sealed record DeletionSummary(
    string PeriodDisplay,
    long RecordCount,
    long ImageCount,
    long ImageBytes);

internal sealed record StorageUsage(
    long TotalBytes,
    long ActiveImageBytes,
    long TrashImageBytes,
    long DatabaseBytes,
    long TotalRecordCount);

internal enum RetentionPeriod
{
    OneMonth = 1,
    ThreeMonths = 3,
    OneYear = 12,
}

internal sealed record AppSettings(bool AutomaticDeletionEnabled, RetentionPeriod RetentionPeriod)
{
    public static AppSettings Default { get; } = new(false, RetentionPeriod.OneYear);
}

internal static class ByteSizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
