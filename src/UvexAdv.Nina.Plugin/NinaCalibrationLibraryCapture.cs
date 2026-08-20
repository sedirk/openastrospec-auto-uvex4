using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using UvexAdv.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed record CalibrationCaptureProgress(
    string Stage,
    int CompletedFrames,
    int TotalFrames,
    string Message,
    string? LastSavedPath = null)
{
    public double Percent => TotalFrames == 0 ? 0 : 100d * CompletedFrames / TotalFrames;
}

internal sealed record CalibrationCaptureResult(
    int RawFrameCount,
    int MasterCount,
    string ConfigurationDirectory,
    string ManifestPath,
    IReadOnlyList<string> Warnings);

internal sealed record CalibrationFrameQuality(
    double MeanAdu,
    double ZeroFraction,
    int SaturatedPixelCount,
    ushort Minimum,
    ushort Maximum);

internal sealed class NinaCalibrationLibraryCapture(
    IProfileService profileService,
    ICameraMediator cameraMediator,
    IImagingMediator imagingMediator,
    IImageDataFactory imageDataFactory,
    IProgress<ApplicationStatus> ninaProgress)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<CalibrationCaptureResult> CaptureAsync(
        CalibrationCapturePlan plan,
        string libraryRoot,
        bool buildMasters,
        IProgress<CalibrationCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        ValidateCamera(plan, requireIdle: true);
        await EnsureTemperatureAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        ValidateTemperature(plan);

        var configurationDirectory = CalibrationLibraryPath.GetConfigurationDirectory(libraryRoot, plan);
        Directory.CreateDirectory(configurationDirectory);
        var manifestDirectory = Path.Combine(configurationDirectory, "manifests");
        Directory.CreateDirectory(manifestDirectory);
        var sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        var manifestPath = Path.Combine(manifestDirectory, $"session-{sessionId}.json");
        var manifest = new CalibrationSessionManifest
        {
            SessionId = sessionId,
            StartedUtc = DateTimeOffset.UtcNow,
            CameraName = plan.CameraName,
            CameraId = plan.CameraId,
            Gain = plan.Gain,
            Offset = plan.Offset,
            Binning = plan.Binning,
            ReadoutModeIndex = plan.ReadoutModeIndex,
            ReadoutModeName = plan.ReadoutModeName,
            TargetTemperatureC = plan.TemperatureC,
            Status = "running",
        };

        var totalFrames = plan.Groups.Sum(group => group.FrameCount);
        var completedFrames = 0;
        var masterCount = 0;
        await WriteManifestAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

        try
        {
            if (plan.WarmupFrameCount > 0)
            {
                var warmupExposure = plan.Groups.FirstOrDefault(group => group.Kind == CalibrationFrameKind.Bias)?.ExposureSeconds
                    ?? Math.Max(cameraMediator.GetInfo().ExposureMin, 0.0001);
                var warmupGroup = new CalibrationCaptureGroup(CalibrationFrameKind.Bias, warmupExposure, plan.WarmupFrameCount);
                for (var warmupNumber = 1; warmupNumber <= plan.WarmupFrameCount; warmupNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateCamera(plan, requireIdle: true);
                    ValidateTemperature(plan);
                    progress.Report(new CalibrationCaptureProgress(
                        "Warmup", completedFrames, totalFrames,
                        $"正在丢弃启动稳定帧 {warmupNumber}/{plan.WarmupFrameCount}（不入库）"));
                    _ = await CaptureFrameAsync(plan, warmupGroup, -warmupNumber, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var group in plan.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateCamera(plan, requireIdle: true);
                ValidateTemperature(plan);
                var rawDirectory = CalibrationLibraryPath.GetRawDirectory(
                    libraryRoot,
                    plan,
                    group,
                    DateOnly.FromDateTime(DateTime.Now));
                Directory.CreateDirectory(rawDirectory);

                RobustFrameAccumulator? accumulator = null;
                ImageProperties? properties = null;
                for (var frameNumber = 1; frameNumber <= group.FrameCount; frameNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateCamera(plan, requireIdle: true);
                    ValidateTemperature(plan);
                    progress.Report(new CalibrationCaptureProgress(
                        group.Kind.ToString(), completedFrames, totalFrames,
                        $"正在采集 {Describe(group)} · {frameNumber}/{group.FrameCount}"));

                    var imageData = await CaptureFrameAsync(plan, group, frameNumber, cancellationToken).ConfigureAwait(false);
                    var quality = AnalyzeFrame(imageData.Data.FlatArray);
                    imageData.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader("MEANADU", quality.MeanAdu, "Mean raw ADU"));
                    imageData.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader("ZEROFRA", quality.ZeroFraction, "Fraction of pixels clipped at zero"));
                    imageData.MetaData.GenericHeaders.Add(new IntMetaDataHeader("SATPIX", quality.SaturatedPixelCount, "Pixels at or above 65520"));
                    properties ??= imageData.Properties;
                    if (imageData.Properties.Width != properties.Width || imageData.Properties.Height != properties.Height)
                    {
                        throw new InvalidOperationException("相机在校准序列中改变了图像尺寸，已停止以免污染库。");
                    }

                    accumulator ??= new RobustFrameAccumulator(imageData.Data.FlatArray.Length);
                    accumulator.Add(imageData.Data.FlatArray);

                    var stem = BuildFrameStem(group, frameNumber);
                    var savedPath = await SaveImageAsync(imageData, rawDirectory, stem, cancellationToken).ConfigureAwait(false);
                    var hash = await ComputeSha256Async(savedPath, cancellationToken).ConfigureAwait(false);
                    completedFrames++;
                    manifest.Frames.Add(new CalibrationFrameManifest
                    {
                        Kind = group.Kind.ToString(),
                        ExposureSeconds = group.ExposureSeconds,
                        FrameNumber = frameNumber,
                        CapturedUtc = DateTimeOffset.UtcNow,
                        RelativePath = Path.GetRelativePath(configurationDirectory, savedPath),
                        Sha256 = hash,
                        TemperatureC = cameraMediator.GetInfo().Temperature,
                        MeanAdu = quality.MeanAdu,
                        ZeroFraction = quality.ZeroFraction,
                        SaturatedPixelCount = quality.SaturatedPixelCount,
                    });
                    if (group.Kind == CalibrationFrameKind.Bias && quality.ZeroFraction > 0.2)
                    {
                        const string warning = "Bias 黑位严重截零（超过 20% 像素为 0）；保留原始帧，但不生成可用于定量校准的 Master Bias。";
                        if (!manifest.Warnings.Contains(warning, StringComparer.Ordinal)) manifest.Warnings.Add(warning);
                    }
                    await WriteManifestAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
                    progress.Report(new CalibrationCaptureProgress(
                        group.Kind.ToString(), completedFrames, totalFrames,
                        $"已保存 {Describe(group)} · {frameNumber}/{group.FrameCount}", savedPath));
                }

                var clippedBias = group.Kind == CalibrationFrameKind.Bias && manifest.Frames
                    .Where(frame => frame.Kind == nameof(CalibrationFrameKind.Bias))
                    .Any(frame => frame.ZeroFraction > 0.2);
                if (buildMasters && !clippedBias && accumulator is { FrameCount: >= 5 } && properties is not null)
                {
                    var masterPath = await SaveMasterAsync(
                        plan, group, accumulator, properties, libraryRoot, cancellationToken).ConfigureAwait(false);
                    manifest.Masters.Add(new CalibrationMasterManifest
                    {
                        Kind = group.Kind.ToString(),
                        ExposureSeconds = group.ExposureSeconds,
                        FrameCount = accumulator.FrameCount,
                        CombineMethod = "one-high/one-low trimmed mean",
                        BiasIncluded = group.Kind == CalibrationFrameKind.Dark,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        RelativePath = Path.GetRelativePath(configurationDirectory, masterPath),
                        Sha256 = await ComputeSha256Async(masterPath, cancellationToken).ConfigureAwait(false),
                    });
                    masterCount++;
                    await WriteManifestAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
                }
            }

            manifest.Status = "complete";
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteManifestAtomicAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
            progress.Report(new CalibrationCaptureProgress(
                "Complete", completedFrames, totalFrames,
                $"校准库采集完成：{completedFrames} 张原始帧，{masterCount} 个 master。",
                configurationDirectory));
            return new CalibrationCaptureResult(completedFrames, masterCount, configurationDirectory, manifestPath, manifest.Warnings.ToArray());
        }
        catch (OperationCanceledException)
        {
            manifest.Status = "cancelled";
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteManifestAtomicAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            manifest.Status = "failed";
            manifest.Error = ex.Message;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteManifestAtomicAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IImageData> CaptureFrameAsync(
        CalibrationCapturePlan plan,
        CalibrationCaptureGroup group,
        int frameNumber,
        CancellationToken cancellationToken)
    {
        var imageType = group.Kind == CalibrationFrameKind.Bias
            ? CaptureSequence.ImageTypes.BIAS
            : CaptureSequence.ImageTypes.DARK;
        var sequence = new CaptureSequence
        {
            ExposureTime = group.ExposureSeconds,
            ImageType = imageType,
            Binning = new BinningMode(plan.Binning, plan.Binning),
            Gain = plan.Gain,
            Offset = plan.Offset,
            TotalExposureCount = 1,
            ProgressExposureCount = 0,
            Dither = false,
            EnableSubSample = false,
        };

        var exposure = await imagingMediator.CaptureImage(
            sequence,
            cancellationToken,
            ninaProgress,
            $"UVEX-ADV {group.Kind} {frameNumber}").ConfigureAwait(false);
        var imageData = await exposure.ToImageData(ninaProgress, cancellationToken).ConfigureAwait(false);
        if (imageData.Properties.IsBayered)
        {
            throw new InvalidOperationException("当前 N.I.N.A. 相机返回 Bayer 图像；校准库只允许绑定的单色 ATR585M。");
        }

        imageData.MetaData.Image.ImageType = imageType;
        imageData.MetaData.Image.ExposureNumber = frameNumber;
        imageData.MetaData.Image.ExposureTime = group.ExposureSeconds;
        AddUvexHeaders(imageData.MetaData, plan, group, frameNumber, isMaster: false, frameCount: 1);
        return imageData;
    }

    private async Task<string> SaveMasterAsync(
        CalibrationCapturePlan plan,
        CalibrationCaptureGroup group,
        RobustFrameAccumulator accumulator,
        ImageProperties properties,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        var metadata = new ImageMetaData
        {
            Image = new ImageParameter
            {
                ImageType = group.Kind == CalibrationFrameKind.Bias ? "MASTER BIAS" : "MASTER DARK",
                ExposureNumber = 0,
                ExposureTime = group.ExposureSeconds,
                Binning = $"{plan.Binning}x{plan.Binning}",
                ExposureStart = DateTime.UtcNow,
                ExposureMidPoint = DateTime.UtcNow,
            },
            Camera = new CameraParameter
            {
                Id = plan.CameraId,
                Name = plan.CameraName,
                BinX = plan.Binning,
                BinY = plan.Binning,
                Gain = plan.Gain,
                Offset = plan.Offset,
                Temperature = cameraMediator.GetInfo().Temperature,
                SetPoint = plan.TemperatureC,
                ReadoutModeIndex = plan.ReadoutModeIndex,
                ReadoutModeName = plan.ReadoutModeName,
            },
        };
        AddUvexHeaders(metadata, plan, group, 0, isMaster: true, accumulator.FrameCount);
        metadata.GenericHeaders.Add(new StringMetaDataHeader(
            "COMBMETH", "TRIMMEAN", "One highest and one lowest pixel rejected"));
        metadata.GenericHeaders.Add(new BoolMetaDataHeader(
            "DARKBIAS", group.Kind == CalibrationFrameKind.Dark, "Master dark still includes bias signal"));

        var master = imageDataFactory.CreateBaseImageData(
            accumulator.BuildMaster(),
            properties.Width,
            properties.Height,
            properties.BitDepth,
            false,
            metadata);
        var masterDirectory = CalibrationLibraryPath.GetMasterDirectory(libraryRoot, plan, group);
        Directory.CreateDirectory(masterDirectory);
        var kind = group.Kind == CalibrationFrameKind.Bias ? "MasterBias" : $"MasterDark_{group.ExposureKey}";
        var stem = $"{kind}_{DateTime.UtcNow:yyyyMMddTHHmmssZ}_N{accumulator.FrameCount}";
        return await SaveImageAsync(master, masterDirectory, stem, cancellationToken).ConfigureAwait(false);
    }

    private static void AddUvexHeaders(
        ImageMetaData metadata,
        CalibrationCapturePlan plan,
        CalibrationCaptureGroup group,
        int frameNumber,
        bool isMaster,
        int frameCount)
    {
        metadata.GenericHeaders.Add(new StringMetaDataHeader("UVEXLIB", "UVEX-ADV-1", "UVEX-ADV calibration library schema"));
        metadata.GenericHeaders.Add(new StringMetaDataHeader("CALTYPE", group.Kind.ToString().ToUpperInvariant(), "Calibration frame type"));
        metadata.GenericHeaders.Add(new StringMetaDataHeader("CAMID", plan.CameraId, "Stable N.I.N.A. camera DeviceId"));
        metadata.GenericHeaders.Add(new IntMetaDataHeader("READMODE", plan.ReadoutModeIndex, "N.I.N.A. camera readout mode index"));
        metadata.GenericHeaders.Add(new StringMetaDataHeader("RDMODNAM", plan.ReadoutModeName, "N.I.N.A. camera readout mode name"));
        metadata.GenericHeaders.Add(new DoubleMetaDataHeader("SET-TEMP", plan.TemperatureC, "Requested sensor temperature [C]"));
        metadata.GenericHeaders.Add(new BoolMetaDataHeader("MASTER", isMaster, "Combined calibration product"));
        metadata.GenericHeaders.Add(new IntMetaDataHeader("NCOMBINE", frameCount, "Number of combined input frames"));
        if (!isMaster)
        {
            metadata.GenericHeaders.Add(new IntMetaDataHeader("FRAMENO", frameNumber, "Frame number in UVEX-ADV session"));
        }
    }

    private async Task<string> SaveImageAsync(
        IImageData imageData,
        string directory,
        string stem,
        CancellationToken cancellationToken)
    {
        var saveInfo = new FileSaveInfo(profileService)
        {
            FilePath = directory,
            FilePattern = stem,
            FileType = FileTypeEnum.FITS,
            ForceExtension = ".fits",
            FITSUseLegacyWriter = false,
        };
        var savedPath = await imageData.SaveToDisk(saveInfo, cancellationToken, true).ConfigureAwait(false);
        if (!File.Exists(savedPath))
        {
            throw new IOException($"N.I.N.A. 报告已保存，但找不到输出文件：{savedPath}");
        }

        return savedPath;
    }

    private async Task EnsureTemperatureAsync(
        CalibrationCapturePlan plan,
        IProgress<CalibrationCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        var info = cameraMediator.GetInfo();
        if (!info.CanSetTemperature)
        {
            throw new InvalidOperationException("ATR585M 未报告可控制冷，无法为暗场库验证温度；请检查相机连接/驱动。");
        }

        if (Math.Abs(info.Temperature - plan.TemperatureC) <= plan.TemperatureToleranceC && info.CoolerOn)
        {
            return;
        }

        progress.Report(new CalibrationCaptureProgress(
            "Cooling", 0, plan.Groups.Sum(group => group.FrameCount),
            $"直接设定 {plan.TemperatureC:0.0} °C，正在等待传感器达到 ±{plan.TemperatureToleranceC:0.0} °C…"));
        _ = await cameraMediator.CoolCamera(
            plan.TemperatureC,
            TimeSpan.Zero,
            ninaProgress,
            cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        var stableSamples = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            info = cameraMediator.GetInfo();
            if (!info.CoolerOn)
            {
                throw new InvalidOperationException("相机制冷在等待过程中关闭，已停止校准任务。");
            }

            if (Math.Abs(info.Temperature - plan.TemperatureC) <= plan.TemperatureToleranceC)
            {
                stableSamples++;
                if (stableSamples >= 3) return;
            }
            else
            {
                stableSamples = 0;
            }

            progress.Report(new CalibrationCaptureProgress(
                "Cooling", 0, plan.Groups.Sum(group => group.FrameCount),
                $"已直接设定 {plan.TemperatureC:0.0} °C；当前 {info.Temperature:0.0} °C，制冷功率 {info.CoolerPower:0}%"));
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        info = cameraMediator.GetInfo();
        throw new InvalidOperationException(
            $"直接设定后 15 分钟仍未达到温度：当前 {info.Temperature:0.0} °C，目标 {plan.TemperatureC:0.0} ± {plan.TemperatureToleranceC:0.0} °C。");
    }

    private void ValidateCamera(CalibrationCapturePlan plan, bool requireIdle)
    {
        var info = cameraMediator.GetInfo();
        if (!info.Connected) throw new InvalidOperationException("N.I.N.A. 中没有连接相机。");
        if (!string.Equals(info.DeviceId, plan.CameraId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"相机 DeviceId 已变化；计划绑定 {plan.CameraId}，当前为 {info.DeviceId}。已停止，绝不尝试其他相机。");
        }

        var identity = string.Join('|', info.Name, info.DisplayName, info.Description, info.DeviceId);
        if (!identity.Contains("ATR585M", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"当前相机“{info.DisplayName ?? info.Name}”不是 ATR585M，已拒绝采集。");
        }

        if (info.ReadoutMode != plan.ReadoutModeIndex)
        {
            throw new InvalidOperationException(
                $"相机读出模式已从 {plan.ReadoutModeIndex} 变为 {info.ReadoutMode}；已停止以免混库。");
        }

        if (requireIdle && info.IsExposing) throw new InvalidOperationException("相机仍在曝光，不能启动校准库任务。");
    }

    private void ValidateTemperature(CalibrationCapturePlan plan)
    {
        var info = cameraMediator.GetInfo();
        if (!info.CoolerOn || Math.Abs(info.Temperature - plan.TemperatureC) > plan.TemperatureToleranceC)
        {
            throw new InvalidOperationException(
                $"校准帧前温度越界：当前 {info.Temperature:0.0} °C，目标 {plan.TemperatureC:0.0} ± {plan.TemperatureToleranceC:0.0} °C。");
        }
    }

    private static string Describe(CalibrationCaptureGroup group) => group.Kind == CalibrationFrameKind.Bias
        ? $"Bias {CalibrationLibraryPath.ExposureKey(group.ExposureSeconds)}"
        : $"Dark {CalibrationLibraryPath.ExposureKey(group.ExposureSeconds)}";

    private static string BuildFrameStem(CalibrationCaptureGroup group, int frameNumber)
    {
        var kind = group.Kind == CalibrationFrameKind.Bias ? "Bias" : $"Dark_{group.ExposureKey}";
        return $"{kind}_{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{frameNumber:D3}";
    }

    private static CalibrationFrameQuality AnalyzeFrame(ReadOnlySpan<ushort> pixels)
    {
        double sum = 0;
        var zeroCount = 0;
        var saturatedCount = 0;
        var minimum = ushort.MaxValue;
        var maximum = ushort.MinValue;
        foreach (var value in pixels)
        {
            sum += value;
            if (value == 0) zeroCount++;
            if (value >= 65_520) saturatedCount++;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }

        return new CalibrationFrameQuality(
            sum / pixels.Length,
            (double)zeroCount / pixels.Length,
            saturatedCount,
            minimum,
            maximum);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteManifestAtomicAsync(
        string path,
        CalibrationSessionManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, true);
    }

    private sealed class CalibrationSessionManifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string SessionId { get; init; } = string.Empty;
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string CameraName { get; init; } = string.Empty;
        public string CameraId { get; init; } = string.Empty;
        public int Gain { get; init; }
        public int Offset { get; init; }
        public short Binning { get; init; }
        public short ReadoutModeIndex { get; init; }
        public string ReadoutModeName { get; init; } = string.Empty;
        public double TargetTemperatureC { get; init; }
        public List<CalibrationFrameManifest> Frames { get; } = [];
        public List<CalibrationMasterManifest> Masters { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    private sealed class CalibrationFrameManifest
    {
        public string Kind { get; init; } = string.Empty;
        public double ExposureSeconds { get; init; }
        public int FrameNumber { get; init; }
        public DateTimeOffset CapturedUtc { get; init; }
        public double TemperatureC { get; init; }
        public double MeanAdu { get; init; }
        public double ZeroFraction { get; init; }
        public int SaturatedPixelCount { get; init; }
        public string RelativePath { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class CalibrationMasterManifest
    {
        public string Kind { get; init; } = string.Empty;
        public double ExposureSeconds { get; init; }
        public int FrameCount { get; init; }
        public string CombineMethod { get; init; } = string.Empty;
        public bool BiasIncluded { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public string RelativePath { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }
}
