using Monitor.Web.Components;
using Monitor.Web.Services;

using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddOptions<HyperVHostsOptions>()
    .Bind(builder.Configuration)
    .Validate(
        static o => o.HyperVHosts.All(static h => !string.IsNullOrWhiteSpace(h.Name)),
        "HyperVHosts:Name is required.")
    .Validate(
        static o => o.HyperVHosts.Select(static h => h.Name).Distinct(StringComparer.Ordinal).Count() == o.HyperVHosts.Count,
        "HyperVHosts:Name must be unique.")
    .Validate(
        static o => o.HyperVHosts.All(static h =>
            Uri.TryCreate(h.BaseUrl, UriKind.Absolute, out var uri) &&
            ((uri.Scheme == Uri.UriSchemeHttp) || (uri.Scheme == Uri.UriSchemeHttps))),
        "HyperVHosts:BaseUrl must be an absolute http or https URL.")
    .ValidateOnStart();
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection("Monitoring"));

builder.Services.AddHttpClient();
foreach (var host in (builder.Configuration.Get<HyperVHostsOptions>() ?? new HyperVHostsOptions()).HyperVHosts)
{
    var baseUrl = host.BaseUrl;
    var apiKey = host.ApiKey;
    builder.Services.AddHttpClient(HyperVApiClient.ClientName(host.Name), client =>
    {
        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        client.Timeout = HyperVApiClient.RequestTimeout;
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    });
}

builder.Services.AddSingleton<IHyperVApiClient, HyperVApiClient>();
builder.Services.AddSingleton<IVmSnapshotBus, VmSnapshotBus>();
builder.Services.AddHostedService<VmMonitorBackgroundService>();

builder.Services.AddScoped<ToastService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
