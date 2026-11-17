using System.Net.Sockets;

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
        var reason = Describe(exception);
        int failures;
        TimeSpan delay;
        bool changed;
        lock (_gate)
        {
            failures = ++_failures;
            var factor = Math.Pow(2, Math.Min(failures - 1, 10));
            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, (long)(MinDelay.Ticks * factor)));
            _retryAt = time.GetUtcNow() + delay;
            changed = _lastError != reason;
            _lastError = reason;
        }

        if (failures == 1 || changed)
        {
            logger.LogWarning(
                "Database unavailable ({Reason}: {Message}); serving from memory, retrying in {Delay}",
                reason, Innermost(exception).Message, delay);
        }

        logger.LogDebug(exception, "Database attempt {Failures} failed", failures);
    }

    private static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return $"{nameof(SocketException)}:{socket.SocketErrorCode}";
            }
        }

        return Innermost(exception).GetType().Name;
    }

    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
