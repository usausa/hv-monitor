namespace Monitor.Web.Services;

using Microsoft.Extensions.Options;

// Polls all configured hosts on a fixed interval and publishes each snapshot to the bus.
public sealed class VmMonitorBackgroundService(
    IHyperVApiClient apiClient,
    IVmSnapshotBus bus,
    IOptions<MonitoringOptions> options,
    ILogger<VmMonitorBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitoring = options.Value;
        if (!monitoring.Enabled)
        {
            return;
        }

        var seconds = monitoring.RefreshIntervalSeconds > 0 ? monitoring.RefreshIntervalSeconds : 10;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        // Fetch once immediately so the first opened page has data, then poll on each tick.
        do
        {
            try
            {
                var snapshot = await apiClient.GetSnapshotAsync(stoppingToken);
                bus.Publish(snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Snapshot polling failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
