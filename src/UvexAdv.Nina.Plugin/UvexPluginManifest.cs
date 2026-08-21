using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Runtime.CompilerServices;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class UvexPluginManifest : PluginBase, INotifyPropertyChanged
{
    private readonly UvexPluginSettings settings;

    [ImportingConstructor]
    public UvexPluginManifest(IProfileService profileService)
    {
        settings = new UvexPluginSettings(profileService);
        profileService.ProfileChanged += (_, _) => RaiseAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ServiceUrl
    {
        get => settings.ServiceUrl;
        set { settings.ServiceUrl = value; Raise(); }
    }

    public string ExpectedCameraName
    {
        get => settings.ExpectedCameraName;
        set { settings.ExpectedCameraName = value; Raise(); }
    }

    public string BoundCameraId
    {
        get => settings.BoundCameraId;
        set { settings.BoundCameraId = value; Raise(); }
    }

    public bool Commissioned
    {
        get => settings.Commissioned;
        set { settings.Commissioned = value; Raise(); }
    }

    public bool SpectralAutofocusCommissioned
    {
        get => settings.SpectralAutofocusCommissioned;
        set
        {
            settings.SpectralAutofocusCommissioned = value;
            Raise();
            Raise(nameof(SpectralAutofocusStatus));
        }
    }

    public bool WavelengthLockCommissioned
    {
        get => settings.WavelengthLockCommissioned;
        set
        {
            settings.WavelengthLockCommissioned = value;
            Raise();
            Raise(nameof(WavelengthLockStatus));
        }
    }

    public double ExposureSeconds
    {
        get => settings.ExposureSeconds;
        set { settings.ExposureSeconds = value; Raise(); }
    }

    public int Gain { get => settings.Gain; set { settings.Gain = value; Raise(); } }
    public int Offset { get => settings.Offset; set { settings.Offset = value; Raise(); } }
    public short Binning { get => settings.Binning; set { settings.Binning = value; Raise(); } }

    public string FocusLinePixelsCsv
    {
        get => settings.FocusLinePixelsCsv;
        set { settings.FocusLinePixelsCsv = value; Raise(); Raise(nameof(SpectralAutofocusStatus)); }
    }

    public int RoiX { get => settings.RoiX; set { settings.RoiX = value; Raise(); } }
    public int RoiY { get => settings.RoiY; set { settings.RoiY = value; Raise(); } }
    public int RoiWidth { get => settings.RoiWidth; set { settings.RoiWidth = value; Raise(); } }
    public int RoiHeight { get => settings.RoiHeight; set { settings.RoiHeight = value; Raise(); } }
    public int ApertureStart { get => settings.ApertureStart; set { settings.ApertureStart = value; Raise(); } }
    public int ApertureLength { get => settings.ApertureLength; set { settings.ApertureLength = value; Raise(); } }
    public DispersionAxis DispersionAxis { get => settings.DispersionAxis; set { settings.DispersionAxis = value; Raise(); } }
    public bool AutoRepairAtr585mSdkWrap { get => settings.AutoRepairAtr585mSdkWrap; set { settings.AutoRepairAtr585mSdkWrap = value; Raise(); } }
    public int SdkWrapShiftPixels { get => settings.SdkWrapShiftPixels; set { settings.SdkWrapShiftPixels = value; Raise(); } }
    public double SdkWrapSeamSigma { get => settings.SdkWrapSeamSigma; set { settings.SdkWrapSeamSigma = value; Raise(); } }
    public int FocusStepSize { get => settings.FocusStepSize; set { settings.FocusStepSize = value; Raise(); Raise(nameof(SpectralAutofocusStatus)); } }
    public int FocusMinimum { get => settings.FocusMinimum; set { settings.FocusMinimum = value; Raise(); Raise(nameof(SpectralAutofocusStatus)); } }
    public int FocusMaximum { get => settings.FocusMaximum; set { settings.FocusMaximum = value; Raise(); Raise(nameof(SpectralAutofocusStatus)); } }
    public int FocusBacklash { get => settings.FocusBacklash; set { settings.FocusBacklash = value; Raise(); Raise(nameof(SpectralAutofocusStatus)); } }
    public double WavelengthReferencePixel { get => settings.WavelengthReferencePixel; set { settings.WavelengthReferencePixel = value; Raise(); } }
    public double WavelengthTargetPixel { get => settings.WavelengthTargetPixel; set { settings.WavelengthTargetPixel = value; Raise(); } }
    public double GratingStepsPerPixel { get => settings.GratingStepsPerPixel; set { settings.GratingStepsPerPixel = value; Raise(); } }

    public string WavelengthReferencePixelText
    {
        get => FormatConfiguredDouble(settings.WavelengthReferencePixel);
        set
        {
            settings.WavelengthReferencePixel = ParseConfiguredDouble(value, double.NaN);
            Raise();
            Raise(nameof(WavelengthReferencePixel));
            Raise(nameof(WavelengthLockStatus));
        }
    }

    public string WavelengthTargetPixelText
    {
        get => FormatConfiguredDouble(settings.WavelengthTargetPixel);
        set
        {
            settings.WavelengthTargetPixel = ParseConfiguredDouble(value, double.NaN);
            Raise();
            Raise(nameof(WavelengthTargetPixel));
            Raise(nameof(WavelengthLockStatus));
        }
    }

    public string GratingStepsPerPixelText
    {
        get => settings.GratingStepsPerPixel == 0 ? string.Empty : FormatConfiguredDouble(settings.GratingStepsPerPixel);
        set
        {
            settings.GratingStepsPerPixel = ParseConfiguredDouble(value, 0);
            Raise();
            Raise(nameof(GratingStepsPerPixel));
            Raise(nameof(WavelengthLockStatus));
        }
    }

    public string SpectralAutofocusStatus
    {
        get
        {
            if (!settings.SpectralAutofocusCommissioned)
            {
                return "未授权：只允许查看和影子采集，不允许自动移动 UVEX M2。";
            }

            if (string.IsNullOrWhiteSpace(settings.BoundCameraId))
            {
                return "配置不完整：必须先在自动观测的 ATR 光谱页或校准库面板绑定 ATR585M DeviceId。";
            }

            int focusLineCount;
            try
            {
                focusLineCount = settings.ParseFocusLines().Count;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                return "配置不完整：谱线位置必须是以英文逗号分隔的有限像素数值。";
            }

            if (focusLineCount < 3 || settings.FocusStepSize <= 0 ||
                settings.FocusMinimum >= settings.FocusMaximum || settings.FocusBacklash < 0)
            {
                return "配置不完整：需要至少三条谱线、正步长、有效绝对行程和非负预压量。";
            }

            return "已单独授权 UVEX M2 谱线对焦；运行时仍会复核 DeviceId、服务状态和软件行程。";
        }
    }

    public string WavelengthLockStatus
    {
        get
        {
            if (!settings.WavelengthLockCommissioned)
            {
                return "未授权：不会自动移动 UVEX 光栅。";
            }

            if (string.IsNullOrWhiteSpace(settings.BoundCameraId))
            {
                return "配置不完整：必须先在自动观测的 ATR 光谱页或校准库面板绑定 ATR585M DeviceId。";
            }

            if (!double.IsFinite(settings.WavelengthReferencePixel) ||
                !double.IsFinite(settings.WavelengthTargetPixel) ||
                !double.IsFinite(settings.GratingStepsPerPixel) ||
                settings.GratingStepsPerPixel == 0)
            {
                return "配置不完整：参考像素、目标像素和带符号的光栅步/像素均未就绪。";
            }

            return "已单独授权光栅波长锁定；运行时仍会复核 DeviceId、服务状态和闭环收敛。";
        }
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RaiseAll()
    {
        Raise(nameof(ServiceUrl));
        Raise(nameof(ExpectedCameraName));
        Raise(nameof(BoundCameraId));
        Raise(nameof(Commissioned));
        Raise(nameof(SpectralAutofocusCommissioned));
        Raise(nameof(WavelengthLockCommissioned));
        Raise(nameof(ExposureSeconds));
        Raise(nameof(Gain));
        Raise(nameof(Offset));
        Raise(nameof(Binning));
        Raise(nameof(FocusLinePixelsCsv));
        Raise(nameof(RoiX));
        Raise(nameof(RoiY));
        Raise(nameof(RoiWidth));
        Raise(nameof(RoiHeight));
        Raise(nameof(ApertureStart));
        Raise(nameof(ApertureLength));
        Raise(nameof(DispersionAxis));
        Raise(nameof(AutoRepairAtr585mSdkWrap));
        Raise(nameof(SdkWrapShiftPixels));
        Raise(nameof(SdkWrapSeamSigma));
        Raise(nameof(FocusStepSize));
        Raise(nameof(FocusMinimum));
        Raise(nameof(FocusMaximum));
        Raise(nameof(FocusBacklash));
        Raise(nameof(WavelengthReferencePixel));
        Raise(nameof(WavelengthTargetPixel));
        Raise(nameof(GratingStepsPerPixel));
        Raise(nameof(WavelengthReferencePixelText));
        Raise(nameof(WavelengthTargetPixelText));
        Raise(nameof(GratingStepsPerPixelText));
        Raise(nameof(SpectralAutofocusStatus));
        Raise(nameof(WavelengthLockStatus));
    }

    private static string FormatConfiguredDouble(double value) =>
        double.IsFinite(value) ? value.ToString("G12", CultureInfo.CurrentCulture) : string.Empty;

    private static double ParseConfiguredDouble(string? value, double unconfiguredValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return unconfiguredValue;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : unconfiguredValue;
    }
}
