namespace Zen.Blazor.Beacons;

public interface IBeaconStore
{
    bool IsConfigured { get; }

    Task InsertAsync(IReadOnlyList<BeaconEntry> entries, CancellationToken cancellationToken);

    Task<BeaconEntry?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<BeaconItem>> ListAsync(string? key, string? referer, Guid? before, int limit, CancellationToken cancellationToken);

    Task<BeaconTotals> SummarizeAsync(string key, string? referer, DateTimeOffset hourlySince, CancellationToken cancellationToken);

    Task<IReadOnlyList<KeyCount>> TopKeysAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, long>> CountAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}

public interface IBeaconMaintenance
{
    Task<PruneResult> PruneAsync(DateTimeOffset olderThan, long budgetBytes, long targetBytes, CancellationToken cancellationToken);

    Task VacuumAsync(CancellationToken cancellationToken);
}
