namespace Monitor.Core.HyperV;

using Monitor.Contracts;

public interface IHyperVService
{
    bool IsElevated { get; }

    ValueTask<IReadOnlyList<VmInfo>> GetVirtualMachinesAsync(CancellationToken cancellationToken = default);

    ValueTask<HostMetrics> GetHostMetricsAsync(CancellationToken cancellationToken = default);

    ValueTask StartAsync(string id, CancellationToken cancellationToken = default);

    ValueTask ShutdownAsync(string id, bool force, CancellationToken cancellationToken = default);

    ValueTask TurnOffAsync(string id, CancellationToken cancellationToken = default);

    ValueTask PauseAsync(string id, CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(string id, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(string id, CancellationToken cancellationToken = default);
}
