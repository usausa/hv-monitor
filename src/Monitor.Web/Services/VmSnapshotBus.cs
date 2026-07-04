namespace Monitor.Web.Services;

// Event payload carrying the latest snapshot of all hosts.
public sealed class VmSnapshotEventArgs(IReadOnlyList<HostVmResult> snapshot) : EventArgs
{
    public IReadOnlyList<HostVmResult> Snapshot { get; } = snapshot;
}

// Singleton bus holding the latest snapshot and notifying subscribers when it changes.
public interface IVmSnapshotBus
{
    // The most recently published snapshot (empty until the first poll).
    // Used for immediate display when a page is opened, without waiting for the next event.
    IReadOnlyList<HostVmResult> Current { get; }

    event EventHandler<VmSnapshotEventArgs>? Updated;

    void Publish(IReadOnlyList<HostVmResult> snapshot);
}

public sealed class VmSnapshotBus : IVmSnapshotBus
{
    private volatile IReadOnlyList<HostVmResult> current = [];

    public IReadOnlyList<HostVmResult> Current => current;

    public event EventHandler<VmSnapshotEventArgs>? Updated;

    public void Publish(IReadOnlyList<HostVmResult> snapshot)
    {
        current = snapshot;
        Updated?.Invoke(this, new VmSnapshotEventArgs(snapshot));
    }
}
