using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UvexAdv.Observatory;

/// <summary>Extra optical state absent from the historical single-pair schema.</summary>
public sealed record QhyG3LocalOpticalState(string C11FocuserDeviceId, int C11PositionSteps,
    string Gs350FocusDomainSha256, int? Gs350PositionSteps, string Gs350PositionAuthority, int G3GainPercent)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(C11FocuserDeviceId) && C11PositionSteps >= 0 &&
        QhyG3LocalModelPolicy.IsSha(Gs350FocusDomainSha256) && G3GainPercent is >= 0 and <= 100 &&
        (Gs350PositionSteps is >= 0 && Gs350PositionAuthority == "NativeOwnerPosition" ||
         Gs350PositionSteps is null && Gs350PositionAuthority == "ExplicitManualLock");

    public string ComputeSha256() => QhyG3LocalModelPolicy.HashFields(C11FocuserDeviceId,
        C11PositionSteps.ToString(CultureInfo.InvariantCulture), Gs350FocusDomainSha256.ToUpperInvariant(),
        Gs350PositionSteps?.ToString(CultureInfo.InvariantCulture) ?? "unattestable-manual-position",
        Gs350PositionAuthority, G3GainPercent.ToString(CultureInfo.InvariantCulture));
}

public sealed record QhyG3LocalModelBinding(string CommissioningPresetSha256, string NightSetupId,
    string NightSetupSha256, string InstallationEpochId, string TelescopeId, string QhyCameraId,
    string G3CameraId, string QhyOpticalTrainId, string G3OpticalTrainId, string PierSide,
    int G3WidthPixels, int G3HeightPixels, int G3Binning, QhyG3LocalOpticalState OpticalState)
{
    public bool? ExpectedG3Flipped { get; init; }
}

/// <summary>Construct only after rehashing the pair, source files and focus evidence.</summary>
public sealed record QhyG3VerifiedLocalSample(QhyToG3TransferCandidate Candidate,
    QhyG3LocalOpticalState OpticalState, string FocusEvidencePath, string FocusEvidenceSha256);

public sealed record QhyG3LocalModelLimits(TimeSpan MaximumSampleAge, TimeSpan MaximumCurrentQhyAge,
    double MaximumLocalSkyRadiusDegrees, double MaximumPredictionUncertaintyArcseconds,
    int MaximumSamples = 24)
{
    public bool IsValid => MaximumSampleAge > TimeSpan.Zero && MaximumSampleAge <= TimeSpan.FromMinutes(30) &&
        MaximumCurrentQhyAge > TimeSpan.Zero && MaximumCurrentQhyAge <= TimeSpan.FromMinutes(1) &&
        double.IsFinite(MaximumLocalSkyRadiusDegrees) && MaximumLocalSkyRadiusDegrees is > 0 and <= 5 &&
        double.IsFinite(MaximumPredictionUncertaintyArcseconds) && MaximumPredictionUncertaintyArcseconds > 0 &&
        MaximumSamples is >= 3 and <= 64;
}

public sealed record QhyG3LocalModelRequest(QhyG3LocalModelBinding Binding, QhyG3SolvedFrame CurrentQhy,
    IReadOnlyList<QhyG3VerifiedLocalSample> Samples, QhyG3LocalModelLimits Limits, DateTimeOffset NowUtc);

/// <summary>
/// An inferred local image geometry, never a formal plate solve or slit measurement.
/// The caller must use the existing durable coarse ledger and independently measure
/// the post-move target before fine placement or any science exposure.
/// </summary>
public sealed record QhyG3LocalPrediction(string ModelSha256, double RightAscensionDegrees,
    double DeclinationDegrees, double PixelScaleArcseconds, double PositionAngleDegrees, bool Flipped,
    double PredictionUncertaintyArcseconds, double IndependentHoldoutResidualArcseconds,
    double G3MinusQhyEastArcseconds, double G3MinusQhyNorthArcseconds,
    IReadOnlyList<string> TrainingCandidateSha256, string HoldoutCandidateSha256,
    DateTimeOffset OldestSampleUtc, DateTimeOffset NewestSampleUtc)
{
    public bool FormalSolve => false;
    public bool MeasuredTarget => false;
    public bool GlobalActiveTransfer => false;
    public bool FineMotionAuthority => false;
}

