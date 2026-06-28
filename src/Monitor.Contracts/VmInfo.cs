namespace Monitor.Contracts;

public sealed record VmInfo(
    string Id,
    string Name,
    VmState State,
    int? CpuUsagePercent,
    long? MemoryUsageMb,
    TimeSpan? Uptime,
    string? Version);
