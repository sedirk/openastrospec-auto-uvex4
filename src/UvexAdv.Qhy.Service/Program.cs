using System.Net;
using System.Text.Json.Serialization;
using UvexAdv.Qhy.Core;
using UvexAdv.Qhy.Service;
using UvexAdv.Qhy.Service.Adapters;

var builder = WebApplication.CreateBuilder(args);
var machineRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "UVEX-ADV",
    "qhy");
builder.Configuration.AddJsonFile(Path.Combine(machineRoot, "appsettings.json"), optional: true, reloadOnChange: true);

builder.Host.UseWindowsService(options => options.ServiceName = "UVEX-ADV-QHY");
builder.WebHost.ConfigureKestrel((context, options) =>
    options.Listen(IPAddress.Loopback, BindServiceOptions(context.Configuration, machineRoot).Port));
// Bind through the final DI configuration. WebApplicationFactory and other host
// configuration callbacks run after the top-level entry point starts executing;
// eager binding here would therefore bypass their last-wins test configuration.
builder.Services.AddSingleton(services =>
    BindServiceOptions(services.GetRequiredService<IConfiguration>(), machineRoot));
builder.Services.AddSingleton(services =>
{
    var configured = services.GetRequiredService<QhyServiceOptions>();
    return QhyServiceConfigurationProof.Create(
        configured.Simulator,
        configured.Simulator ? "simulator" : "qhy-native",
        configured.ExpectedModel,
        configured.ExpectedStableId,
        configured.NativeSdkSha256,
        configured.NativeReadoutMode,
        configured.NativeFilterPositions);
});
builder.Services.AddSingleton<IQhyCameraAdapter>(services =>
{
    var configured = services.GetRequiredService<QhyServiceOptions>();
    return configured.Simulator
        ? new SimulatedQhyCameraAdapter(configured)
        : new NativeQhyCameraAdapter(configured);
});
builder.Services.AddSingleton(services =>
{
    var configured = services.GetRequiredService<QhyServiceOptions>();
    return new QhyJobCoordinator(
        services.GetRequiredService<IQhyCameraAdapter>(),
        new QhyCoordinatorOptions
        {
            ExpectedStableId = configured.ExpectedStableId,
            ExpectedModel = configured.ExpectedModel,
            DataRoot = configured.DataRoot,
        });
});
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHostedService<QhyTelemetryPublisher>();
builder.Services.AddHostedService<QhyAutoConnectHostedService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next(context).ConfigureAwait(false);
    }
    catch (KeyNotFoundException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
    }
    catch (UnauthorizedAccessException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is InvalidOperationException or QhyAdapterException)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context).ConfigureAwait(false);
    }
});

app.MapGet("/api/v1/health", (QhyServiceConfigurationProof proof) => Results.Ok(new QhyServiceHealth(
    "UVEX-ADV-QHY",
    "ok",
    LoopbackOnly: true,
    proof,
    DateTimeOffset.UtcNow)));
app.MapGet("/api/v1/camera", (QhyJobCoordinator coordinator) => Results.Ok(coordinator.CameraStatus));
app.MapPost("/api/v1/camera/connect", async (QhyJobCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.ConnectCameraAsync(cancellationToken).ConfigureAwait(false)));
app.MapPost("/api/v1/camera/disconnect", async (QhyJobCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.DisconnectCameraAsync(cancellationToken).ConfigureAwait(false)));
app.MapGet("/api/v1/camera/filter-wheel", async (QhyJobCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.ReadFilterWheelStatusAsync(cancellationToken).ConfigureAwait(false)));
app.MapPost("/api/v1/camera/filter-wheel/select", async (
        QhyFilterSelectionRequest request,
        QhyJobCoordinator coordinator,
        CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.SelectFilterAsync(request.FilterName, cancellationToken).ConfigureAwait(false)));

app.MapGet("/api/v1/jobs", (int? count, QhyJobCoordinator coordinator) =>
    Results.Ok(coordinator.RecentJobs(count ?? 50)));
