namespace Zen.Blazor.Beacons;

public sealed class NullBeaconStore : IBeaconStore, IBeaconMaintenance
{
    private static readonly IReadOnlyDictionary<string, long> NoCounts = new Dictionary<string, long>();

    public bool IsConfigured => false;

    public Task InsertAsync(IReadOnlyList<BeaconEntry> entries, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<BeaconEntry?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<BeaconEntry?>(null);

    public Task<IReadOnlyList<BeaconItem>> ListAsync(string? key, string? referer, Guid? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BeaconItem>>([]);

    public Task<BeaconTotals> SummarizeAsync(string key, string? referer, DateTimeOffset hourlySince, CancellationToken cancellationToken) =>
        Task.FromResult(BeaconTotals.Empty);

    public Task<IReadOnlyList<KeyCount>> TopKeysAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<KeyCount>>([]);

    public Task<IReadOnlyDictionary<string, long>> CountAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult(NoCounts);

    public Task<PruneResult> PruneAsync(DateTimeOffset olderThan, long budgetBytes, long targetBytes, CancellationToken cancellationToken) =>
        Task.FromResult(new PruneResult(0, 0, 0));

    public Task VacuumAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
