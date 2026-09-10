using System.Globalization;
using System.Text.RegularExpressions;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class PhotometryUiPresentationTests
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void EveryWorkerStateHasLocalizedOperatorWording()
    {
        foreach (var state in Enum.GetValues<QhyJobState>())
        {
            var zh = PhotometryUiPresentation.JobState(state, Chinese);
            var en = PhotometryUiPresentation.JobState(state, English);
            Assert.Matches("[\\u3400-\\u9fff]", zh);
            Assert.DoesNotContain(state.ToString(), zh, StringComparison.Ordinal);
            Assert.DoesNotMatch("[\\u3400-\\u9fff]", en);
        }
        Assert.Contains("尚未确认", PhotometryUiPresentation.JobState(QhyJobState.Cancelling, Chinese));
        Assert.Contains("等待当前帧", PhotometryUiPresentation.JobState(QhyJobState.Pausing, Chinese));
    }

    [Theory]
    [InlineData("PHOTOMETRY_SHARED_DEVICE_FORBIDDEN: Telescope,Guider", "取消")]
    [InlineData("PHOTOMETRY_NATIVE_FITS_REQUIRED: fail", "未压缩 FITS")]
    [InlineData("PHOTOMETRY_LEGACY_OWNER_RUNNING: fail", "释放测光设备")]
    [InlineData("PHOTOMETRY_DEVICE_BINDING_CHANGED: fail", "记录所选设备")]
    [InlineData("PHOTOMETRY_SHARED_TRIGGER_FORBIDDEN: fail", "换滤镜时暂停导星")]
    [InlineData("PHOTOMETRY_ENDPOINT_INVALID", "地址未保存")]
    [InlineData("PHOTOMETRY_CAMERA_BUSY: fail", "其他任务占用")]
    [InlineData("PHOTOMETRY_FOCUS_PENDING: fail", "未确认完成")]
    public void SetupErrorsExplainTheExactNextAction(string raw, string expected)
    {
        Assert.Contains(expected, PhotometryUiPresentation.Error(raw, Chinese));
        Assert.DoesNotMatch("[\\u3400-\\u9fff]", PhotometryUiPresentation.Error(raw, English));
    }

    [Fact]
    public void SavedAddressIsNotReportedAsConnectedOrReady()
    {
        Assert.Contains("尚未验证连接", PhotometryUiPresentation.EndpointSummary("nina://11111111-2222-3333-4444-555555555555", Chinese));
        Assert.Contains("旧版", PhotometryUiPresentation.EndpointSummary("http://127.0.0.1:12345", Chinese));
        Assert.Contains("尚未配置", PhotometryUiPresentation.EndpointSummary("QHYminiCam8M", Chinese));
        Assert.False(PhotometryUiPresentation.IsDifferentProfile("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public void ManualPauseTakesPrecedenceOverARecordedAcquisitionYield()
    {
        var job = new QhyJobSnapshot(Guid.NewGuid(), "fixture-run", QhyJobKind.Photometry, QhyJobState.Paused,
            DateTimeOffset.UnixEpoch, null, null, "fixture-target", "fixture-camera", null, null, [], [], "fixture-manifest",
            TotalFrameCount: 12, YieldedToAcquisitionRequestId: "fixture-request");
        Assert.Contains("人工暂停", PhotometryUiPresentation.JobAdvice(job, true, Chinese));
        Assert.Contains("让路", PhotometryUiPresentation.JobAdvice(job, false, Chinese));
        Assert.Contains("连续测光", PhotometryUiPresentation.JobSummary(job, Chinese));
        Assert.Contains("12 帧", PhotometryUiPresentation.JobSummary(job, Chinese));
        Assert.DoesNotContain("fixture-manifest", PhotometryUiPresentation.JobSummary(job, Chinese));
        Assert.Contains("未压缩 FITS", PhotometryUiPresentation.JobAdvice(
            job with { State = QhyJobState.Faulted, YieldedToAcquisitionRequestId = null,
                Error = "PHOTOMETRY_NATIVE_FITS_REQUIRED", AttentionReason = null }, false, Chinese));
    }

    [Fact]
    public void RoleNamesDoNotRenameEvidenceCodesOrClaimUniversalDeviceSupport()
    {
        Assert.Equal("光谱相机 / 测光相机 / 光谱仪导星相机", CameraRoleLabels.Description("ATR / QHY / G3", Chinese));
        const string code = "G3_FRAME_REUSED";
        var gate = GateResult.Unknown(code, "G3_FRAME_REUSED: ATR585M / QHYminiCam8M original evidence");
        var display = ObservationUiPresentation.Present(ObservationStage.PlaceTargetOnSlit, gate, Chinese);
        Assert.Contains("光谱仪导星相机", display.Summary);
        Assert.Equal(code + ": " + gate.Message, display.TechnicalDetails);
        Assert.Equal(code, gate.Code);
        Assert.Contains("QHYminiCam8M", PhotometryUiPresentation.Error("Select the exact native QHYminiCam8M camera", Chinese));
        foreach (var stage in Enum.GetValues<ObservationStage>())
            Assert.DoesNotMatch("\\b(ATR|QHY|G3)\\b", ObservationUiPresentation.StageName(stage, Chinese));
    }

    [Fact]
    public void CameraTabsUseRolesWhileBindingsKeepTheirStableNames()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates.xaml"));
        Assert.Contains("Header=\"测光相机 · 定位\"", xaml);
        Assert.Contains("Header=\"光谱仪导星相机\"", xaml);
        Assert.Contains("Header=\"光谱相机 · 光谱\"", xaml);
        Assert.Contains("{Binding QhyPreviewImage}", xaml);
        Assert.Contains("{Binding G3PreviewImage}", xaml);
        Assert.Contains("{Binding AtrPreviewImage}", xaml);
    }
}
