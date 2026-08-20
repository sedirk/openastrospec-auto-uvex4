using System.Net;
using System.Text.Json.Serialization;
using UvexAdv.Core;
using UvexAdv.Protocol;
using UvexAdv.Service;
using UvexAdv.Service.Operations;
using UvexAdv.Service.Persistence;
using UvexAdv.Service.Transport;

var builder = WebApplication.CreateBuilder(args);
var dataPaths = new UvexDataPaths();
builder.Configuration.AddJsonFile(dataPaths.Configuration, optional: true, reloadOnChange: true);
builder.Logging.AddProvider(new JsonFileLoggerProvider(dataPaths));
builder.Host.UseWindowsService(options => options.ServiceName = "UVEX-ADV");
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 47844));

var safety = new UvexSafetyOptions();
builder.Configuration.GetSection("Uvex").Bind(safety);
builder.Services.AddSingleton(safety);
builder.Services.AddSingleton(dataPaths);
builder.Services.AddSingleton<UvexDatabase>();
builder.Services.AddSingleton<ControlLeaseManager>();
builder.Services.AddSingleton<IUvexTransport>(services => safety.Simulator
    ? new SimulatedUvexTransport()
    : new SerialUvexTransport(safety, services.GetRequiredService<ILogger<SerialUvexTransport>>()));
builder.Services.AddSingleton(services => new UvexProtocolSession(
    services.GetRequiredService<IUvexTransport>(),
    safety.CommandTimeout));
builder.Services.AddSingleton<UvexDeviceController>();
builder.Services.AddSingleton<OperationRegistry>();
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHostedService<UvexHostedService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next(context).ConfigureAwait(false);
    }
    catch (UnauthorizedAccessException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context).ConfigureAwait(false);
    }
    catch (ArgumentOutOfRangeException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
    }
    catch (InvalidOperationException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context).ConfigureAwait(false);
    }
});

app.MapGet("/api/v1/health", () => Results.Ok(new { service = "UVEX-ADV", status = "ok", timestampUtc = DateTimeOffset.UtcNow }));
app.MapGet("/api/v1/device", (UvexDeviceController device) => Results.Ok(device.Status));

app.MapPost("/api/v1/leases", (LeaseRequest request, ControlLeaseManager leases) =>
    Results.Ok(leases.Acquire(request.Owner, TimeSpan.FromSeconds(request.TtlSeconds))));
app.MapPost("/api/v1/leases/{token}/renew", (string token, RenewLeaseRequest request, ControlLeaseManager leases) =>
    Results.Ok(leases.Renew(token, TimeSpan.FromSeconds(request.TtlSeconds))));
app.MapDelete("/api/v1/leases/{token}", (string token, ControlLeaseManager leases) =>
{
    leases.Release(token);
    return Results.NoContent();
});

app.MapGet("/api/v1/operations", (OperationRegistry operations) => Results.Ok(operations.Recent()));
app.MapGet("/api/v1/operations/{id:guid}", (Guid id, OperationRegistry operations) =>
    operations.Get(id) is { } operation ? Results.Ok(operation) : Results.NotFound());
app.MapDelete("/api/v1/operations/{id:guid}", (Guid id, OperationRegistry operations) =>
    operations.Cancel(id) ? Results.Accepted() : Results.NotFound());

app.MapGet("/api/v1/calibrations", (UvexDatabase database, UvexSafetyOptions options) =>
    Results.Ok(CalibrationProfilePolicy.CompatibleProfiles(database.GetCalibrationProfiles(), options)));
app.MapGet("/api/v1/calibrations/{id}", (string id, UvexDatabase database, UvexSafetyOptions options) =>
    database.GetCalibrationProfile(id) is { } profile && CalibrationProfilePolicy.IsCompatible(profile, options)
        ? Results.Ok(profile)
        : Results.NotFound());
app.MapPut("/api/v1/calibrations/{id}", (string id, CalibrationProfile profile, HttpRequest request, ControlLeaseManager leases, UvexDatabase database, UvexSafetyOptions options) =>
{
    _ = RequireLeaseHeader(request, leases);
    if (!id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest("Route and payload calibration ids differ.");
    database.UpsertCalibrationProfile(CalibrationProfilePolicy.PrepareForStorage(profile, options));
    return Results.NoContent();
});

app.MapPost("/api/v1/device/connect", (HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("device.connect", ct => device.ConnectAsync(ct)));
});
app.MapPost("/api/v1/device/disconnect", (HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    _ = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("device.disconnect", ct => device.DisconnectAsync(ct)));
});
app.MapPost("/api/v1/device/maintenance/enter", (HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("maintenance.enter", ct => device.EnterMaintenanceAsync(token, ct)));
});
app.MapPost("/api/v1/device/maintenance/exit", (HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("maintenance.exit", ct => device.ExitMaintenanceAsync(token, ct)));
});
app.MapPost("/api/v1/device/stop", (UvexDeviceController device, OperationRegistry operations) =>
    Results.Accepted(value: operations.Start("device.emergency-stop", ct => device.EmergencyStopAsync(ct))));