public sealed record QhyG3LocalModelResult(GateResult Gate, QhyG3LocalPrediction? Prediction);
public sealed record QhyG3LocalSearchPrior(string SourceCandidateSha256, double RightAscensionDegrees,
    double DeclinationDegrees, double UncertaintyArcseconds, double PixelScaleArcseconds,
    double PositionAngleDegrees, bool Flipped)
{
    public bool MotionAuthority => false;
    public bool MeasuredTarget => false;
}

/// <summary>
/// ADR-0043 causal local translation: fit earlier independent pairs, validate on
/// the latest withheld pair, then predict only inside the observed neighbourhood.
/// No rejected outlier is silently discarded and no future sample enters a fit.
/// </summary>
public static class QhyG3LocalModelPolicy
{
    /// <summary>Deterministic applicability selection BEFORE fitting; never selects by a favourable residual.</summary>
    public static IReadOnlyList<QhyG3VerifiedLocalSample> SelectLocalSamples(IReadOnlyList<QhyG3VerifiedLocalSample> input,
        QhyG3SolvedFrame query, DateTimeOffset now, TimeSpan maximumAge, double radiusDegrees = 5) =>
        input.Where(s => s.Candidate.CreatedUtc < query.ExposureStartedUtc && s.Candidate.CreatedUtc <= now &&
            now - s.Candidate.CreatedUtc <= maximumAge && s.Candidate.ValidUntilUtc >= now &&
            Separation(s.Candidate.Qhy, query) <= radiusDegrees * 3600)
        .OrderBy(s => s.Candidate.CreatedUtc).ThenBy(s => s.Candidate.CandidateSha256, StringComparer.Ordinal)
        .TakeLast(24).ToArray();

    public static QhyG3LocalSearchPrior? SearchPrior(QhyG3LocalModelRequest request)
    {
        if (!request.Limits.IsValid || request.Binding.OpticalState is not { IsComplete: true } || !ValidFrame(request.CurrentQhy) ||
            request.CurrentQhy.CameraStableId != request.Binding.QhyCameraId ||
            request.CurrentQhy.SolveCompletedUtc > request.NowUtc ||
            request.NowUtc - request.CurrentQhy.ExposureMidpointUtc > request.Limits.MaximumCurrentQhyAge) return null;
        var source = SelectLocalSamples(request.Samples, request.CurrentQhy, request.NowUtc, request.Limits.MaximumSampleAge,
            request.Limits.MaximumLocalSkyRadiusDegrees).LastOrDefault();
        if (source is null || source.Candidate.ValidateIntegrity().Count != 0 || !source.OpticalState.IsComplete ||
            !IsSha(source.FocusEvidenceSha256) || string.IsNullOrWhiteSpace(source.FocusEvidencePath) ||
            !SameHash(source.OpticalState.ComputeSha256(), request.Binding.OpticalState.ComputeSha256()) ||
            !SameHash(source.Candidate.CommissioningPresetSha256, request.Binding.CommissioningPresetSha256) ||
            !SameHash(source.Candidate.NightSetupSha256, request.Binding.NightSetupSha256) ||
            source.Candidate.NightSetupId != request.Binding.NightSetupId ||
            source.Candidate.InstallationEpochId != request.Binding.InstallationEpochId ||
            source.Candidate.TelescopeDeviceId != request.Binding.TelescopeId ||
            source.Candidate.QhyCameraStableId != request.Binding.QhyCameraId ||
            source.Candidate.G3CameraStableId != request.Binding.G3CameraId ||
            source.Candidate.QhyOpticalTrainId != request.Binding.QhyOpticalTrainId ||
            source.Candidate.G3OpticalTrainId != request.Binding.G3OpticalTrainId ||
            source.Candidate.PierSide != request.Binding.PierSide || !SameGeometry(source.Candidate.Qhy, request.CurrentQhy) ||
            source.Candidate.G3.FrameWidthPixels != request.Binding.G3WidthPixels ||
            source.Candidate.G3.FrameHeightPixels != request.Binding.G3HeightPixels ||
            source.Candidate.G3.BinningX != request.Binding.G3Binning || source.Candidate.G3.BinningY != request.Binding.G3Binning ||
            source.Candidate.G3.RoiOriginX != 0 || source.Candidate.G3.RoiOriginY != 0 ||
            source.Candidate.G3.RoiWidthPixels != request.Binding.G3WidthPixels || source.Candidate.G3.RoiHeightPixels != request.Binding.G3HeightPixels ||
            request.Binding.ExpectedG3Flipped is { } expected && source.Candidate.G3.Flipped != expected) return null;
        var c = source.Candidate;
        var rebuilt = QhyG3SolvePairBuilder.Build(new(c.CollectionPolicy, c.ObservationRunId, c.ActionConfigurationSha256,
            c.CommissioningPresetSha256, c.NightSetupId, c.NightSetupSha256, c.InstallationEpochId, c.TelescopeDeviceId,
            c.QhyOpticalTrainId, c.G3OpticalTrainId, c.PairSource, c.Qhy, c.G3, c.MountReadbacks, c.CreatedUtc));
        if (rebuilt.Candidate is null || !SameHash(rebuilt.Candidate.CandidateSha256, c.CandidateSha256)) return null;
        var point = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(request.CurrentQhy.CenterRightAscensionDegrees,
            request.CurrentQhy.CenterDeclinationDegrees, c.Model.G3MinusQhyEastArcseconds, c.Model.G3MinusQhyNorthArcseconds);
        return new(c.CandidateSha256, point.RaDegrees, point.DecDegrees,
            c.Model.PredictionUncertaintyArcseconds * 2 + request.CurrentQhy.PixelScaleArcseconds +
            Separation(c.Qhy, request.CurrentQhy) / 100,
            c.G3.PixelScaleArcseconds / c.Qhy.PixelScaleArcseconds * request.CurrentQhy.PixelScaleArcseconds,
            NormalizeAngle(request.CurrentQhy.PositionAngleDegrees + c.Model.G3MinusQhyPositionAngleDegrees), c.G3.Flipped);
    }

