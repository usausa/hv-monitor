namespace Monitor.Web.Services;

using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Monitor.Contracts;

public sealed class HyperVApiClient : IHyperVApiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly Uri SnapshotEndpoint = new("/api/snapshot", UriKind.Relative);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IReadOnlyList<HyperVHostOptions> hosts;
    private readonly ILogger<HyperVApiClient> logger;

    public HyperVApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<HyperVHostsOptions> options,
        ILogger<HyperVApiClient> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.hosts = options.Value.HyperVHosts;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<HostVmResult>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var tasks = hosts.Select(host => GetHostResultAsync(host, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks);
    }

    public Task StartAsync(string hostName, string vmId, CancellationToken cancellationToken = default) =>
        PostAsync(hostName, VmActionPath(vmId, "start"), content: null, cancellationToken);

    public Task ResumeAsync(string hostName, string vmId, CancellationToken cancellationToken = default) =>
        PostAsync(hostName, VmActionPath(vmId, "resume"), content: null, cancellationToken);

    public Task PauseAsync(string hostName, string vmId, CancellationToken cancellationToken = default) =>
        PostAsync(hostName, VmActionPath(vmId, "pause"), content: null, cancellationToken);

    public Task SaveAsync(string hostName, string vmId, CancellationToken cancellationToken = default) =>
        PostAsync(hostName, VmActionPath(vmId, "save"), content: null, cancellationToken);

    public async Task ShutdownAsync(string hostName, string vmId, bool force, CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new ShutdownRequest(force));
        await PostAsync(hostName, VmActionPath(vmId, "shutdown"), content, cancellationToken);
    }

    public Task TurnOffAsync(string hostName, string vmId, CancellationToken cancellationToken = default) =>
        PostAsync(hostName, VmActionPath(vmId, "turnoff"), content: null, cancellationToken);

    private static Uri VmActionPath(string vmId, string action) =>
        new($"/api/vms/{Uri.EscapeDataString(vmId)}/{action}", UriKind.Relative);

    private async Task<HostVmResult> GetHostResultAsync(HyperVHostOptions host, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(host);

            var snapshot = await client.GetFromJsonAsync<HostSnapshot>(SnapshotEndpoint, cancellationToken);
            if (snapshot is null)
            {
                return new HostVmResult(host.Name, IsElevated: false, Metrics: null, [], "応答が空でした");
            }

            return new HostVmResult(host.Name, snapshot.Host.IsElevated, snapshot.Metrics, snapshot.Vms, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Failed to query host {HostName} ({BaseUrl})", host.Name, host.BaseUrl);
            return new HostVmResult(host.Name, IsElevated: false, Metrics: null, [], ex.Message);
        }
    }

    private async Task PostAsync(string hostName, Uri path, HttpContent? content, CancellationToken cancellationToken)
    {
        var host = FindHost(hostName);
        using var client = CreateClient(host);
        using var response = await client.PostAsync(path, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadProblemMessageAsync(response, cancellationToken);
            throw new HyperVApiException(message);
        }
    }

    private HttpClient CreateClient(HyperVHostOptions host)
    {
        var client = httpClientFactory.CreateClient(nameof(HyperVApiClient));
        client.BaseAddress = new Uri(host.BaseUrl, UriKind.Absolute);
        client.Timeout = RequestTimeout;
        client.DefaultRequestHeaders.Add("X-Api-Key", host.ApiKey);
        return client;
    }

    private HyperVHostOptions FindHost(string hostName)
    {
        foreach (var host in hosts)
        {
            if (string.Equals(host.Name, hostName, StringComparison.Ordinal))
            {
                return host;
            }
        }

        throw new HyperVApiException($"ホストが見つかりません: {hostName}");
    }

    private static async Task<string> ReadProblemMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
            if (!string.IsNullOrEmpty(problem?.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrEmpty(problem?.Title))
            {
                return problem.Title;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            // Body was not a ProblemDetails payload; fall through to the status-code message.
        }

        return $"操作に失敗しました (HTTP {(int)response.StatusCode})";
    }
}
