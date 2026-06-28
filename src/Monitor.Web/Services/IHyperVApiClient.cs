namespace Monitor.Web.Services;

// Client that talks to one or more Monitor.Agent instances over HTTP.
public interface IHyperVApiClient
{
    // Queries every configured host in parallel. Per-host failures are reported via HostVmResult.Error.
    Task<IReadOnlyList<HostVmResult>> GetVirtualMachinesAsync(CancellationToken cancellationToken = default);

    Task StartAsync(string hostName, string vmId, CancellationToken cancellationToken = default);

    Task ResumeAsync(string hostName, string vmId, CancellationToken cancellationToken = default);

    Task PauseAsync(string hostName, string vmId, CancellationToken cancellationToken = default);

    Task SaveAsync(string hostName, string vmId, CancellationToken cancellationToken = default);

    Task ShutdownAsync(string hostName, string vmId, bool force, CancellationToken cancellationToken = default);

    Task TurnOffAsync(string hostName, string vmId, CancellationToken cancellationToken = default);
}
