namespace Zen.Blazor.Beacons;

public sealed record BeaconEntry(
    Guid Id,
    string Key,
    DateTimeOffset ReceivedAt,
    string Method,
    string Path,
    string Query,
    string Referer,
    string? ContentType,
    string? Client,
    string? UserAgent,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string BodyEncoding,
    int Size);

public sealed record BeaconItem(
    Guid Id,
    string Key,
    DateTimeOffset ReceivedAt,
    string Referer,
    string? ContentType,
    int Size,
    string Preview);

public sealed record RefererCount(string Referer, long Count, DateTimeOffset LastAt);

public sealed record HourCount(DateTimeOffset Hour, long Count);

public sealed record KeyCount(string Key, long Count);

public sealed record BeaconTotals(
    long Total,
    long Bytes,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    IReadOnlyList<RefererCount> Referers,
    IReadOnlyList<HourCount> Hourly)
{
    public const int MaxReferers = 25;

    public static readonly BeaconTotals Empty = new(0, 0, null, null, [], []);

    public static BeaconTotals From(IEnumerable<BeaconEntry> entries, DateTimeOffset hourlySince)
    {
        var list = entries as IReadOnlyCollection<BeaconEntry> ?? entries.ToList();
        if (list.Count == 0)
        {
            return Empty;
        }

        var referers = list
            .GroupBy(e => e.Referer, StringComparer.Ordinal)
            .Select(g => new RefererCount(g.Key, g.LongCount(), g.Max(e => e.ReceivedAt)))
            .ToList();

        var hourly = list
            .Where(e => e.ReceivedAt >= hourlySince)
            .GroupBy(e => TruncateToHour(e.ReceivedAt))
            .Select(g => new HourCount(g.Key, g.LongCount()))
            .ToList();

        return new BeaconTotals(
            list.Count,
            list.Sum(e => (long)e.Size),
            list.Min(e => e.ReceivedAt),
            list.Max(e => e.ReceivedAt),
            referers,
            hourly);
    }

    public BeaconTotals Combine(BeaconTotals other)
    {
        if (other.Total == 0)
        {
            return this with { Referers = TopReferers(Referers), Hourly = [.. Hourly.OrderBy(h => h.Hour)] };
        }

        var referers = Referers.Concat(other.Referers)
            .GroupBy(r => r.Referer, StringComparer.Ordinal)
            .Select(g => new RefererCount(g.Key, g.Sum(r => r.Count), g.Max(r => r.LastAt)));

        var hourly = Hourly.Concat(other.Hourly)
            .GroupBy(h => h.Hour)
            .Select(g => new HourCount(g.Key, g.Sum(h => h.Count)))
            .OrderBy(h => h.Hour)
            .ToList();

        return new BeaconTotals(
            Total + other.Total,
            Bytes + other.Bytes,
            Min(FirstAt, other.FirstAt),
            Max(LastAt, other.LastAt),
            TopReferers(referers),
            hourly);
    }

    public static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static List<RefererCount> TopReferers(IEnumerable<RefererCount> referers) =>
        [.. referers.OrderByDescending(r => r.Count).ThenBy(r => r.Referer, StringComparer.Ordinal).Take(MaxReferers)];

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}

public sealed record PruneResult(long Removed, long StoredBytes, long DatabaseBytes);

public sealed record PruneReport(DateTimeOffset At, long Removed, long StoredBytes, long DatabaseBytes, long BudgetBytes);

public enum LookupStatus
{
    Found,
    NotFound,
    Unavailable,
}

public sealed record Lookup(LookupStatus Status, BeaconEntry? Entry);

public sealed record AcceptedBeacon(Guid Id, string Key, DateTimeOffset ReceivedAt, int Size, string Location);

public sealed record BeaconSummary(
    string Key,
    string? Referer,
    long Total,
    long Bytes,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    IReadOnlyList<RefererCount> Referers,
    IReadOnlyList<HourCount> Hourly,
    bool Degraded);

public sealed record BeaconPage(string? Key, string? Referer, IReadOnlyList<BeaconItem> Items, Guid? Next, bool Degraded);

public sealed record BeaconKeys(IReadOnlyList<KeyCount> Keys, bool Degraded);

public sealed record StorageStatus(
    string Database,
    string? LastError,
    DateTimeOffset? RetryAt,
    DateTimeOffset? LastSuccessAt,
    int MemoryEntries,
    long MemoryBytes,
    int Unsaved,
    long Dropped,
    long? StoredBytes,
    long? DatabaseBytes,
    long BudgetBytes,
    long DatabaseLimitBytes,
    int RetentionDays,
    int MaxBodyBytes,
    DateTimeOffset? LastPruneAt);

public sealed record BeaconOverview(
    IReadOnlyDictionary<string, long> Counts,
    IReadOnlyList<BeaconItem> Recent,
    IReadOnlyList<KeyCount> TopKeys,
    StorageStatus Status,
    bool Degraded,
    DateTimeOffset At);
