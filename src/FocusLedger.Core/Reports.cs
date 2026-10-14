namespace FocusLedger.Core;

public enum Period { Day, Week, Month, Year }
public sealed record PeriodRange(DateTimeOffset Start, DateTimeOffset End, string Label);
public sealed record SummaryBucket(string Label, DateTimeOffset Start, double Seconds);

public static class Reports
{
    public static PeriodRange Range(DateTime selected, Period period, TimeZoneInfo zone)
    {
        var day = selected.Date;
        DateTime start = period switch
        {
            Period.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            Period.Month => new(day.Year, day.Month, 1),
            Period.Year => new(day.Year, 1, 1),
            _ => day
        };
        var end = period switch { Period.Week => start.AddDays(7), Period.Month => start.AddMonths(1), Period.Year => start.AddYears(1), _ => start.AddDays(1) };
        return new(ToUtc(start, zone), ToUtc(end, zone), period switch
        {
            Period.Day => start.ToString("yyyy 年 M 月 d 日"),
            Period.Week => $"{start:yyyy.MM.dd} — {end.AddDays(-1):MM.dd}",
            Period.Month => start.ToString("yyyy 年 M 月"), _ => $"{start.Year} 年"
        });
    }

    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone));

    public static List<Segment> Clip(IEnumerable<Segment> source, DateTimeOffset start, DateTimeOffset end) =>
        source.Where(s => s.End > start && s.Start < end)
            .Select(s => s with { Start = s.Start < start ? start : s.Start, End = s.End > end ? end : s.End }).ToList();

    public static List<SummaryBucket> Buckets(IEnumerable<Segment> source, PeriodRange range, Period period, TimeZoneInfo zone)
    {
        var all = source.ToList();
        var cursor = TimeZoneInfo.ConvertTime(range.Start, zone).DateTime;
        List<SummaryBucket> result = [];
        while (ToUtc(cursor, zone) < range.End)
        {
            var next = period switch { Period.Day => cursor.AddHours(1), Period.Year => cursor.AddMonths(1), _ => cursor.AddDays(1) };
            var startUtc = ToUtc(cursor, zone);
            var endUtc = ToUtc(next, zone);
            var parts = Clip(all, startUtc, endUtc);
            result.Add(new(period switch { Period.Day => cursor.ToString("HH"), Period.Year => $"{cursor.Month}月", _ => cursor.ToString("MM/dd") },
                startUtc, parts.Where(s => s.IsEffective).Sum(s => s.Seconds)));
            cursor = next;
        }
        return result;
    }

    public static double LongestEffective(IEnumerable<Segment> source)
    {
        DateTimeOffset? start = null, end = null;
        double longest = 0;
        foreach (var segment in source.Where(s => s.IsEffective).OrderBy(s => s.Start))
        {
            if (end is null || segment.Start > end) start = segment.Start;
            end = end is not null && end > segment.End ? end : segment.End;
            longest = Math.Max(longest, (end.Value - start!.Value).TotalSeconds);
        }
        return longest;
    }
}
