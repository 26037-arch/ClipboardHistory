namespace ClipboardHistory;

internal static class HistoryDateRangeCalculator
{
    public static HistoryDateRange ForYear(int year, TimeZoneInfo timeZone)
    {
        DateTime start = new(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return Create(HistoryGroupLevel.Year, start, start.AddYears(1), $"{year}년", timeZone);
    }

    public static HistoryDateRange ForMonth(int year, int month, TimeZoneInfo timeZone)
    {
        DateTime start = new(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return Create(
            HistoryGroupLevel.Month,
            start,
            start.AddMonths(1),
            $"{year}년 {month}월",
            timeZone);
    }

    public static HistoryDateRange ForDay(int year, int month, int day, TimeZoneInfo timeZone)
    {
        DateTime start = new(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return Create(
            HistoryGroupLevel.Day,
            start,
            start.AddDays(1),
            $"{year}년 {month}월 {day}일",
            timeZone);
    }

    public static DateTimeOffset CalculateRetentionCutoffUtc(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        RetentionPeriod period)
    {
        DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
        DateTime localCutoff = period switch
        {
            RetentionPeriod.OneMonth => localNow.AddMonths(-1),
            RetentionPeriod.ThreeMonths => localNow.AddMonths(-3),
            RetentionPeriod.OneYear => localNow.AddYears(-1),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };
        return ToUtc(localCutoff, timeZone);
    }

    private static HistoryDateRange Create(
        HistoryGroupLevel level,
        DateTime localStart,
        DateTime localEnd,
        string displayName,
        TimeZoneInfo timeZone) => new(
        level,
        ToUtc(localStart, timeZone),
        ToUtc(localEnd, timeZone),
        displayName);

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        TimeSpan offset = timeZone.GetUtcOffset(unspecified);
        if (timeZone.IsAmbiguousTime(unspecified))
        {
            offset = timeZone.GetAmbiguousTimeOffsets(unspecified).Max();
        }

        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
