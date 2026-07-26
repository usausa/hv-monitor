namespace Monitor.Contracts;

// One atomic read of a host: static info, current metrics, and the VM list.
// Metrics is null when the host counters were unavailable; the VM list is still returned.
public sealed record HostSnapshot(
    HostInfo Host,
    HostMetrics? Metrics,
    IReadOnlyList<VmInfo> Vms);
