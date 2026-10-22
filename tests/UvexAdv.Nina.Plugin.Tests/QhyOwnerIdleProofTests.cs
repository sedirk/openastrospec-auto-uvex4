using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyOwnerIdleProofTests
{
    [Theory]
    [InlineData(QhyJobState.Completed, true)]
    [InlineData(QhyJobState.Cancelled, true)]
    [InlineData(QhyJobState.Faulted, true)]
    [InlineData(QhyJobState.TakenOver, true)]
    [InlineData(QhyJobState.Queued, false)]
    [InlineData(QhyJobState.Running, false)]
    [InlineData(QhyJobState.Pausing, false)]
    [InlineData(QhyJobState.Paused, false)]
    [InlineData(QhyJobState.PausedNeedsAttention, false)]
    [InlineData(QhyJobState.Cancelling, false)]
    [InlineData((QhyJobState)123, false)]
    public async Task LegacyReadsActualOwnerNotNewMastersEmptyDictionary(QhyJobState state, bool idle)
    {
        var calls = new List<string>();
        using var client = Client(false, request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            Assert.True(request.Headers.CacheControl!.NoCache);
            return Json(new[] { Job(state) });
        });
        var gate = await client.ReadGlobalIdleAsync(CancellationToken.None);
        Assert.Equal(idle, gate.Disposition == GateDisposition.Passed);
        Assert.Equal(new[] { "GET /api/v1/jobs?count=200" }, calls);
    }

    [Fact]
    public async Task LegacyEmptyResponseIsDifferentFromAValidEmptyList()
    {
        using var valid = Client(false, _ => Json(Array.Empty<QhyJobSnapshot>()));
        Assert.Equal(GateDisposition.Passed, (await valid.ReadGlobalIdleAsync(CancellationToken.None)).Disposition);
        using var missing = Client(false, _ => new(HttpStatusCode.OK) { Content = new StringContent("null") });
        Assert.Equal("QHY_OWNER_IDLE_UNAVAILABLE", (await missing.ReadGlobalIdleAsync(CancellationToken.None)).Code);
        using var partial = Client(false, _ => Json(new[] { Job(QhyJobState.Completed) with { CompletedUtc = null } }));
        Assert.Equal("QHY_OWNER_STOP_UNCONFIRMED", (await partial.ReadGlobalIdleAsync(CancellationToken.None)).Code);
    }

    [Fact]
    public async Task CappedLegacyListCannotHideAnOlderActiveJobAfterClockCorrection()
    {
        using var client = Client(false, _ => Json(Enumerable.Range(0, 200).Select(_ => Job(QhyJobState.Completed)).ToArray()));
        Assert.Equal("QHY_OWNER_IDLE_UNAVAILABLE", (await client.ReadGlobalIdleAsync(CancellationToken.None)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReadSupportCannotBecomeIdle(bool native)
    {
        using var client = Client(native, _ => new(HttpStatusCode.NotFound) { Content = new StringContent("unsupported") });
        Assert.Equal("QHY_OWNER_IDLE_UNAVAILABLE", (await client.ReadGlobalIdleAsync(CancellationToken.None)).Code);
    }

    [Fact]
    public async Task TimeoutFailsClosedButCallerCancellationPropagates()
    {
        using var timed = Client(false, _ => throw new TaskCanceledException("timeout"));
        Assert.Equal("QHY_OWNER_IDLE_UNAVAILABLE", (await timed.ReadGlobalIdleAsync(CancellationToken.None)).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelled = Client(false, _ => throw new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ReadGlobalIdleAsync(cancellation.Token));
    }

    [Fact]
    public async Task NativeProbeUsesBoundReadonlyRouteAndCompleteFreshProof()
    {
        using var client = Client(true, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/owner-idle", request.RequestUri!.AbsolutePath);
            return Json(Idle());
        });
        Assert.Equal("QHY_OWNER_IDLE_CONFIRMED", (await client.ReadGlobalIdleAsync(CancellationToken.None)).Code);
    }

    [Theory]
    [InlineData("CoordinatorIdle")]
    [InlineData("CameraIdle")]
    [InlineData("WheelIdle")]
    [InlineData("FocuserIdle")]
    [InlineData("PersistenceIdle")]
    public void NativeCannotPassAnUnconfirmedOwnerCategory(string property)
    {
        var proof = Idle();
        typeof(PhotometryOwnerIdleProof).GetProperty(property)!.SetValue(proof, false);
        Assert.Equal("QHY_OWNER_STOP_UNCONFIRMED", PhotometryOwnerIdleProof.Evaluate(proof, DateTimeOffset.UtcNow).Code);
    }

    [Theory]
    [InlineData(-30, 1)]
    [InlineData(30, 1)]
    [InlineData(0, 0)]
    public void OldUnknownOrFutureNativeProofCannotPass(int age, int version)
    {
        var proof = Idle() with { TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(age), Version = version };
        Assert.Equal("QHY_OWNER_IDLE_UNAVAILABLE", PhotometryOwnerIdleProof.Evaluate(proof, DateTimeOffset.UtcNow).Code);
    }

    [Theory]
    [InlineData("idle", true)]
    [InlineData("cameraDisconnected", false)]
    [InlineData("cameraWrongIdentity", false)]
    [InlineData("exposing", false)]
    [InlineData("liveView", false)]
    [InlineData("downloading", false)]
    [InlineData("foreignCaptureBlock", false)]
    [InlineData("wheelMoving", false)]
    [InlineData("focuserMoving", false)]
    [InlineData("focuserSettling", false)]
    [InlineData("durablePending", false)]
    [InlineData("coordinatorBusy", false)]
    public void NativeProbeReadsGlobalMediatorStatesWithoutAnyCommand(string scenario, bool idle)
    {
        var calls = new List<string>(); var owner = new object();
        object? Read(MethodInfo m, object?[] args, string id)
        {
            calls.Add(id + "." + m.Name);
            if (m.Name == "IsFreeToCapture") return ReferenceEquals(args[0], owner) && scenario != "foreignCaptureBlock";
            Assert.Equal("GetInfo", m.Name);
            var info = Activator.CreateInstance(m.ReturnType)!;
            void Set(string name, object value) => m.ReturnType.GetProperty(name)!.SetValue(info, value);
            Set("Connected", scenario != id + "Disconnected");
            Set("DeviceId", scenario == id + "WrongIdentity" ? "wrong" : id);
            if (id == "camera")
            {
                Set("IsExposing", scenario == "exposing"); Set("LiveViewEnabled", scenario == "liveView");
                Set("CameraState", scenario == "downloading" ? CameraStates.Download : CameraStates.Idle);
            }
            else Set("IsMoving", scenario == id + "Moving");
            if (id == "focuser") Set("IsSettling", scenario == "focuserSettling");
            return info;
        }
        var settings = Settings(); settings.PhotometryCameraId = "camera";
        settings.PhotometryFilterWheelId = "wheel"; settings.PhotometryFocuserId = "focuser";
        var adapter = new NinaPhotometryCameraAdapter(Proxy<IProfileService>(),
            Proxy<ICameraMediator>((m, a) => Read(m, a, "camera")),
            Proxy<IFilterWheelMediator>((m, a) => Read(m, a, "wheel")),
            Proxy<IFocuserMediator>((m, a) => Read(m, a, "focuser")),
            Proxy<IImagingMediator>(), Proxy<IImageSaveMediator>(), settings, () => { }, new Progress<ApplicationStatus>(), () => owner);
        var proof = adapter.ReadOwnerIdle(scenario != "coordinatorBusy", scenario != "durablePending");
        Assert.Equal(idle, PhotometryOwnerIdleProof.Evaluate(proof, DateTimeOffset.UtcNow).Disposition == GateDisposition.Passed);
        Assert.All(calls, call => Assert.True(call.EndsWith(".GetInfo") || call == "camera.IsFreeToCapture"));
    }

    [Fact]
    public void WorkerRestartStillSeesUnresolvedDurableFocusAndNeverRewritesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-idle-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var policy = new QhyFocusRunPolicy("focuser", 100, 0, 1000, 100, 200, ApproachSign: 1);
            var budget = QhyFocusBudgetStore.Open(directory, "older-run", new string('a', 64), policy);
            budget.Reserve(100, 20);
            var path = Directory.GetFiles(directory).Single(); var before = File.ReadAllBytes(path);
            Assert.True(QhyFocusBudgetStore.HasPendingRecords(directory));
            Assert.Equal(before, File.ReadAllBytes(path));
            budget.Confirm(120);
            Assert.False(QhyFocusBudgetStore.HasPendingRecords(directory));
            File.WriteAllText(path, "{\"Version\":1,\"State\":{},\"Policy\":{}}");
            Assert.Throws<InvalidDataException>(() => QhyFocusBudgetStore.HasPendingRecords(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static PhotometryOwnerIdleProof Idle() => new(1, DateTimeOffset.UtcNow, true, true, true, true, true);
    private static QhyJobSnapshot Job(QhyJobState state) => new(Guid.NewGuid(), "old-run", QhyJobKind.Acquisition, state,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "target", "QHY", null, null, [], [], "manifest.json");
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static QhyServiceClient Client(bool native, Func<HttpRequestMessage, HttpResponseMessage> action) =>
        new("http://127.0.0.1/", new Handler(action), native);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(action(request));
    }
    private static UvexPluginSettings Settings()
    {
        var values = new Dictionary<string, object?>();
        var accessor = Proxy<IPluginOptionsAccessor>((m, a) =>
        {
            if (m.Name.StartsWith("GetValue")) return values.TryGetValue((string)a[0]!, out var v) ? v : a[1];
            if (m.Name.StartsWith("SetValue")) { values[(string)a[0]!] = a[1]; return null; }
            return Default(m.ReturnType);
        });
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        return new(Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType)), accessor);
    }
    private static object? Default(Type t) => t == typeof(void) ? null : t.IsValueType ? Activator.CreateInstance(t) : null;
    private static T Proxy<T>(Func<MethodInfo, object?[], object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler ?? ((m, _) => Default(m.ReturnType)); return proxy;
    }
    private class InterfaceProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
