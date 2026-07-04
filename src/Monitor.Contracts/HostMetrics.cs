namespace Monitor.Contracts;

public sealed record HostMetrics(
    double CpuUsagePercent,
    long TotalMemoryMb,
    long UsedMemoryMb,
    IReadOnlyList<DiskInfo> Disks);
