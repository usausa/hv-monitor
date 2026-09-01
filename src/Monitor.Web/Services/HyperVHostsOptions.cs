namespace Monitor.Web.Services;

// Binds the "HyperVHosts" array in appsettings.json to a typed options object.
public sealed class HyperVHostsOptions
{
    public IReadOnlyList<HyperVHostOptions> HyperVHosts { get; init; } = [];
}

// A single Monitor.Agent endpoint registered in configuration.
public sealed class HyperVHostOptions
{
    public string Name { get; init; } = string.Empty;

#pragma warning disable CA1056
    public string BaseUrl { get; init; } = string.Empty;
#pragma warning restore CA1056

    public string ApiKey { get; init; } = string.Empty;
}
