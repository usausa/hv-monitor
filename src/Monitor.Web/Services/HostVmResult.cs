namespace Monitor.Web.Services;

using Monitor.Contracts;

// Aggregated result of querying a single host's Agent.
// Error is non-null when the host was unreachable or the request failed; in that case Vms is empty.
public sealed record HostVmResult(string HostName, bool IsElevated, IReadOnlyList<VmInfo> Vms, string? Error);