app.MapPost("/api/v1/grating/home", (HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("grating.home", ct => device.HomeGratingAsync(token, ct)));
});
app.MapPost("/api/v1/grating/move", (MoveRequest move, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("grating.move", ct => device.MoveGratingRelativeAsync(move.DeltaSteps, token, ct)));
});
app.MapPost("/api/v1/grating/wavelength", (WavelengthRequest target, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("grating.wavelength", ct => device.GotoWavelengthAsync(target.WavelengthNm, token, ct)));
});
app.MapPost("/api/v1/focus/home", (HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("focus.home", ct => device.HomeFocusAsync(token, ct)));
});
app.MapPost("/api/v1/focus/move", (MoveRequest move, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("focus.move", ct => device.MoveFocusRelativeAsync(move.DeltaSteps, token, ct)));
});
app.MapPost("/api/v1/slit/select", (SlitRequest slit, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("slit.select", ct => device.SelectSlitAsync(slit.Position, token, ct)));
});
app.MapPost("/api/v1/slit/select-open-loop", (SlitOpenLoopRequest slit, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    if (!slit.Confirmed)
    {
        return Results.BadRequest("Explicit confirmation is required to select a slit without photodiode detection.");
    }

    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start(
        "slit.select-open-loop",
        ct => device.SelectSlitAsync(slit.Position, usePhotodiode: false, token, ct)));
});
app.MapPost("/api/v1/slit/calibrate-position", (SlitCalibrationRequest calibration, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    if (!calibration.Confirmed) return Results.BadRequest("Explicit confirmation is required for slit position calibration.");
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("slit.calibrate-position", ct => device.CalibrateSlitPositionAsync(calibration.Position, token, ct)));
});
app.MapPost("/api/v1/slit/calibrate-photodiode", (CalibrationConfirmation confirmation, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    if (!confirmation.Confirmed) return Results.BadRequest("Explicit confirmation is required for photodiode calibration.");
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("slit.calibrate-photodiode", ct => device.AutoCalibrateSlitPhotodiodeAsync(token, ct)));
});
app.MapPost("/api/v1/slit/calibrate-offset", (SlitOffsetCalibrationRequest calibration, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    if (!calibration.Confirmed) return Results.BadRequest("Explicit confirmation is required for slit offset calibration.");
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start(
        "slit.calibrate-offset",
        ct => device.SetSlitOffsetAsync(calibration.Position, calibration.OffsetSteps, token, ct)));
});
app.MapPost("/api/v1/slit/illumination", (SlitIlluminationRequest illumination, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start(
        "slit.illumination",
        ct => device.SetSlitIlluminationAsync(illumination.Enabled, token, ct)));
});
app.MapPost("/api/v1/calibration/relay", (RelayRequest relay, HttpRequest request, UvexDeviceController device, ControlLeaseManager leases, OperationRegistry operations) =>
{
    var token = RequireLeaseHeader(request, leases);
    return Results.Accepted(value: operations.Start("calibration.relay", ct => device.SetCalibrationRelayAsync(relay.Relay, relay.Enabled, token, ct)));
});

app.MapHub<UvexStatusHub>("/hubs/telemetry");
app.Run();

static string RequireLeaseHeader(HttpRequest request, ControlLeaseManager leases)
{
    var token = request.Headers["X-Uvex-Lease"].FirstOrDefault();
    leases.Require(token);
    return token!;
}

public sealed record LeaseRequest(string Owner, int TtlSeconds = 30);
public sealed record RenewLeaseRequest(int TtlSeconds = 30);
public sealed record MoveRequest(int DeltaSteps);
public sealed record WavelengthRequest(double WavelengthNm);
public sealed record SlitRequest(int Position);
public sealed record SlitOpenLoopRequest(int Position, bool Confirmed);
public sealed record SlitCalibrationRequest(int Position, bool Confirmed);
public sealed record SlitOffsetCalibrationRequest(int Position, int OffsetSteps, bool Confirmed);
public sealed record CalibrationConfirmation(bool Confirmed);
public sealed record SlitIlluminationRequest(bool Enabled);
public sealed record RelayRequest(int Relay, bool Enabled);

public partial class Program;
