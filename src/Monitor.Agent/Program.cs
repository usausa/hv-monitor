using Monitor.Contracts;
using Monitor.Core.HyperV;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IHyperVService, HyperVService>();

var app = builder.Build();

var apiKey = app.Configuration["Agent:ApiKey"];

// API key authentication. The health endpoint is exempt so reachability can be checked without a key.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrEmpty(apiKey) ||
            !context.Request.Headers.TryGetValue("X-Api-Key", out var provided) ||
            provided != apiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    await next(context);
});

app.MapGet("/api/health", () => Results.Ok());

app.MapGet("/api/host", (IHyperVService hyperV) =>
    Results.Ok(new HostInfo(Environment.MachineName, hyperV.IsElevated)));

app.MapGet("/api/host/metrics", async (IHyperVService hyperV, CancellationToken cancellationToken) =>
{
    try
    {
        var metrics = await hyperV.GetHostMetricsAsync(cancellationToken);
        return Results.Ok(metrics);
    }
    catch (HyperVException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/snapshot", async (IHyperVService hyperV, CancellationToken cancellationToken) =>
{
    try
    {
        var host = new HostInfo(Environment.MachineName, hyperV.IsElevated);
        var metrics = await hyperV.GetHostMetricsAsync(cancellationToken);
        var vms = await hyperV.GetVirtualMachinesAsync(cancellationToken);
        return Results.Ok(new HostSnapshot(host, metrics, vms));
    }
    catch (HyperVException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/vms", async (IHyperVService hyperV, CancellationToken cancellationToken) =>
{
    try
    {
        var machines = await hyperV.GetVirtualMachinesAsync(cancellationToken);
        return Results.Ok(machines);
    }
    catch (HyperVException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/vms/{id}/start", (string id, IHyperVService hyperV, CancellationToken cancellationToken) =>
    ExecuteAsync(() => hyperV.StartAsync(id, cancellationToken).AsTask()));

app.MapPost("/api/vms/{id}/resume", (string id, IHyperVService hyperV, CancellationToken cancellationToken) =>
    ExecuteAsync(() => hyperV.ResumeAsync(id, cancellationToken).AsTask()));

app.MapPost("/api/vms/{id}/pause", (string id, IHyperVService hyperV, CancellationToken cancellationToken) =>
    ExecuteAsync(() => hyperV.PauseAsync(id, cancellationToken).AsTask()));

app.MapPost("/api/vms/{id}/save", (string id, IHyperVService hyperV, CancellationToken cancellationToken) =>
    ExecuteAsync(() => hyperV.SaveAsync(id, cancellationToken).AsTask()));

app.MapPost("/api/vms/{id}/shutdown", (string id, ShutdownRequest request, IHyperVService hyperV, CancellationToken cancellationToken) =>
    ExecuteAsync(() => hyperV.ShutdownAsync(id, request.Force, cancellationToken).AsTask()));

app.MapPost("/api/vms/{id}/turnoff", (string id, IHyperVService hyperV, CancellationToken cancellationToken) =>
    ExecuteAsync(() => hyperV.TurnOffAsync(id, cancellationToken).AsTask()));

app.Run();

static async Task<IResult> ExecuteAsync(Func<Task> operation)
{
    try
    {
        await operation();
        return Results.Ok();
    }
    catch (HyperVException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
}
