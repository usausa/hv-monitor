namespace Monitor.Web.Services;

// Binds the "Monitoring" configuration section in appsettings.json.
public sealed class MonitoringOptions
{
    public bool Enabled { get; init; } = true;

    public int RefreshIntervalSeconds { get; init; } = 10;
}
