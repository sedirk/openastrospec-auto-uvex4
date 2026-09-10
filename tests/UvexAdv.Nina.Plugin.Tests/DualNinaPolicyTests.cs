using System.IO;
using System.Net.Http;
using System.Text.Json;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class DualNinaPolicyTests
{
    [Fact]
    public void WorkerAddressPinsAnExactSeparateProfileWithoutNetworkFallback()
    {
        var id = Guid.NewGuid();
        Assert.True(NinaInstancePolicy.TryWorkerEndpoint($"nina://{id:D}", out var actual));
        Assert.Equal(id, actual);
        Assert.True(QhyServiceClient.IsSupportedEndpoint($"nina://{id:D}"));
        foreach (var address in new[] { "nina://localhost", $"nina://user@{id:D}", $"nina://{id:D}:99",
            $"nina://{id:D}/mount", $"nina://{id:D}?fallback=true", "file:///camera", "http://example.com" })
        {
            Assert.False(NinaInstancePolicy.TryWorkerEndpoint(address, out _));
            Assert.False(QhyServiceClient.IsSupportedEndpoint(address));
        }
    }

    [Theory]
    [InlineData("Telescope")]
    [InlineData("Guider")]
    [InlineData("Dome")]
    [InlineData("SafetyMonitor")]
    [InlineData("Weather")]
    [InlineData("FlatDevice/Cover")]
    [InlineData("Rotator")]
    [InlineData("Switch")]
    public void EverySharedDeviceSelectionIsRejected(string category)
    {
        var error = Assert.Throws<InvalidOperationException>(() => NinaInstancePolicy.ValidateForbiddenSelections(
            new Dictionary<string, string?> { [category] = "fixture-device" }));
        Assert.Contains(category, error.Message);
    }

    [Fact]
    public void NativeNoGuiderAndNoDeviceAreAllowedButUnknownNamesAreNot()
    {
        NinaInstancePolicy.ValidateForbiddenSelections(new Dictionary<string, string?>
            { ["Guider"] = "No_Guider", ["Telescope"] = "No_Device", ["Roof"] = "", ["Cover"] = null });
        Assert.Throws<InvalidOperationException>(() => NinaInstancePolicy.ValidateForbiddenSelections(
            new Dictionary<string, string?> { ["Roof"] = "not-configured-maybe" }));
    }

    [Theory]
    [InlineData(null, "request")]
    [InlineData("night", "")]
    public void NoExposureWithoutNightSetupAndIdempotency(string? night, string? request)
    { Assert.Throws<InvalidDataException>(() => PhotometryWorkerHost.RequireJobBinding(night, request)); }

    [Fact]
    public async Task MalformedOrOversizedPipeFramesAreRejectedBeforeDeserialization()
    {
        foreach (var length in new[] { -1, 0, PhotometryPipeProtocol.MaximumMessageBytes + 1 })
        {
            using var stream = new MemoryStream(BitConverter.GetBytes(length));
            await Assert.ThrowsAsync<InvalidDataException>(() => PhotometryPipeProtocol.ReadAsync<PhotometryPipeRequest>(stream, CancellationToken.None));
        }
    }

    [Fact]
    public async Task CurrentUserPipePinsPeerSessionAndRestartCannotRepeatMutation()
    {
        var worker = Guid.NewGuid(); var master = Guid.NewGuid();
        var identity = new QhyNinaWorkerIdentity(worker, master, Guid.NewGuid(), Environment.ProcessId + 1,
            DateTimeOffset.UtcNow, new string('A', 64), "fixture-camera", "fixture-wheel", "fixture-focuser");
        var acceptedMutations = 0;
        using var server = new PhotometryPipeServer(worker, (request, _) =>
        {
            var status = 200;
            if (request.Method == "POST")
            {
                if (request.WorkerSessionId != identity.SessionId) status = 409;
                else acceptedMutations++;
            }
            return Task.FromResult(new PhotometryPipeResponse(status, "{}", "application/json", [], identity));
        });
        using var http = new HttpClient(new PhotometryPipeHttpHandler(worker, master)) { BaseAddress = new Uri("http://127.0.0.1"), Timeout = TimeSpan.FromSeconds(10) };
        using var initial = await http.GetAsync("/api/v1/camera");
        Assert.True(initial.IsSuccessStatusCode);
        identity = identity with { SessionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.PostAsync("/api/v1/jobs/photometry", new StringContent("{}")));
        Assert.Equal(0, acceptedMutations);
    }

    [Fact]
    public async Task WrongMasterBindingNeverSendsMutation()
    {
        var worker = Guid.NewGuid(); var master = Guid.NewGuid(); var posts = 0;
        var identity = new QhyNinaWorkerIdentity(worker, Guid.NewGuid(), Guid.NewGuid(), Environment.ProcessId + 1,
            DateTimeOffset.UtcNow, new string('A', 64), "fixture-camera", "fixture-wheel", "fixture-focuser");
        using var server = new PhotometryPipeServer(worker, (request, _) =>
        {
            if (request.Method == "POST") posts++;
            return Task.FromResult(new PhotometryPipeResponse(200, "{}", "application/json", [], identity));
        });
        using var http = new HttpClient(new PhotometryPipeHttpHandler(worker, master)) { BaseAddress = new Uri("http://127.0.0.1") };
        await Assert.ThrowsAsync<InvalidDataException>(() => http.PostAsync("/api/v1/camera/connect", null));
        Assert.Equal(0, posts);
    }

    [Fact]
    public void JobSnapshotSeparatesManualInterventionFromQualityFailure()
    {
        var snapshot = new QhyJobSnapshot(Guid.NewGuid(), "run", QhyJobKind.Photometry, QhyJobState.Paused,
            DateTimeOffset.UtcNow, null, null, "star", "fixture-camera", null, null, [], [], "manifest",
            NightSetupId: "night", OperatorInterventionRequired: true);
        var roundtrip = JsonSerializer.Deserialize<QhyJobSnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.True(roundtrip.OperatorInterventionRequired);
        Assert.Equal("night", roundtrip.NightSetupId);
    }

    [Fact]
    public void DuplicateWorkerProfileCannotClaimTheSameLocalEndpoint()
    {
        var profile = Guid.NewGuid();
        using var first = new PhotometryPipeServer(profile, (_, _) => throw new NotSupportedException());
        Assert.Throws<IOException>(() => new PhotometryPipeServer(profile, (_, _) => throw new NotSupportedException()));
    }

    [Fact]
    public void DifferentWorkerProfilesCannotClaimTheSamePhysicalDeviceBinding()
    {
        var camera = Guid.NewGuid().ToString(); var wheel = Guid.NewGuid().ToString(); var focus = Guid.NewGuid().ToString();
        using var first = new PhotometryDeviceClaims(camera, wheel, focus);
        Assert.Throws<IOException>(() => new PhotometryDeviceClaims(camera, "other-wheel", "other-focus"));
        Assert.Throws<IOException>(() => new PhotometryDeviceClaims("other-camera", wheel, "other-focus"));
        using var independent = new PhotometryDeviceClaims("independent-camera", "independent-wheel", "independent-focus");
    }

    [Fact]
    public void ReceiverTemplateRejectsNativeExposureAndNestedParallelWork()
    {
        var sequential = new NINA.Sequencer.Container.SequentialContainer();
        sequential.Add(new NINA.Sequencer.Container.ParallelContainer());
        var exposure = (NINA.Sequencer.SequenceItem.ISequenceItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(NINA.Sequencer.SequenceItem.Imaging.TakeExposure));
        Assert.Throws<InvalidOperationException>(() => PhotometryReceiverTemplatePolicy.Validate(exposure));
        Assert.Throws<InvalidOperationException>(() => PhotometryReceiverTemplatePolicy.Validate(sequential));
        Assert.Throws<InvalidOperationException>(() => PhotometryReceiverTemplatePolicy.Validate(new NINA.Sequencer.Container.ParallelContainer()));
        Assert.Throws<InvalidOperationException>(() => PhotometryReceiverTemplatePolicy.Validate(new NINA.Sequencer.Container.SequentialContainer()));
    }
}
