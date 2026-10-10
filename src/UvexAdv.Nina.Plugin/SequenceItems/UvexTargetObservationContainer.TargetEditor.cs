using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.DragDrop;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

// These are planning operations only. No device mediator or execution command is
// exposed here: each command edits this target or opens N.I.N.A.'s framing view.
public sealed partial class UvexTargetObservationContainer
{
    private readonly IFramingAssistantVM? framingAssistant;
    private readonly IApplicationMediator? applicationMediator;
    private readonly IPlanetariumFactory? planetariumFactory;
    private RelayCommand<object?> dropTargetCommand = null!;
    private SimpleAsyncCommand coordsFromPlanetariumCommand = null!;
    private SimpleAsyncCommand coordsToFramingCommand = null!;
    private int targetEditorBusy;
    private long targetEditorGeneration;
    private string targetEditorStatus = string.Empty;

    [JsonIgnore] public ICommand DropTargetCommand => dropTargetCommand;
    [JsonIgnore] public ICommand CoordsFromPlanetariumCommand => coordsFromPlanetariumCommand;
    [JsonIgnore] public ICommand CoordsToFramingCommand => coordsToFramingCommand;
    [JsonIgnore] public bool TargetEditorBusy => Volatile.Read(ref targetEditorBusy) != 0;
    [JsonIgnore] public bool CanEditTargetIdentity => CanEditPlan && !TargetEditorBusy;
    [JsonIgnore] public string TargetEditorStatus
    {
        get => targetEditorStatus;
        private set { targetEditorStatus = value; RaisePropertyChanged(); }
    }

    private void InitializeTargetEditorCommands()
    {
        dropTargetCommand = new RelayCommand<object?>(p => TryImportDroppedTarget(p),
            p => CanEditTargetIdentity && p is DropIntoParameters { Source: TargetSequenceContainer });
        coordsFromPlanetariumCommand = new SimpleAsyncCommand(
            async () => { await ImportTargetFromPlanetariumAsync(); },
            () => CanEditTargetIdentity && planetariumFactory is not null);
        coordsToFramingCommand = new SimpleAsyncCommand(
            async () => { await SendTargetToFramingAsync(); },
            () => CanEditTargetIdentity && framingAssistant is not null && applicationMediator is not null);
    }

    private void RefreshTargetEditorCommands()
    {
        RaisePropertyChanged(nameof(TargetEditorBusy));
        RaisePropertyChanged(nameof(CanEditTargetIdentity));
        dropTargetCommand?.NotifyCanExecuteChanged();
        coordsFromPlanetariumCommand?.RaiseCanExecuteChanged();
        coordsToFramingCommand?.RaiseCanExecuteChanged();
    }

    internal bool TryImportDroppedTarget(object? parameter)
    {
        // A regular instruction/sequence must never enter the immutable canonical
        // stage list through the identity replacement drop zone.
        if (!CanEditTargetIdentity || parameter is not DropIntoParameters { Source: TargetSequenceContainer source })
            return false;
        try
        {
            var imported = source.Container.Target;
            ApplyImportedTarget(imported.TargetName, imported.InputCoordinates?.Coordinates, imported.PositionAngle);
            TargetEditorStatus = UiText("已替换本目标；曝光、导星选项及其它目标未改变。");
            return true;
        }
        catch (Exception)
        {
            TargetEditorStatus = UiText("目标未替换：请检查来源坐标，并确认序列仍空闲。");
            return false;
        }
    }

    internal async Task<bool> ImportTargetFromPlanetariumAsync()
    {
        if (planetariumFactory is null || !TryBeginTargetEditorOperation()) return false;
        var stamp = CaptureTargetEditorStamp();
        try
        {
            TargetEditorStatus = UiText("正在读取星图软件所选目标；不连接或移动设备。");
            var planetarium = planetariumFactory.GetPlanetarium();
            var imported = await planetarium.GetTarget().WaitAsync(TimeSpan.FromSeconds(30));
            RequireUnchangedEditableTarget(stamp);
            if (imported?.Coordinates is null)
            {
                TargetEditorStatus = UiText("星图软件没有返回已选目标；原目标保持不变。");
                return false;
            }
            // Snapshot before another await: the native planetarium result is
            // mutable and must not remain an alias into the saved target plan.
            var name = imported.Name;
            var coordinates = imported.Coordinates.Clone();
            var angle = planetarium.CanGetRotationAngle
                ? await planetarium.GetRotationAngle().WaitAsync(TimeSpan.FromSeconds(30))
                : 0;
            RequireUnchangedEditableTarget(stamp);
            ApplyImportedTarget(name, coordinates, double.IsFinite(angle) ? angle : 0);
            TargetEditorStatus = UiText("已导入星图目标；只修改本目标名称与坐标，没有执行指向。");
            return true;
        }
        catch (Exception)
        {
            TargetEditorStatus = UiText("读取目标未完成或编辑状态已变化；原目标保持不变，请在空闲时重试。");
            return false;
        }
        finally { EndTargetEditorOperation(); }
    }