    /// <summary>Fit-only preflight before spending a current QHY exposure; grants no prediction.</summary>
    public static bool HasIndependentFitSupport(IReadOnlyList<QhyG3VerifiedLocalSample> input,
        QhyG3LocalModelBinding binding, double maximumUncertaintyArcseconds)
    {
        if (input.Count < 3 || input.Count > 24 || !binding.OpticalState.IsComplete ||
            !double.IsFinite(maximumUncertaintyArcseconds) || maximumUncertaintyArcseconds <= 0) return false;
        var samples = input.OrderBy(s => s.Candidate.CreatedUtc).Select(s => s.Candidate).ToArray();
        if (samples.Any(c => c.ValidateIntegrity().Count != 0 || c.G3.FrameWidthPixels != binding.G3WidthPixels ||
            c.G3.FrameHeightPixels != binding.G3HeightPixels || c.G3.BinningX != binding.G3Binning || c.G3.BinningY != binding.G3Binning ||
            c.G3.Flipped != samples[0].G3.Flipped || !SameGeometry(c.Qhy, samples[0].Qhy))) return false;
        var pairs = samples.SelectMany((a, i) => samples.Skip(i + 1).Select(b => Separation(a.Qhy, b.Qhy))).ToArray();
        if (pairs.Length == 0 || pairs.Any(d => !double.IsFinite(d)))
            return false;
        var train = samples[..^1];
        var east = Median(train.Select(c => c.Model.G3MinusQhyEastArcseconds));
        var north = Median(train.Select(c => c.Model.G3MinusQhyNorthArcseconds));
        var ratio = Median(train.Select(c => c.G3.PixelScaleArcseconds / c.Qhy.PixelScaleArcseconds));
        var angleOrigin = train[0].Model.G3MinusQhyPositionAngleDegrees;
        var angle = angleOrigin + Median(train.Select(c => DeltaAngle(c.Model.G3MinusQhyPositionAngleDegrees, angleOrigin)));
        var corner = Math.Sqrt(Math.Pow(binding.G3WidthPixels, 2) + Math.Pow(binding.G3HeightPixels, 2)) / 2;
        var residual = samples.Max(c => Math.Sqrt(Math.Pow(c.Model.G3MinusQhyEastArcseconds - east, 2) +
            Math.Pow(c.Model.G3MinusQhyNorthArcseconds - north, 2)) + corner *
            (Math.Abs(ratio * c.Qhy.PixelScaleArcseconds - c.G3.PixelScaleArcseconds) + 2 * c.G3.PixelScaleArcseconds *
                Math.Abs(Math.Sin(DeltaAngle(c.Model.G3MinusQhyPositionAngleDegrees, angle) * Math.PI / 360))));
        return residual + 2 * samples.Max(c => c.Model.PredictionUncertaintyArcseconds) < maximumUncertaintyArcseconds;
    }

