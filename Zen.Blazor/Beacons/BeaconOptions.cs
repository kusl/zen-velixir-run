namespace Zen.Blazor.Beacons;

public sealed class BeaconOptions
{
    public const string Section = "Beacons";

    public int MaxBodyBytes { get; set; } = 32 * 1024;
    public int MaxHeaders { get; set; } = 64;
    public int MaxHeaderValueLength { get; set; } = 1024;
    public long MemoryBytes { get; set; } = 4L * 1024 * 1024;
    public int MemoryEntries { get; set; } = 2000;
    public long StorageBudgetBytes { get; set; } = 48L * 1024 * 1024;
    public long DatabaseLimitBytes { get; set; } = 160L * 1024 * 1024;
    public int RetentionDays { get; set; } = 30;
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromMinutes(2);
    public int WriteBatchSize { get; set; } = 50;
    public int MaxPoolSize { get; set; } = 2;
    public int WritesPerMinute { get; set; } = 30;
    public int ReadsPerMinute { get; set; } = 120;
    public string CspKey { get; set; } = "zen-csp";

    public bool IsValid() =>
        MaxBodyBytes is > 0 and <= 1024 * 1024
        && MaxHeaders > 0
        && MaxHeaderValueLength > 0
        && MemoryBytes >= MaxBodyBytes * 4L
        && MemoryEntries > 0
        && StorageBudgetBytes > 0
        && DatabaseLimitBytes > StorageBudgetBytes
        && RetentionDays > 0
        && PruneInterval >= TimeSpan.FromSeconds(10)
        && WriteBatchSize > 0
        && MaxPoolSize is > 0 and <= 5
        && WritesPerMinute > 0
        && ReadsPerMinute > 0
        && BeaconKey.IsValid(CspKey);
}
