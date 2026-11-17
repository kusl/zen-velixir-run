using Microsoft.Extensions.Options;

namespace Zen.Blazor.Beacons;

public sealed class BeaconWriter(
    BeaconBuffer buffer,
    IBeaconStore store,
    DatabaseCircuit circuit,
    IOptions<BeaconOptions> options,
    ILogger<BeaconWriter> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownFlush = TimeSpan.FromSeconds(5);

    private readonly int _batchSize = options.Value.WriteBatchSize;
    private long _reportedDropped;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsConfigured)
        {
            logger.LogWarning("No PostgreSQL settings found; beacons are kept in memory only");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(stoppingToken);
                ReportDrops();
                var wait = circuit.CanAttempt ? IdleWait : circuit.RetryIn;
                await buffer.WaitForWorkAsync(wait, stoppingToken);
                if (!circuit.CanAttempt)
                {
                    await Task.Delay(circuit.RetryIn, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Beacon writer loop failed");
                await Task.Delay(IdleWait, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        using var timeout = new CancellationTokenSource(ShutdownFlush);
        try
        {
            await FlushAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final beacon flush incomplete; {Count} unsaved beacons lost", buffer.Stats.Unsaved);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (circuit.CanAttempt)
        {
            var batch = buffer.TakeUnsaved(_batchSize);
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await store.InsertAsync(batch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                circuit.Failed(ex);
                return;
            }

            buffer.MarkPersisted(batch);
            circuit.Succeeded();
        }
    }

    private void ReportDrops()
    {
        var dropped = buffer.Stats.Dropped;
        if (dropped > _reportedDropped)
        {
            logger.LogWarning("{Count} unsaved beacons dropped from a full memory buffer", dropped - _reportedDropped);
            _reportedDropped = dropped;
        }
    }
}