    public static bool CurrentExposuresAligned(DateTimeOffset g3CompletedUtc, int actualG3ExposureMilliseconds,
        QhyG3SolvedFrame qhy, QhyG3FastPairPolicy policy, DateTimeOffset now, out string reason)
    {
        reason = "QHY_G3_LOCAL_EXPOSURE_TIME_UNKNOWN";
        if (g3CompletedUtc == default || actualG3ExposureMilliseconds <= 0 || g3CompletedUtc > now ||
            policy.Validate().Count != 0 || !policy.Enabled) return false;
        var midpoint = g3CompletedUtc - TimeSpan.FromMilliseconds(actualG3ExposureMilliseconds / 2d);
        reason = "QHY_G3_LOCAL_EXPOSURE_ALIGNMENT_EXCEEDED";
        if (qhy.ExposureStartedUtc > now || qhy.ExposureEndedUtc > now || qhy.SolveCompletedUtc > now ||
            now - g3CompletedUtc > policy.MaximumPairWallClock ||
            now - qhy.ExposureMidpointUtc > policy.MaximumCachedQhyAge ||
            Math.Abs((qhy.ExposureMidpointUtc - midpoint).TotalSeconds) > policy.MaximumPairMidpointSeparation.TotalSeconds)
            return false;
        reason = "QHY_G3_LOCAL_EXPOSURES_TIME_ALIGNED";
        return true;
    }