    internal async Task<bool> SendTargetToFramingAsync()
    {
        if (framingAssistant is null || applicationMediator is null || !TryBeginTargetEditorOperation()) return false;
        var stamp = CaptureTargetEditorStamp();
        try
        {
            var coordinates = DetachedJ2000(Target.InputCoordinates?.Coordinates);
            var dso = new DeepSkyObject(TargetName, coordinates, profileService.ActiveProfile.AstrometrySettings.Horizon)
            {
                RotationPositionAngle = Target.PositionAngle
            };
            TargetEditorStatus = UiText("正在发送到构图助手；不会指向、居中或曝光。");
            RequireUnchangedEditableTarget(stamp);
            var accepted = await framingAssistant.SetCoordinates(dso).WaitAsync(TimeSpan.FromSeconds(30));
            RequireUnchangedEditableTarget(stamp);
            if (!accepted)
            {
                TargetEditorStatus = UiText("构图助手未接受目标；序列目标保持不变。");
                return false;
            }
            applicationMediator.ChangeTab(ApplicationTab.FRAMINGASSISTANT);
            TargetEditorStatus = UiText("已打开构图助手；序列参数未改变，没有执行设备动作。");
            return true;
        }
        catch (Exception)
        {
            TargetEditorStatus = UiText("打开构图助手未完成或序列状态已变化；没有改动序列目标。");
            return false;
        }
        finally { EndTargetEditorOperation(); }
    }

    private bool TryBeginTargetEditorOperation()
    {
        if (!CanEditPlan || Interlocked.CompareExchange(ref targetEditorBusy, 1, 0) != 0) return false;
        if (!CanEditPlan) { EndTargetEditorOperation(); return false; }
        RefreshTargetEditorCommands();
        return true;
    }

    private void EndTargetEditorOperation()
    {
        Volatile.Write(ref targetEditorBusy, 0);
        RefreshTargetEditorCommands();
    }

    private (long Generation, InputTarget Target, string Name, double Ra, double Dec, double Angle) CaptureTargetEditorStamp() =>
        (Volatile.Read(ref targetEditorGeneration), Target, TargetName, RightAscensionDegrees, DeclinationDegrees, Target.PositionAngle);

    private void RequireUnchangedEditableTarget((long Generation, InputTarget Target, string Name, double Ra, double Dec, double Angle) stamp)
    {
        RequireEditablePlan();
        if (stamp != CaptureTargetEditorStamp())
            throw new InvalidOperationException("目标或序列状态已改变，不能应用之前发起的读取结果。");
    }

    private void ApplyImportedTarget(string? name, Coordinates? coordinates, double positionAngle)
    {
        RequireEditablePlan();
        // Validate everything before the first assignment. Import changes identity
        // only, leaving science, calibration, failure, horizon and device settings.
        var detached = DetachedJ2000(coordinates);
        if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(positionAngle))
            throw new ArgumentException("目标名称或坐标无效。");
        Target.InputCoordinates = new InputCoordinates(detached);
        Target.TargetName = name.Trim();
        Target.PositionAngle = positionAngle;
        InvalidateTargetCatalog();
        RaiseTargetPropertiesChanged();
    }

    private static Coordinates DetachedJ2000(Coordinates? coordinates)
    {
        if (coordinates is null) throw new ArgumentException("目标坐标缺失。");
        var detached = coordinates.Transform(Epoch.J2000).Clone();
        if (!double.IsFinite(detached.RADegrees) || detached.RADegrees is < 0 or >= 360 ||
            !double.IsFinite(detached.Dec) || detached.Dec is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(coordinates), "目标坐标超出 J2000 范围。");
        return detached;
    }
}
