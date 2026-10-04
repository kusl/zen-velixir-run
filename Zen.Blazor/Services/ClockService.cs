namespace Zen.Blazor.Services;

public sealed class ClockService(ILogger<ClockService> logger) : BackgroundService
{
    public event Action<DateTimeOffset>? Ticked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = 1005 - DateTimeOffset.Now.Millisecond;
            await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (stoppingToken.IsCancellationRequested || Ticked is not { } handlers)
            {
                continue;
            }

            var now = DateTimeOffset.Now;
            foreach (var handler in handlers.GetInvocationList().Cast<Action<DateTimeOffset>>())
            {
                try
                {
                    handler(now);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Clock subscriber failed");
                }
            }
        }
    }
}
