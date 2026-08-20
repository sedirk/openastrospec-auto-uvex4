using System.IO;
using System.Net.Http;
using System.Reflection;
using UvexAdv.Admin;
using UvexAdv.Core;
using Xunit;

namespace UvexAdv.Admin.Tests;

public sealed class SlitIlluminationTests
{
    [Fact]
    public void PresentationMakesCommandStateAndEvidenceScopeExplicit()
    {
        var status = ReadyStatus() with
        {
            SlitIlluminationLedState = UvexOutputState.On,
            SlitIlluminationLedCommandedUtc = new DateTimeOffset(2026, 8, 17, 1, 2, 3, TimeSpan.Zero),
            SlitPhotodiodeValue = 250,
            SlitPhotodiodeThreshold = 283,
        };

        var commandState = SlitIlluminationPresentation.FormatCommandState(status);
        var photodiode = SlitIlluminationPresentation.FormatPhotodiode(status);
        var warning = SlitIlluminationPresentation.FormatWarning(status);

        Assert.Contains("定位LED", commandState, StringComparison.Ordinal);
        Assert.Contains("非Calibrex", commandState, StringComparison.Ordinal);
        Assert.Contains("服务最近成功命令：开", commandState, StringComparison.Ordinal);
        Assert.Contains("250", photodiode, StringComparison.Ordinal);
        Assert.Contains("283", photodiode, StringComparison.Ordinal);
        Assert.Contains("未超过阈值", warning, StringComparison.Ordinal);
        Assert.Contains("不是 G3 图像中的光学狭缝判定", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void PhotodiodeAboveThresholdHasNoWarning()
    {
        var status = ReadyStatus() with
        {
            SlitIlluminationLedState = UvexOutputState.On,
            SlitPhotodiodeValue = 284,
            SlitPhotodiodeThreshold = 283,
        };

        Assert.Empty(SlitIlluminationPresentation.FormatWarning(status));
    }

    [Fact]
    public async Task DisposeTurnsLedOffBeforeReleasingLease()
    {
        var api = new FakeUvexApiClient { IlluminatedPhotodiodeValue = 250 };
        var viewModel = new MainViewModel(api);
        Assert.False(viewModel.TurnOnSlitIlluminationCommand.CanExecute(null));

        await viewModel.AcquireLeaseAsync();
        Assert.True(viewModel.TurnOnSlitIlluminationCommand.CanExecute(null));
        await viewModel.TurnOnSlitIlluminationAsync();

        Assert.Contains(viewModel.Messages, message =>
            message.Contains("未超过阈值", StringComparison.Ordinal) &&
            message.Contains("不是 G3 图像中的光学狭缝判定", StringComparison.Ordinal));

        await viewModel.DisposeAsync();

        Assert.Equal(
            ["lease:acquire", "illumination:on", "illumination:off", "lease:release"],
            api.Calls);
        Assert.DoesNotContain(viewModel.Messages, message =>
            message.Contains("关灯未确认", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostOnResponseAttemptsOffAndDoesNotInventOffConfirmation()
    {
        var api = new FakeUvexApiClient { ThrowAfterAcceptingOn = true, FailOff = true };
        var viewModel = new MainViewModel(api);
        await viewModel.AcquireLeaseAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.TurnOnSlitIlluminationAsync());

        Assert.Equal(["lease:acquire", "illumination:on", "illumination:off"], api.Calls);
        Assert.Contains(viewModel.Messages, message =>
            message.Contains("关灯未确认", StringComparison.Ordinal) &&
            message.Contains("不能推定实际状态", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.Messages, message =>
            message.Contains("定位LED关灯命令已确认", StringComparison.Ordinal));

        await viewModel.DisposeAsync();
    }

    [Fact]
    public void XamlExposesExplicitManualControls()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));

        Assert.Contains("定位LED（非Calibrex）", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"开启定位LED\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding TurnOnSlitIlluminationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"关闭定位LED\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding TurnOffSlitIlluminationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("不判定 G3 图像中的光学狭缝", xaml, StringComparison.Ordinal);
    }

    private static UvexDeviceStatus ReadyStatus() => new()
    {
        ConnectionState = DeviceConnectionState.Ready,
        Capabilities = UvexCapabilities.MotorizedSlit | UvexCapabilities.SlitPhotodiode,
        SlitPhotodiodeEnabled = true,
    };

    private sealed class FakeUvexApiClient : IUvexApiClient
    {
        private UvexDeviceStatus status = ReadyStatus();

        public List<string> Calls { get; } = [];
        public int IlluminatedPhotodiodeValue { get; init; } = 350;
        public bool ThrowAfterAcceptingOn { get; init; }
        public bool FailOff { get; init; }

        public Task<UvexDeviceStatus?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UvexDeviceStatus?>(status);

        public Task<LeaseResponse> AcquireLeaseAsync(CancellationToken cancellationToken)
        {
            Calls.Add("lease:acquire");
            return Task.FromResult(new LeaseResponse("test-token", "test", DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        public Task ReleaseLeaseAsync(string token, CancellationToken cancellationToken)
        {
            Calls.Add("lease:release");
            return Task.CompletedTask;
        }

        public Task<LeaseResponse> RenewLeaseAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(new LeaseResponse(token, "test", DateTimeOffset.UtcNow.AddMinutes(1)));

        public Task<OperationResponse> PostOperationAsync(
            string path,
            object? body,
            string? leaseToken,
            CancellationToken cancellationToken)
        {
            Assert.Equal("test-token", leaseToken);
            Assert.Equal("/api/v1/slit/illumination", path);
            var enabled = ReadEnabled(body);
            Calls.Add(enabled ? "illumination:on" : "illumination:off");

            if (enabled)
            {
                status = status with
                {
                    SlitIlluminationLedState = UvexOutputState.On,
                    SlitIlluminationLedCommandedUtc = DateTimeOffset.UtcNow,
                    SlitPhotodiodeValue = IlluminatedPhotodiodeValue,
                    SlitPhotodiodeThreshold = 283,
                };
                if (ThrowAfterAcceptingOn)
                {
                    throw new HttpRequestException("response lost");
                }
            }
            else
            {
                if (FailOff)
                {
                    throw new HttpRequestException("off failed");
                }

                status = status with
                {
                    SlitIlluminationLedState = UvexOutputState.Off,
                    SlitIlluminationLedCommandedUtc = DateTimeOffset.UtcNow,
                    SlitPhotodiodeValue = 100,
                    SlitPhotodiodeThreshold = 283,
                };
            }

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OperationResponse(
                Guid.NewGuid(),
                "slit.illumination",
                "Succeeded",
                now,
                now,
                null));
        }

        public Task<OperationResponse?> GetOperationAsync(Guid id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Terminal fake operations should not be polled.");

        public void Dispose()
        {
        }

        private static bool ReadEnabled(object? body)
        {
            var property = body?.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(body) as bool?
                ?? throw new InvalidOperationException("Missing enabled request property.");
        }
    }
}
