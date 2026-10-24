using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record WideFieldTargetSearchHint(PixelPoint PredictedTarget, double? UncertaintyPixels,
    string EvidencePath, IReadOnlyDictionary<string, string> SourceFiles)
{
    public bool SearchOnly => true;
    public bool MeasuredTarget => false;
    public bool MotionAuthority => false;
    public Coordinates? SkyCenter { get; init; }
    public DateTimeOffset ValidUntilUtc { get; init; }
    public string OpticalStateSha256 { get; init; } = string.Empty;
}

internal sealed partial class RealObservationStageRunner
{
    private readonly SemaphoreSlim wideFieldHintQueryGate = new(1, 1);
    private readonly Dictionary<string, (WideFieldTargetSearchHint? Hint, DateTimeOffset Until)> wideFieldHintQueries = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Coordinates?> TryWideFieldSolveHintAsync(ObservationContext context,
        G3PlateSolveProbeState probe, CancellationToken token) =>
        (await TryWideFieldTargetSearchHintAsync(context, probe, token).ConfigureAwait(false))?.SkyCenter;

    private async Task<WideFieldTargetSearchHint?> TryWideFieldTargetSearchHintAsync(ObservationContext context,
        G3PlateSolveProbeState probe, CancellationToken token)
    {
        await wideFieldHintQueryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var key = probe.FramePath + "|" + probe.MountBinding?.BindingSha256;
            if (wideFieldHintQueries.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow < cached.Until)
            {
                if (cached.Hint is null) return null;
                foreach (var parent in cached.Hint.SourceFiles)
                    if (!SameHash(await ComputeFileSha256Async(parent.Key, token).ConfigureAwait(false), parent.Value))
                    {
                        wideFieldHintQueries.Remove(key);
                        throw new PhysicalActionGateException(GateResult.Unknown("QHY_WIDE_SEARCH_PRIOR_SOURCE_CHANGED", "An immutable cached search-prior parent changed."));
                    }
                var current = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
                return current is not null && SameHash(current.ComputeSha256(), cached.Hint.OpticalStateSha256) &&
                    DateTimeOffset.UtcNow < cached.Hint.ValidUntilUtc ? cached.Hint : null;
            }
            var hint = await BuildWideFieldTargetSearchHintAsync(context, probe, token).ConfigureAwait(false);
            if (wideFieldHintQueries.Count >= 64) wideFieldHintQueries.Clear();
            wideFieldHintQueries[key] = (hint, hint?.ValidUntilUtc ?? DateTimeOffset.UtcNow.AddSeconds(1));
            return hint;
        }
        finally { wideFieldHintQueryGate.Release(); }
    }

    private async Task<WideFieldTargetSearchHint?> BuildWideFieldTargetSearchHintAsync(ObservationContext context,
        G3PlateSolveProbeState probe, CancellationToken token)
    {
        if (localDualModelDisabled || probe.Image is null || probe.MountBinding is null || commissioning is null || nightSetup is null)
            return null;
        var optical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (optical is null) return null;
        var current = await TryGetWideFieldPairSourceAsync(context, probe, token).ConfigureAwait(false)
            ?? await TryPrepareCachedQhyPairSourceAsync(context, probe, configuration.G3.EffectiveFastSolvePair, token).ConfigureAwait(false);
        if (current is null) return null;
        var q = AsLocalDualFrame(context, current);
        var binding = CurrentLocalDualBinding(context, probe, optical);
        var archive = await QhyG3LocalModelArchive.LoadAsync(LocalDualArchiveRoot, binding, DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(30), token).ConfigureAwait(false);
        if (archive.IntegrityFailure || archive.Samples.Count == 0)
        {
            await WriteAuditBestEffortAsync("qhy-wide-search-prior-unavailable", new { sampleCount = archive.Samples.Count,
                archive.IntegrityFailure, measuredG3Required = true, oldSchemaNotPromoted = true }).ConfigureAwait(false);
            return null;
        }
        // This search-only path deliberately does not repeatedly refit the
        // multi-parameter model. A hash-checked applicable single pair is a
        // broader boresight prior; the native identifier must measure the star.
        var prior = QhyG3LocalModelPolicy.SearchPrior(new(binding, q, archive.Samples,
            new(TimeSpan.FromMinutes(30), configuration.G3.EffectiveFastSolvePair.MaximumCachedQhyAge, 5,
                commissioning.Value.Phd2SlitPlacement!.TargetSearchRadiusPixels * q.PixelScaleArcseconds), DateTimeOffset.UtcNow));
        if (prior is null) return null;
        var pose = new PlateSolveResult { Success = true,
            Coordinates = new Coordinates(prior.RightAscensionDegrees, prior.DeclinationDegrees, Epoch.J2000, Coordinates.RAType.Degrees),
            Pixscale = prior.PixelScaleArcseconds, PositionAngle = prior.PositionAngleDegrees, Flipped = prior.Flipped };
        var target = G3WcsTargetProjector.Project(TargetCoordinates(context.Plan), pose,
            probe.Image.Properties.Width, probe.Image.Properties.Height, current.Solve.SolverIdentity);
        var parents = new Dictionary<string, string>(archive.SourceFiles, StringComparer.OrdinalIgnoreCase)
        { [current.Frame.FitsPath] = current.Frame.Sha256, [current.Solve.EvidencePath] = current.Solve.EvidenceSha256,
            [probe.FramePath] = probe.MountBinding.FrameSha256 };
        foreach (var source in current.Solve.InferenceSources) parents[source.Key] = source.Value;
        var receipt = await PublishRunJsonEvidenceAsync("qhy-wide-target-search-hint", "Local optical boresight prior for measured target search only",
            new { prior, predictedTarget = target, qhyExposureMidpointUtc = q.ExposureMidpointUtc,
                g3ExposureMidpointUtc = G3ProbeMidpointUtc(probe), absoluteG3PositionVerified = false,
                uncertaintyAtG3TimeUnknownWithoutDrift = true, searchOnly = true, measuredTarget = false,
                motionAuthority = false, budgetReset = false }, probe.FramePath, token).ConfigureAwait(false);
        parents[receipt] = await ComputeFileSha256Async(receipt, token).ConfigureAwait(false);
        foreach (var parent in parents)
            if (!SameHash(await ComputeFileSha256Async(parent.Key, token).ConfigureAwait(false), parent.Value))
                throw new PhysicalActionGateException(GateResult.Unknown("QHY_WIDE_SEARCH_PRIOR_SOURCE_CHANGED", "An immutable search-prior parent changed before first consumption."));
        var late = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        var expiry = q.ExposureMidpointUtc + configuration.G3.EffectiveFastSolvePair.MaximumCachedQhyAge;
        if (current.Solve.InferenceValidUntilUtc is { } inherited && inherited < expiry) expiry = inherited;
        if (late is null || !SameHash(late.ComputeSha256(), optical.ComputeSha256()) ||
            DateTimeOffset.UtcNow >= expiry) return null;
        // No zero-drift assumption: absence of a qualified drift bridge is
        // explicit unknown uncertainty, never a precise/verified target point.
        return new(target, null, receipt, parents) { SkyCenter = pose.Coordinates,
            OpticalStateSha256 = optical.ComputeSha256(),
            ValidUntilUtc = expiry };
    }
}
