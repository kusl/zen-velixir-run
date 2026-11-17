using Microsoft.Extensions.Options;

namespace Zen.Blazor.Beacons;

public sealed class BeaconJanitor(
    IBeaconStore store,
    IBeaconMaintenance maintenance,
    DatabaseCircuit circuit,
    IOptions<BeaconOptions> options,
    TimeProvider time,
    ILogger<BeaconJanitor> logger) : BackgroundService
{
    private const double TargetRatio = 0.8;
    private const double RecoverRatio = 0.6;
    private const long MinBudgetBytes = 1024 * 1024;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    private readonly BeaconOptions _options = options.Value;
    private long _budget = options.Value.StorageBudgetBytes;
    private long _lastDatabaseBytes;
    private PruneReport? _lastReport;

    public PruneReport? LastReport => Volatile.Read(ref _lastReport);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsConfigured)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(_options.PruneInterval, time);
            do
            {
                if (!circuit.CanAttempt)
                {
                    continue;
                }

                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    circuit.Failed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-_options.RetentionDays);
        var result = await PruneAsync(cutoff, cancellationToken);

        if (result.DatabaseBytes > _options.DatabaseLimitBytes)
        {
            var growing = result.DatabaseBytes > _lastDatabaseBytes;
            if (growing)
            {
                _budget = Math.Max(MinBudgetBytes, _budget / 2);
                logger.LogWarning(
                    "Database at {Size} bytes exceeds {Limit}; budget lowered to {Budget}",
                    result.DatabaseBytes, _options.DatabaseLimitBytes, _budget);
                result = await PruneAsync(cutoff, cancellationToken);
            }

            await VacuumAsync(cancellationToken);
        }
        else if (result.DatabaseBytes < _options.DatabaseLimitBytes * RecoverRatio && _budget < _options.StorageBudgetBytes)
        {
            _budget = Math.Min(_options.StorageBudgetBytes, _budget + _budget / 4);
        }

        _lastDatabaseBytes = result.DatabaseBytes;
        circuit.Succeeded();
        Volatile.Write(ref _lastReport, new PruneReport(time.GetUtcNow(), result.Removed, result.StoredBytes, result.DatabaseBytes, _budget));
    }

    private async Task VacuumAsync(CancellationToken cancellationToken)
    {
        try
        {
            await maintenance.VacuumAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Vacuum failed");
        }
    }

    private async Task<PruneResult> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var result = await maintenance.PruneAsync(cutoff, _budget, (long)(_budget * TargetRatio), cancellationToken);
        if (result.Removed > 0)
        {
            logger.LogInformation("Pruned {Count} beacons; {Stored} bytes stored, database {Database} bytes", result.Removed, result.StoredBytes, result.DatabaseBytes);
        }

        return result;
    }
}
