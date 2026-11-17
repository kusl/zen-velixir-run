using Microsoft.Extensions.Options;

namespace Zen.Blazor.Beacons;

public sealed class BeaconService(
    BeaconBuffer buffer,
    IBeaconStore store,
    DatabaseCircuit circuit,
    BeaconJanitor janitor,
    IOptions<BeaconOptions> options,
    TimeProvider time,
    ILogger<BeaconService> logger)
{
    public const int MaxPageSize = 100;
    public const int MaxTopKeys = 50;

    private static readonly TimeSpan OverviewTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HourlyWindow = TimeSpan.FromHours(24);

    private readonly BeaconOptions _options = options.Value;
    private readonly SemaphoreSlim _overviewGate = new(1, 1);
    private OverviewCache? _overview;

    public void Accept(BeaconEntry entry) => buffer.Add(entry);

    public async Task<Lookup> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (buffer.TryGet(id, out var cached))
        {
            return new Lookup(LookupStatus.Found, cached);
        }

        var (entry, ok) = await TryStoreAsync(s => s.GetAsync(id, cancellationToken), cancellationToken);
        if (!ok)
        {
            return new Lookup(LookupStatus.Unavailable, null);
        }

        return entry is null ? new Lookup(LookupStatus.NotFound, null) : new Lookup(LookupStatus.Found, entry);
    }

    public async Task<BeaconPage> ListAsync(string? key, string? referer, Guid? before, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var (stored, ok) = await TryStoreAsync(s => s.ListAsync(key, referer, before, limit, cancellationToken), cancellationToken);

        var memory = Memory(ok)
            .Where(e => (key is null || e.Key == key)
                        && (referer is null || e.Referer == referer)
                        && (before is null || e.Id.CompareTo(before.Value) < 0))
            .Select(ToItem);

        var items = (stored ?? [])
            .Concat(memory)
            .DistinctBy(i => i.Id)
            .OrderByDescending(i => i.Id)
            .Take(limit)
            .ToList();

        var next = items.Count == limit ? items[^1].Id : (Guid?)null;
        return new BeaconPage(key, referer, items, next, IsDegraded(ok));
    }

    public async Task<BeaconSummary> SummarizeAsync(string key, string? referer, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - HourlyWindow;
        var (stored, ok) = await TryStoreAsync(s => s.SummarizeAsync(key, referer, since, cancellationToken), cancellationToken);

        var memory = BeaconTotals.From(
            Memory(ok).Where(e => e.Key == key && (referer is null || e.Referer == referer)),
            since);

        var totals = (stored ?? BeaconTotals.Empty).Combine(memory);
        return new BeaconSummary(key, referer, totals.Total, totals.Bytes, totals.FirstAt, totals.LastAt, totals.Referers, totals.Hourly, IsDegraded(ok));
    }

    public async Task<BeaconKeys> TopKeysAsync(int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxTopKeys);
        var (stored, ok) = await TryStoreAsync(s => s.TopKeysAsync(limit, cancellationToken), cancellationToken);
        var keys = (stored ?? [])
            .Concat(Memory(ok).GroupBy(e => e.Key, StringComparer.Ordinal).Select(g => new KeyCount(g.Key, g.LongCount())))
            .GroupBy(k => k.Key, StringComparer.Ordinal)
            .Select(g => new KeyCount(g.Key, g.Sum(k => k.Count)))
            .OrderByDescending(k => k.Count)
            .ThenBy(k => k.Key, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return new BeaconKeys(keys, IsDegraded(ok));
    }

    public StorageStatus Status()
    {
        var stats = buffer.Stats;
        var state = circuit.State;
        var report = janitor.LastReport;
        var database = !store.IsConfigured ? "memory-only"
            : state.Failures > 0 ? "unavailable"
            : state.LastSuccessAt is null ? "connecting"
            : "connected";

        return new StorageStatus(
            database,
            state.LastError,
            state.RetryAt,
            state.LastSuccessAt,
            stats.Entries,
            stats.Bytes,
            stats.Unsaved,
            stats.Dropped,
            report?.StoredBytes,
            report?.DatabaseBytes,
            report?.BudgetBytes ?? _options.StorageBudgetBytes,
            _options.DatabaseLimitBytes,
            _options.RetentionDays,
            _options.MaxBodyBytes,
            report?.At);
    }

    public async Task<BeaconOverview> GetOverviewAsync(IReadOnlyCollection<string> countKeys, bool fresh, CancellationToken cancellationToken)
    {
        var signature = string.Join('\n', countKeys);
        var requestedAt = time.GetUtcNow();

        if (!fresh && Volatile.Read(ref _overview) is { } cached && cached.Signature == signature && requestedAt - cached.Value.At < OverviewTtl)
        {
            return cached.Value;
        }

        await _overviewGate.WaitAsync(cancellationToken);
        try
        {
            if (_overview is { } current && current.Signature == signature
                && (fresh ? current.Value.At >= requestedAt : time.GetUtcNow() - current.Value.At < OverviewTtl))
            {
                return current.Value;
            }

            var overview = await BuildOverviewAsync(countKeys, cancellationToken);
            Volatile.Write(ref _overview, new OverviewCache(signature, overview));
            return overview;
        }
        finally
        {
            _overviewGate.Release();
        }
    }

    private async Task<BeaconOverview> BuildOverviewAsync(IReadOnlyCollection<string> countKeys, CancellationToken cancellationToken)
    {
        var at = time.GetUtcNow();
        var (stored, ok) = await TryStoreAsync(s => s.CountAsync(countKeys, cancellationToken), cancellationToken);
        var memory = Memory(ok).Where(e => countKeys.Contains(e.Key)).ToList();
        var counts = countKeys.Distinct(StringComparer.Ordinal).ToDictionary(
            k => k,
            k => (stored?.GetValueOrDefault(k) ?? 0) + memory.Count(e => e.Key == k),
            StringComparer.Ordinal);

        var recent = await ListAsync(null, null, null, 15, cancellationToken);
        var keys = await TopKeysAsync(10, cancellationToken);
        var degraded = IsDegraded(ok) || recent.Degraded || keys.Degraded;

        return new BeaconOverview(counts, recent.Items, keys.Keys, Status(), degraded, at);
    }

    private IEnumerable<BeaconEntry> Memory(bool storeOk)
    {
        var snapshot = buffer.Snapshot();
        return storeOk
            ? snapshot.Where(b => !b.Persisted).Select(b => b.Entry)
            : snapshot.Select(b => b.Entry);
    }

    private bool IsDegraded(bool storeOk) => store.IsConfigured && !storeOk;

    private async Task<(T? Value, bool Ok)> TryStoreAsync<T>(Func<IBeaconStore, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!store.IsConfigured)
        {
            return (await operation(store), true);
        }

        if (!circuit.CanAttempt)
        {
            return (default, false);
        }

        try
        {
            var value = await operation(store);
            circuit.Succeeded();
            return (value, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            circuit.Failed(ex);
            logger.LogDebug(ex, "Beacon read fell back to memory");
            return (default, false);
        }
    }

    private static BeaconItem ToItem(BeaconEntry entry) => new(
        entry.Id,
        entry.Key,
        entry.ReceivedAt,
        entry.Referer,
        entry.ContentType,
        entry.Size,
        entry.Body.Length > 160 ? entry.Body[..160] : entry.Body);

    private sealed record OverviewCache(string Signature, BeaconOverview Value);
}