    public static QhyG3LocalModelResult Evaluate(QhyG3LocalModelRequest request)
    {
        QhyG3LocalModelResult Reject(string suffix, string message) => new(
            GateResult.Warn("QHY_G3_LOCAL_MODEL_" + suffix, message), null);
        var b = request.Binding;
        var q = request.CurrentQhy;
        var now = request.NowUtc;
        if (!request.Limits.IsValid || now == default || b.OpticalState is null || !b.OpticalState.IsComplete ||
            !IsSha(b.CommissioningPresetSha256) || !IsSha(b.NightSetupSha256) ||
            string.IsNullOrWhiteSpace(b.NightSetupId) || string.IsNullOrWhiteSpace(b.InstallationEpochId) ||
            string.IsNullOrWhiteSpace(b.TelescopeId) || string.IsNullOrWhiteSpace(b.QhyOpticalTrainId) ||
            string.IsNullOrWhiteSpace(b.G3OpticalTrainId) || !KnownPier(b.PierSide) ||
            b.G3WidthPixels <= 0 || b.G3HeightPixels <= 0 || b.G3Binning <= 0)
            return Reject("BINDING_INVALID", "Local model context, optical state or limits are incomplete; use normal G3 acquisition.");
        if (!ValidFrame(q) || q.CameraStableId != b.QhyCameraId || q.ExposureStartedUtc > now ||
            q.ExposureEndedUtc > now || q.SolveCompletedUtc > now ||
            now - q.ExposureMidpointUtc > request.Limits.MaximumCurrentQhyAge)
            return Reject("CURRENT_QHY_STALE", "A fresh current-pointing hash-bound formal QHY frame is required.");

        var samples = request.Samples.OrderBy(s => s.Candidate.CreatedUtc)
            .ThenBy(s => s.Candidate.CandidateSha256, StringComparer.Ordinal).ToArray();
        if (samples.Length < 3 || samples.Length > request.Limits.MaximumSamples)
            return Reject("INSUFFICIENT_INDEPENDENT_SAMPLES", "At least two earlier training pairs and one independent chronological holdout are required.");
        var uniqueFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            var c = sample.Candidate;
            if (c.ValidateIntegrity().Count != 0 || !sample.OpticalState.IsComplete ||
                !SameHash(sample.OpticalState.ComputeSha256(), b.OpticalState.ComputeSha256()) ||
                !IsSha(sample.FocusEvidenceSha256) || string.IsNullOrWhiteSpace(sample.FocusEvidencePath) ||
                !SameHash(c.CommissioningPresetSha256, b.CommissioningPresetSha256) ||
                !SameHash(c.NightSetupSha256, b.NightSetupSha256) || c.NightSetupId != b.NightSetupId ||
                c.InstallationEpochId != b.InstallationEpochId || c.TelescopeDeviceId != b.TelescopeId ||
                c.QhyCameraStableId != b.QhyCameraId || c.G3CameraStableId != b.G3CameraId ||
                c.QhyOpticalTrainId != b.QhyOpticalTrainId || c.G3OpticalTrainId != b.G3OpticalTrainId ||
                !string.Equals(c.PierSide, b.PierSide, StringComparison.OrdinalIgnoreCase) ||
                !SameGeometry(c.Qhy, q) || c.G3.FrameWidthPixels != b.G3WidthPixels ||
                c.G3.FrameHeightPixels != b.G3HeightPixels || c.G3.BinningX != b.G3Binning ||
                c.G3.BinningY != b.G3Binning || c.G3.RoiOriginX != 0 || c.G3.RoiOriginY != 0 ||
                c.G3.RoiWidthPixels != b.G3WidthPixels || c.G3.RoiHeightPixels != b.G3HeightPixels ||
                b.ExpectedG3Flipped is { } expected && c.G3.Flipped != expected)
                return Reject("SOURCE_INAPPLICABLE", "A source differs in immutable configuration, focus, detector geometry, installation or pier side.");
            // Rebuild the semantic pair, not just its caller-recomputed self hash.
            var rebuilt = QhyG3SolvePairBuilder.Build(new(c.CollectionPolicy, c.ObservationRunId,
                c.ActionConfigurationSha256, c.CommissioningPresetSha256, c.NightSetupId, c.NightSetupSha256,
                c.InstallationEpochId, c.TelescopeDeviceId, c.QhyOpticalTrainId, c.G3OpticalTrainId,
                c.PairSource, c.Qhy, c.G3, c.MountReadbacks, c.CreatedUtc));
            if (rebuilt.Candidate is null || !SameHash(rebuilt.Candidate.CandidateSha256, c.CandidateSha256))
                return Reject("SOURCE_SEMANTICS_INVALID", "A stored pair cannot be rebuilt from its formal source geometry and five-readback bracket.");
            if (c.CreatedUtc >= q.ExposureStartedUtc || c.CreatedUtc > now || c.ValidUntilUtc < now ||
                now - c.CreatedUtc > request.Limits.MaximumSampleAge || c.Qhy.SolveCompletedUtc > c.CreatedUtc ||
                c.G3.SolveCompletedUtc > c.CreatedUtc)
                return Reject("SOURCE_NOT_CAUSAL", "Future, current-frame self-validation, expired or stale pairs cannot validate this prediction.");
            if (!uniqueFiles.Add(c.Qhy.FrameSha256) ||
                !uniqueFiles.Add(c.G3.FrameSha256) || SameHash(c.Qhy.FrameSha256, q.FrameSha256))
                return Reject("DUPLICATE_SOURCE", "Repeated source frames do not count as independent pairs; distinct same-run exposures may.");
            var distance = Separation(c.Qhy, q);
            if (!double.IsFinite(distance) || distance > request.Limits.MaximumLocalSkyRadiusDegrees * 3600)
                return Reject("OUTSIDE_LOCAL_SKY", "A source lies outside the explicitly bounded local sky neighbourhood.");
        }
        if (samples.Any(s => s.Candidate.G3.Flipped != samples[0].Candidate.G3.Flipped ||
            s.Candidate.Qhy.Flipped != q.Flipped))
            return Reject("PARITY_CHANGED", "Detector parity differs; no local transfer across flips is inferred.");

