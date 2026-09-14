using System.ComponentModel.Composition;
using System.Reflection;
using System.Text.Json;
using NINA.Core.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class SepMainFocusViewModelTests
{
    [Fact]
    public async Task InvalidFormIsVisibleAndBothCommandAndSharedEntryRejectIt()
    {
        using var vm = ViewModel(Settings(), () => null);
        Assert.Contains("最小、最大", vm.AvailabilityMessage);
        Assert.False(vm.StartCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ExecuteAsync(CancellationToken.None));
        Assert.Contains("未启动", vm.Status);
        Assert.False(vm.IsBusy);
        vm.MinimumPosition = 4600; vm.MaximumPosition = 5500;
        Assert.True(vm.StartCommand.CanExecute(null));
        vm.ExposureSeconds = .1;
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("曝光", vm.AvailabilityMessage);
        vm.ExposureSeconds = 3;
        Assert.Equal(3, vm.ExposureSeconds);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task BusyObservationNeverLetsFocusTouchADeviceButDraftCanBeSaved()
    {
        var state = ObservationRunState.Cancelling;
        using var vm = ViewModel(Settings(valid: true), () => SepMainFocusViewModel.ObservationBlockReason(state));
        Assert.Contains("取消并收尾", vm.AvailabilityMessage);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ExecuteAsync(CancellationToken.None));
        state = ObservationRunState.Cancelled;
        vm.Refresh();
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void TwoViewsCannotClearEachOthersInvalidInputAndProfileReloadUsesNewDraft()
    {
        var settings = Settings(valid: true);
        using var vm = ViewModel(settings, () => null);
        var a = new object(); var b = new object();
        vm.SetEditorErrors(a, true); vm.SetEditorErrors(b, true); vm.SetEditorErrors(a, false);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.SetEditorErrors(b, false);
        Assert.True(vm.StartCommand.CanExecute(null));
        settings.SepMainFocusOptionsJson = "{}";
        vm.ReloadProfile();
        Assert.Equal(0, vm.MaximumPosition);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Empty(vm.Curve);
    }

    [Theory]
    [InlineData(ObservationRunState.Cancelled)]
    [InlineData(ObservationRunState.Completed)]
    [InlineData(ObservationRunState.Idle)]
    [InlineData(ObservationRunState.Faulted)]
    public void TerminalRunCannotBeResurrectedByQueuedExposureProgress(ObservationRunState state)
    {
        Assert.False(ObservationDockable.AllowsStageProgress(state));
        Assert.Null(SepMainFocusViewModel.ObservationBlockReason(state));
    }

    [Fact]
    public void NativeAutofocusIsOptInAndSharesTheVisibleViewModel()
    {
        Assert.True(typeof(IAutoFocusVMFactory).IsAssignableFrom(typeof(SepMainFocusFactory)));
        Assert.True(typeof(IAutoFocusVM).IsAssignableFrom(typeof(SepMainFocusViewModel)));
        Assert.Contains(typeof(SepMainFocusFactory).GetCustomAttributes<ExportAttribute>(), a => a.ContractType == typeof(IPluggableBehavior));
        // Resolving metadata is not creation of the live dockable or any equipment.
        var provider = new SepMainFocusFactory(new Lazy<ObservationDockable>(() => throw new InvalidOperationException("No hardware in tests")));
        Assert.Contains("PHD2", provider.Name);
        Assert.Equal(typeof(SepMainFocusFactory).FullName, provider.ContentId);
    }

    [Fact]
    public void BackendSamplingUsesVisibleSaveWithoutChangingTravelOrStartingHardware()
    {
        var settings = Settings(valid: true);
        using var vm = ViewModel(settings, () => "Device unavailable");
        var before = vm.OptionsSnapshot;
        var draft = new SepFocusSamplingDraft(5, 5000, 100);
        Assert.True(vm.SaveCommand.CanExecute(draft));
        vm.SaveCommand.Execute(draft);
        Assert.Equal(before with { Frames = 5, ExposureMilliseconds = 5000 }, vm.OptionsSnapshot);
        Assert.Equal(vm.OptionsSnapshot, JsonSerializer.Deserialize<SepMainFocusOptions>(settings.SepMainFocusOptionsJson));
        Assert.Equal(5, vm.Frames);
        Assert.Equal(5, vm.ExposureSeconds);
        Assert.Contains("未启动", vm.Status);
        Assert.False(vm.IsBusy);
        vm.SaveCommand.Execute(new SepFocusSamplingDraft(100, 5000, 100));
        Assert.Equal(5, vm.Frames);
        Assert.False(vm.SaveCommand.CanExecute(new object()));
        vm.SetEditorErrors(new object(), true);
        Assert.False(vm.SaveCommand.CanExecute(draft));
    }

    [Fact]
    public void SamplingSaveCannotChangeRunningDraftOrSupplyMissingMotionLimits()
    {
        var current = new SepMainFocusOptions();
        var editable = true;
        var command = new SepFocusSaveCommand(() => current, value => current = value, () => editable);
        var draft = new SepFocusSamplingDraft(5, 5000, 50);
        Assert.False(command.CanExecute(draft));
        current = current with { MinimumPosition = 4600, MaximumPosition = 5500 };
        editable = false;
        command.Execute(draft);
        Assert.Equal(3, current.Frames);
        editable = true;
        Assert.True(command.CanExecute(draft));
        command.Execute(draft);
        Assert.Equal(4600, current.MinimumPosition);
        Assert.Equal(5500, current.MaximumPosition);
        Assert.Equal(50, current.GainPercent);
    }

    private static SepMainFocusViewModel ViewModel(UvexPluginSettings settings, Func<string?> reason) =>
        new(settings, null!, null!, reason, () => "offline-test-profile", () => { }); // Invalid preflight must not dereference either hardware dependency.

    private static UvexPluginSettings Settings(bool valid = false)
    {
        var values = new Dictionary<string, object?>();
        var accessor = Proxy<IPluginOptionsAccessor>((m, a) =>
        {
            if (m.Name.StartsWith("GetValue", StringComparison.Ordinal)) return values.TryGetValue((string)a[0]!, out var v) ? v : a[1];
            if (m.Name.StartsWith("SetValue", StringComparison.Ordinal)) { values[(string)a[0]!] = a[1]; return null; }
            return null;
        });
        var settings = new UvexPluginSettings(Proxy<IProfileService>((_, _) => null), accessor);
        if (valid) settings.SepMainFocusOptionsJson = JsonSerializer.Serialize(new SepMainFocusOptions { MinimumPosition = 4600, MaximumPosition = 5500 });
        return settings;
    }
    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, FakeInterface>(); ((FakeInterface)(object)proxy).Handler = handler; return proxy;
    }
    private class FakeInterface : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    }
}
