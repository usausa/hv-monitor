namespace Monitor.Contracts;

// One atomic read of a host: static info, current metrics, and the VM list.
public sealed record HostSnapshot(
    HostInfo Host,
    HostMetrics Metrics,
    IReadOnlyList<VmInfo> Vms);
