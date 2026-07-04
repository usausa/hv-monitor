namespace Monitor.Contracts;

public sealed record DiskInfo(
    string Name,
    long TotalBytes,
    long FreeBytes);
