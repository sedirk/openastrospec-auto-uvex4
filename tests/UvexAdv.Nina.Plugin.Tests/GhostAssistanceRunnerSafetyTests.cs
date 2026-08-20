using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class GhostAssistanceRunnerSafetyTests
{
    private static readonly string RunnerSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    private static readonly string GhostSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.GhostAssistance.cs"));

    [Fact]
    public void ActionConfigurationDefaultsToExplicitSkipAndCapturesProfileSelection()
    {
        var parameter = typeof(G3RunConfiguration)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(item => item.Name == "GhostAssistanceMode");

        Assert.True(parameter.HasDefaultValue);
        Assert.Equal(GhostAssistanceMode.Skip, parameter.DefaultValue);
        Assert.NotNull(typeof(UvexPluginSettings).GetProperty(nameof(UvexPluginSettings.GhostAssistanceMode)));
        Assert.NotNull(typeof(G3RunConfiguration).GetProperty(nameof(G3RunConfiguration.GhostAssistanceMode)));
    }

    [Fact]
    public void AcquireG3ConsultsGhostAtEveryOrdinaryOrBrightIdentityFailure()
    {
        var body = Slice(
            RunnerSource,
            "private async Task<G3FieldState> CaptureAndAnalyzeG3Async(",
            "private static bool FocusFailureMayBeSaturationDominated(");

        Assert.True(Count(body, "TryAcquireTargetFromGhostAsync(") >= 3);
        Assert.Contains("g3Solve: null", body, StringComparison.Ordinal);
        Assert.Contains("if (ghostTarget is not null) return ghostTarget", body, StringComparison.Ordinal);
        Assert.Contains("TryAcquireBrightTargetFromWingsAsync", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperConsumesOnlyExistingFreshSameExposureOffFramesAndNeverCommandsHardware()
    {
        Assert.Contains("G3SlitIlluminationPhase.OffBefore or G3SlitIlluminationPhase.OffAfter", GhostSource, StringComparison.Ordinal);
        Assert.Contains("Math.Max(2, ghost.MatchPolicy.MinimumFrameCount)", GhostSource, StringComparison.Ordinal);
        Assert.Contains("GroupBy(frame => (ExposureMilliseconds", GhostSource, StringComparison.Ordinal);
        var extract = GhostSource.IndexOf("GhostFrameObservationFactory.FromMonochromeFrame", StringComparison.Ordinal);
        var evaluate = GhostSource.IndexOf("GhostTemplateAssistance.Evaluate", StringComparison.Ordinal);
        Assert.True(extract >= 0 && extract < evaluate);
        Assert.Contains("ComputeFileSha256Async", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFullFrameAsync", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCurrentGuidingFrameAsync", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SlewToCoordinatesAsync", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExactLockPositionAsync", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", GhostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalIdentityIsRunBoundRehashedAndRejectsPostQhySolveMountDrift()
    {
        Assert.Contains("job.ObservationRunId", GhostSource, StringComparison.Ordinal);
        Assert.Contains("context.Plan.ObservationRunId", GhostSource, StringComparison.Ordinal);
        Assert.Contains("accepted.Sha256", GhostSource, StringComparison.Ordinal);
        Assert.Contains("solve.EvidenceSha256", GhostSource, StringComparison.Ordinal);
        Assert.Contains("lastQhySolveMountBinding", GhostSource, StringComparison.Ordinal);
        Assert.Contains("acceptedFrameMountBinding.Validate(", GhostSource, StringComparison.Ordinal);
        Assert.Contains("configuration.ActionConfigurationSha256", GhostSource, StringComparison.Ordinal);
        Assert.Contains("commissioning.Sha256", GhostSource, StringComparison.Ordinal);
        Assert.Contains("job.Id", GhostSource, StringComparison.Ordinal);
        Assert.Contains("currentReported.Epoch.ToString()", GhostSource, StringComparison.Ordinal);
        Assert.Contains("mountBinding.PierSide", GhostSource, StringComparison.Ordinal);
        Assert.Contains("MountCommandArrivalToleranceArcseconds", GhostSource, StringComparison.Ordinal);
        Assert.Contains("mount moved", GhostSource, StringComparison.Ordinal);
        Assert.True(Count(GhostSource, "ComputeFileSha256Async(") >= 4);
    }

    [Fact]
    public void OnlyRequireTurnsUnavailableTemplateIntoAttention()
    {
        var body = Slice(
            GhostSource,
            "internal static GhostAssistanceResult GhostUnavailableResult(",
            "private GateResult ValidateGhostSlitAuthority(");

        Assert.Contains("mode == GhostAssistanceMode.RequireValid", body, StringComparison.Ordinal);
        Assert.Contains("GhostAssistanceDecision.PauseNeedsAttention", body, StringComparison.Ordinal);
        Assert.Contains("GhostAssistanceDecision.ContinueLongExposureWcsFallback", body, StringComparison.Ordinal);
        Assert.Contains("GHOST_ASSISTANCE_SKIPPED_FALLBACK", body, StringComparison.Ordinal);
        Assert.Contains("GHOST_ASSISTANCE_INVALID_FALLBACK", body, StringComparison.Ordinal);
    }

    public static TheoryData<object> InvalidFitsGains => new()
    {
        "not-a-number",
        double.NaN,
        double.PositiveInfinity,
        long.MaxValue,
    };

    [Theory]
    [MemberData(nameof(InvalidFitsGains))]
    public async Task MalformedNanOrOverflowFitsGainIsIsolatedBeforeTemplateEvaluation(object gain)
    {
        var isolated = await RealObservationStageRunner.IsolateGhostFramePreparationAsync(
            _ => Task.FromResult(
                RealObservationStageRunner.ConvertGhostFrameGain(gain).ToString()),
            CancellationToken.None);

        Assert.False(isolated.Succeeded);
        Assert.Null(isolated.Value);
        Assert.Equal("GHOST_FRAME_PREPARATION_FAILED", isolated.Failure?.Code);
        Assert.Contains("FITS metadata conversion", isolated.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOrUnreadableOffFrameIsIsolatedAndModePolicyRemainsExplicit()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"uvex-ghost-missing-{Guid.NewGuid():N}.fits");
        var missing = await RealObservationStageRunner.IsolateGhostFramePreparationAsync(
            token => File.ReadAllTextAsync(missingPath, token),
            CancellationToken.None);
        var unreadable = await RealObservationStageRunner.IsolateGhostFramePreparationAsync(
            _ => Task.FromException<string>(new UnauthorizedAccessException("fixture OFF is unreadable")),
            CancellationToken.None);

        foreach (var isolated in new[] { missing, unreadable })
        {
            Assert.False(isolated.Succeeded);
            Assert.Equal("GHOST_FRAME_PREPARATION_FAILED", isolated.Failure?.Code);

            var automatic = RealObservationStageRunner.GhostUnavailableResult(
                GhostAssistanceMode.AutoIfValidElseSkip,
                isolated.Failure!);
            var required = RealObservationStageRunner.GhostUnavailableResult(
                GhostAssistanceMode.RequireValid,
                isolated.Failure!);

            Assert.Equal(GhostAssistanceDecision.ContinueLongExposureWcsFallback, automatic.Decision);
            Assert.Equal(GateDisposition.Passed, automatic.Gate.Disposition);
            Assert.Equal("GHOST_ASSISTANCE_INVALID_FALLBACK", automatic.Gate.Code);
            Assert.Equal(GhostAssistanceDecision.PauseNeedsAttention, required.Decision);
            Assert.Equal(GateDisposition.Indeterminate, required.Gate.Disposition);
            Assert.Equal("GHOST_REQUIRED_ASSISTANCE_UNAVAILABLE", required.Gate.Code);
        }
    }

    [Fact]
    public async Task InjectedExtractorExceptionIsIsolatedBeforePureEvaluation()
    {
        var isolated = await RealObservationStageRunner.IsolateGhostFramePreparationAsync(
            _ => Task.FromException<string>(new InvalidOperationException("fixture extractor failed")),
            CancellationToken.None);

        Assert.False(isolated.Succeeded);
        Assert.Equal("GHOST_FRAME_PREPARATION_FAILED", isolated.Failure?.Code);
        Assert.Contains("InvalidOperationException", isolated.Failure?.Message, StringComparison.Ordinal);
        Assert.Contains("fixture extractor failed", isolated.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserCancellationEscapesButUnrelatedCancellationIsIsolated()
    {
        using var userCancellation = new CancellationTokenSource();
        userCancellation.Cancel();
        var invoked = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RealObservationStageRunner.IsolateGhostFramePreparationAsync(
                _ =>
                {
                    invoked = true;
                    return Task.FromResult("should-not-run");
                },
                userCancellation.Token));
        Assert.False(invoked);

        using var unrelatedCancellation = new CancellationTokenSource();
        unrelatedCancellation.Cancel();
        var isolated = await RealObservationStageRunner.IsolateGhostFramePreparationAsync(
            _ => Task.FromException<string>(new OperationCanceledException(unrelatedCancellation.Token)),
            CancellationToken.None);

        Assert.False(isolated.Succeeded);
        Assert.Equal("GHOST_FRAME_PREPARATION_FAILED", isolated.Failure?.Code);
    }

    [Fact]
    public void SkipAndPreparationFailureEvidenceDoNotReopenTheOffFrame()
    {
        var skipBranch = Slice(
            GhostSource,
            "if (mode == GhostAssistanceMode.Skip)",
            "var ghost = commissioning?.Value.GhostAssistance;");
        var preparationBranch = Slice(
            GhostSource,
            "var preparation = await IsolateGhostFramePreparationAsync(",
            "var prepared = preparation.Value!;");
        var publisher = Slice(
            GhostSource,
            "private async Task<string> PublishGhostAssistanceEvidenceAsync(",
            "private static string ComputeGhostBindingSha256(");

        Assert.Contains("rehashReferenceSource: false", skipBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("ComputeFileSha256Async", skipBranch, StringComparison.Ordinal);
        Assert.Contains("CompleteUnavailableGhostAttemptAsync(", preparationBranch, StringComparison.Ordinal);
        Assert.Contains("rehashReferenceSource: false", preparationBranch, StringComparison.Ordinal);
        Assert.Contains("rehashReferenceSource ? reference.Captured.Capture.Path : null", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidGhostOutputCannotClaimIdentityOrMotionAuthority()
    {
        Assert.Contains("result.CanEstablishTargetIdentity ? 1 : 0", GhostSource, StringComparison.Ordinal);
        Assert.Contains("[\"ghostCanAuthorizeMotion\"] = 0", GhostSource, StringComparison.Ordinal);
        Assert.Contains("[\"ghostAuthority\"] = result.Authority.ToString()", GhostSource, StringComparison.Ordinal);
        Assert.Contains("Fresh slit/PHD2 residual authority is still mandatory", GhostSource, StringComparison.Ordinal);
        Assert.Contains("PeakAdu: 0", GhostSource, StringComparison.Ordinal);
        Assert.Contains("SignalToNoise: 0", GhostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Enif", GhostSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Deneb", GhostSource, StringComparison.OrdinalIgnoreCase);
    }

    private static int Count(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate source section {startMarker} -> {endMarker}.");
        return source[start..end];
    }
}