app.MapGet("/api/v1/jobs/{id:guid}", (Guid id, QhyJobCoordinator coordinator) =>
    coordinator.GetJob(id) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapGet("/api/v1/jobs/lookup", (string observationRunId, QhyJobKind kind, string clientRequestId, QhyJobCoordinator coordinator) =>
    coordinator.FindByClientRequest(observationRunId, kind, clientRequestId) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapPost("/api/v1/jobs/acquisition", (AcquisitionJobRequest request, QhyJobCoordinator coordinator, HttpResponse response) =>
    StartAcquisition(request, coordinator, response));
app.MapPost("/api/v1/jobs/photometry", (PhotometryJobRequest request, QhyJobCoordinator coordinator, HttpResponse response) =>
    StartPhotometry(request, coordinator, response));
app.MapPost("/api/v1/jobs/{id:guid}/pause", async (
        Guid id,
        QhyOwnerControlRequest request,
        QhyJobCoordinator coordinator,
        CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.PauseAsync(id, request, cancellationToken).ConfigureAwait(false)));
app.MapPost("/api/v1/jobs/{id:guid}/resume", async (
        Guid id,
        QhyResumeRequest request,
        QhyJobCoordinator coordinator,
        CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.ResumeAsync(id, request, cancellationToken).ConfigureAwait(false)));
app.MapPost("/api/v1/jobs/{id:guid}/cancel", async (
        Guid id,
        QhyOwnerControlRequest request,
        QhyJobCoordinator coordinator,
        CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.CancelAsync(id, request, cancellationToken).ConfigureAwait(false)));
app.MapPost("/api/v1/jobs/{id:guid}/lease/renew", async (
        Guid id,
        QhyLeaseRenewalRequest request,
        QhyJobCoordinator coordinator,
        CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.RenewLeaseAsync(id, request, cancellationToken).ConfigureAwait(false)));
app.MapPost("/api/v1/jobs/{id:guid}/takeover", async (
        Guid id,
        OperatorTakeoverRequest request,
        QhyJobCoordinator coordinator,
        CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.TakeOverAsync(id, request, cancellationToken).ConfigureAwait(false)));
app.MapGet("/api/v1/jobs/{id:guid}/preview", (Guid id, QhyJobCoordinator coordinator, HttpResponse response) =>
{
    var preview = coordinator.GetLatestPreview(id);
    if (preview is null) return Results.NotFound();
    response.Headers["X-QHY-Frame-Id"] = preview.FrameId.ToString("D");
    response.Headers["X-QHY-Image-Width"] = preview.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-QHY-Image-Height"] = preview.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-QHY-Display-Min"] = preview.DisplayMinimumAdu.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-QHY-Display-Max"] = preview.DisplayMaximumAdu.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    return Results.File(preview.PngBytes, "image/png", enableRangeProcessing: false);
});
app.MapGet("/api/v1/jobs/{id:guid}/preview/metadata", (Guid id, QhyJobCoordinator coordinator) =>
    coordinator.GetLatestPreview(id) is { } preview
        ? Results.Ok(preview with { PngBytes = [] })
        : Results.NotFound());
app.MapGet("/api/v1/jobs/{id:guid}/manifest", (Guid id, QhyJobCoordinator coordinator) =>
{
    var snapshot = coordinator.GetJob(id);
    return snapshot is null
        ? Results.NotFound()
        : File.Exists(snapshot.ManifestPath)
            ? Results.File(snapshot.ManifestPath, "application/json", enableRangeProcessing: false)
            : Results.NotFound();
});

app.MapHub<QhyTelemetryHub>("/hubs/qhy");
app.Run();

static IResult StartAcquisition(
    AcquisitionJobRequest request,
    QhyJobCoordinator coordinator,
    HttpResponse response)
{
    var control = coordinator.StartAcquisition(request);
    AddOwnerControlHeaders(response, control);
    return Results.Accepted($"/api/v1/jobs/{control.Job.Id}", control.Job);
}

static IResult StartPhotometry(
    PhotometryJobRequest request,
    QhyJobCoordinator coordinator,
    HttpResponse response)
{
    var control = coordinator.StartPhotometry(request);
    AddOwnerControlHeaders(response, control);
    return Results.Accepted($"/api/v1/jobs/{control.Job.Id}", control.Job);
}

static void AddOwnerControlHeaders(HttpResponse response, QhyJobControlResponse control)
{
    response.Headers[QhyControlProtocol.OwnerTokenHeaderName] = control.OwnerToken;
    response.Headers[QhyControlProtocol.LeaseExpiresUtcHeaderName] = control.LeaseExpiresUtc.ToString("O");
    response.Headers.CacheControl = "no-store";
}

static QhyServiceOptions BindServiceOptions(IConfiguration configuration, string machineRoot)
{
    var configured = new QhyServiceOptions();
    configuration.GetSection("Qhy").Bind(configured);
    if (configured.Port is < 1 or > 65_535)
    {
        throw new InvalidOperationException("Qhy:Port must be within 1-65535.");
    }

    var filterPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var (rawName, position) in configured.NativeFilterPositions)
    {
        var name = rawName?.Trim() ?? string.Empty;
        if (!filterPositions.TryAdd(name, position))
        {
            throw new InvalidOperationException(
                $"Qhy:NativeFilterPositions contains duplicate name '{rawName}' under case-insensitive matching.");
        }
    }
    QhyServiceConfigurationProof.ValidateFilterPositions(
        filterPositions,
        requireConfigured: !configured.Simulator);

    return configured with
    {
        DataRoot = string.IsNullOrWhiteSpace(configured.DataRoot)
            ? Path.Combine(machineRoot, "data")
            : Path.GetFullPath(configured.DataRoot),
        NativeFilterPositions = filterPositions,
    };
}

public partial class Program;
