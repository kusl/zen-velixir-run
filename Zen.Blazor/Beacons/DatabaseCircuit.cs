namespace Zen.Blazor.Beacons;

public readonly record struct CircuitState(int Failures, DateTimeOffset? RetryAt, DateTimeOffset? LastSuccessAt, string? LastError);

public sealed class DatabaseCircuit(TimeProvider time, ILogger<DatabaseCircuit> logger)
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSuccessAt;
    private string? _lastError;

    public bool CanAttempt
    {
        get
        {
            lock (_gate)
            {
                return time.GetUtcNow() >= _retryAt;
            }
        }
    }

    public TimeSpan RetryIn
    {
        get
        {
            lock (_gate)
            {
                var wait = _retryAt - time.GetUtcNow();
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
    }

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return new CircuitState(
                    _failures,
                    _failures > 0 ? _retryAt : null,
                    _lastSuccessAt,
                    _lastError);
            }
        }
    }

    public void Succeeded()
    {
        int previous;
        lock (_gate)
        {
            previous = _failures;
            _failures = 0;
            _retryAt = DateTimeOffset.MinValue;
            _lastSuccessAt = time.GetUtcNow();
            _lastError = null;
        }

        if (previous > 0)
        {
            logger.LogWarning("Database available again after {Failures} failed attempts", previous);
        }
    }

    public void Failed(Exception exception)
    {
        int failures;
        TimeSpan delay;
        lock (_gate)
        {
            failures = ++_failures;
            var factor = Math.Pow(2, Math.Min(failures - 1, 10));
            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, (long)(MinDelay.Ticks * factor)));
            _retryAt = time.GetUtcNow() + delay;
            _lastError = exception.GetType().Name;
        }

        if (failures == 1)
        {
            logger.LogWarning(exception, "Database unavailable; serving from memory, retrying in {Delay}", delay);
        }
        else
        {
            logger.LogDebug(exception, "Database still unavailable after {Failures} attempts; retrying in {Delay}", failures, delay);
        }
    }
}