        var training = samples[..^1].Select(s => s.Candidate).ToArray();
        var holdout = samples[^1].Candidate;
        var east = Median(training.Select(c => c.Model.G3MinusQhyEastArcseconds));
        var north = Median(training.Select(c => c.Model.G3MinusQhyNorthArcseconds));
        var ratio = Median(training.Select(c => c.G3.PixelScaleArcseconds / c.Qhy.PixelScaleArcseconds));
        var angleOrigin = training[0].Model.G3MinusQhyPositionAngleDegrees;
        var angle = angleOrigin + Median(training.Select(c => DeltaAngle(c.Model.G3MinusQhyPositionAngleDegrees, angleOrigin)));
        var maxRadiusPixels = Math.Sqrt(b.G3WidthPixels * (double)b.G3WidthPixels + b.G3HeightPixels * (double)b.G3HeightPixels) / 2;
        double Residual(QhyToG3TransferCandidate c)
        {
            var translation = Math.Sqrt(Math.Pow(c.Model.G3MinusQhyEastArcseconds - east, 2) +
                                        Math.Pow(c.Model.G3MinusQhyNorthArcseconds - north, 2));
            // Validate orientation/scale on a full-frame corner, not merely the fitted centre.
            var predictedScale = ratio * c.Qhy.PixelScaleArcseconds;
            var cornerError = maxRadiusPixels * (Math.Abs(predictedScale - c.G3.PixelScaleArcseconds) +
                2 * c.G3.PixelScaleArcseconds * Math.Abs(Math.Sin(DeltaAngle(c.Model.G3MinusQhyPositionAngleDegrees, angle) * Math.PI / 360)));
            return translation + cornerError;
        }
        var trainingResidual = training.Max(Residual);
        var holdoutResidual = Residual(holdout);
        var spanSeconds = (holdout.CreatedUtc - training[0].CreatedUtc).TotalSeconds;
        var skySpan = samples.SelectMany((a, i) => samples.Skip(i + 1).Select(c => Separation(a.Candidate.Qhy, c.Candidate.Qhy))).DefaultIfEmpty(0).Max();
        var nearest = samples.Min(s => Separation(s.Candidate.Qhy, q));
        // Independent same-pointing exposures validate local translation and
        // full-detector scale/orientation repeatability, not global flexure.
        // Their sky domain remains at most one measured detector half-field.
        var validatedSkySpan = Math.Max(skySpan, Math.Min(b.G3WidthPixels, b.G3HeightPixels) * samples[0].Candidate.G3.PixelScaleArcseconds / 2);
        if (spanSeconds <= 0 || nearest > validatedSkySpan)
            return Reject("OUTSIDE_VALIDATED_ENVELOPE", "Requested time/sky extrapolation exceeds the measured local validation envelope.");
        var baseResidual = Math.Max(trainingResidual, holdoutResidual);
        var sourceUncertainty = samples.Max(s => s.Candidate.Model.PredictionUncertaintyArcseconds);
        var growth = 1 + nearest / validatedSkySpan + (now - holdout.CreatedUtc).TotalSeconds / spanSeconds;
        var uncertainty = baseResidual * growth + 2 * sourceUncertainty + q.PixelScaleArcseconds;
        if (!double.IsFinite(uncertainty) || uncertainty > request.Limits.MaximumPredictionUncertaintyArcseconds)
            return Reject("UNCERTAINTY_EXCEEDED", $"Independent holdout/corner residual and locality growth imply {uncertainty:F2} arcsec, above the original recognition envelope.");
        var center = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(q.CenterRightAscensionDegrees,
            q.CenterDeclinationDegrees, east, north);
        var hashes = training.Select(c => c.CandidateSha256).ToArray();
        var modelSha = HashFields("QHY_G3_CAUSAL_LOCAL_V1", b.OpticalState.ComputeSha256(),
            string.Join("|", samples.Select(s => s.Candidate.CandidateSha256)), q.FrameSha256, q.SolveEvidenceSha256,
            now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            request.Limits.MaximumPredictionUncertaintyArcseconds.ToString("R", CultureInfo.InvariantCulture));
        var prediction = new QhyG3LocalPrediction(modelSha, center.RaDegrees, center.DecDegrees,
            ratio * q.PixelScaleArcseconds, NormalizeAngle(q.PositionAngleDegrees + angle), samples[0].Candidate.G3.Flipped,
            uncertainty, holdoutResidual, east, north, hashes, holdout.CandidateSha256,
            samples[0].Candidate.CreatedUtc, holdout.CreatedUtc);
        return new(GateResult.Warn("QHY_G3_LOCAL_MODEL_VALIDATED_PREDICTION",
            "Locally validated inferred G3 geometry; not formal WCS or measured target. Original coarse/return budgets and fresh post-move measurements remain mandatory.",
            new Dictionary<string, double> { ["predictionUncertaintyArcseconds"] = uncertainty,
                ["independentHoldoutResidualArcseconds"] = holdoutResidual, ["trainingSamples"] = training.Length,
                ["nearestSourceArcseconds"] = nearest, ["observedSkySpanArcseconds"] = skySpan }), prediction);
    }

    public static string HardwareFingerprint(QhyG3LocalModelBinding b) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("|", b.InstallationEpochId, b.TelescopeId, b.QhyCameraId, b.G3CameraId,
            b.QhyOpticalTrainId, b.G3OpticalTrainId, b.PierSide, "QHY_TO_G3_LOCAL_TRANSLATION_V1",
            G3AcquisitionMotionState.CurrentTangentProjectionId)))).ToLowerInvariant();

    public static bool SameGeometry(QhyG3SolvedFrame a, QhyG3SolvedFrame b) =>
        a.CameraStableId == b.CameraStableId && a.FrameWidthPixels == b.FrameWidthPixels && a.FrameHeightPixels == b.FrameHeightPixels &&
        a.BinningX == b.BinningX && a.BinningY == b.BinningY && a.RoiOriginX == b.RoiOriginX && a.RoiOriginY == b.RoiOriginY &&
        a.RoiWidthPixels == b.RoiWidthPixels && a.RoiHeightPixels == b.RoiHeightPixels && a.Flipped == b.Flipped;
    public static bool IsSha(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    public static bool SameHash(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static string HashFields(params string[] fields) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Concat(fields.Select(v => $"{v.Length}:{v}")))));
    private static double Separation(QhyG3SolvedFrame a, QhyG3SolvedFrame b) => G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
        a.CenterRightAscensionDegrees, a.CenterDeclinationDegrees, b.CenterRightAscensionDegrees, b.CenterDeclinationDegrees);
    private static double Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2; }
    private static double DeltaAngle(double a, double b) => Math.IEEERemainder(a - b, 360);
    private static double NormalizeAngle(double value) => (value % 360 + 360) % 360;
    private static bool KnownPier(string side) => side is "pierEast" or "pierWest";
    private static bool ValidFrame(QhyG3SolvedFrame f) => IsSha(f.FrameSha256) && IsSha(f.SolveEvidenceSha256) && IsSha(f.MountBindingSha256) &&
        f.FrameWidthPixels > 0 && f.FrameHeightPixels > 0 && f.BinningX > 0 && f.BinningY > 0 &&
        f.RoiOriginX >= 0 && f.RoiOriginY >= 0 && f.RoiWidthPixels == f.FrameWidthPixels && f.RoiHeightPixels == f.FrameHeightPixels &&
        f.ExposureStartedUtc != default && f.ExposureStartedUtc <= f.ExposureMidpointUtc && f.ExposureMidpointUtc <= f.ExposureEndedUtc &&
        f.ExposureEndedUtc <= f.SolveCompletedUtc && double.IsFinite(f.CenterRightAscensionDegrees) && f.CenterRightAscensionDegrees is >= 0 and < 360 &&
        double.IsFinite(f.CenterDeclinationDegrees) && f.CenterDeclinationDegrees is > -90 and < 90 && double.IsFinite(f.PositionAngleDegrees) &&
        double.IsFinite(f.PixelScaleArcseconds) && f.PixelScaleArcseconds > 0;
}
