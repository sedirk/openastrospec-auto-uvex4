using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class DistanceGuardRecoveryTests
{
    private const string Lines = """
        02:09:45.220 00.003 16764 evsrv: cli A request: {"method":"set_lock_position","params":{"x":1735.35,"y":1008.02,"exact":true},"id":291}
        02:09:45.483 00.003 16764 Star::Find(50, 1719, 984, 0, (0,0,0,0), 1.5, 10.0, 4096) frame 16
        02:09:45.486 00.003 16764 Star::Find returns 1 (1), X=1719.41, Y=987.65, Mass=84638, SNR=40.6, Peak=7152 HFD=5.8
        02:09:45.489 00.003 16764 DistanceChecker: reject for large offset (25.87 > 7.40) avgDist = 3.70 count = 16
        """;
    private static Phd2DistanceGuardWitness? Parse(string text)
    {
        var t = new DateTimeOffset(new DateTime(2026,11,21,2,9,45,500,DateTimeKind.Local));
        var point = new Phd2Point(1735.3489,1008.0166);
        return Phd2DistanceGuardWitness.Parse(text, new HashSet<int> { 16764 },
            new(new(1721.81,987), point, point, 25, 0,t.AddMilliseconds(-221),true,false,false,false,false),
            new(16,null,null,40.64,5.76,3.26,8), t, new(1719.68,984.34));
    }

    [Theory]
    [InlineData("original")] [InlineData("missing-reject")] [InlineData("different-star")]
    [InlineData("other-frame")] [InlineData("other-thread")] [InlineData("stale")]
    [InlineData("other-lock")] [InlineData("different-snr")] [InlineData("ambiguous")]
    public void RequiresSameFrameSameThreadAndExplicitNativeDistanceRejection(string mode)
    {
        var text = mode switch {
            "missing-reject" => Lines.Replace("DistanceChecker:", "Other:"),
            "different-star" => Lines.Replace("1719.41", "1700.41"),
            "other-frame" => Lines.Replace("frame 16", "frame 17"),
            "other-thread" => Lines.Replace("16764 Distance", "4280 Distance"),
            "stale" => Lines.Replace("02:09:45.", "02:08:45."),
            "other-lock" => Lines.Replace("1735.35", "1736.35"),
            "different-snr" => Lines.Replace("40.6", "4.6"),
            "ambiguous" => Lines + "\n" + Lines, _ => Lines };
        Assert.Equal(mode == "original", Parse(text) is not null);
    }

    private sealed class RecordedFact : FactAttribute
    {
        public RecordedFact() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UVEX_REPLAY_PHD2_DEBUG")))
            Skip = "Set UVEX_REPLAY_PHD2_DEBUG to the private HD 26546 PHD2 debug log."; }
    }

    [RecordedFact]
    public void OriginalPrivateNativeLogReplaysTheActualDistanceVeto()
    {
        using var file = File.Open(Environment.GetEnvironmentVariable("UVEX_REPLAY_PHD2_DEBUG")!,
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("02:09:45.", StringComparison.Ordinal)) lines.Add(line);
        Assert.Equal(new Phd2DistanceGuardWitness(16, new(1719.41,987.65),25.87,7.40), Parse(string.Join("\n",lines)));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Theory]
    [InlineData("native-recovers")] [InlineData("after-wait")] [InlineData("missing-output-until-timeout")]
    [InlineData("corroborated-repeated-veto")] [InlineData("unwitnessed-repeat")] [InlineData("repeat-low-snr")]
    [InlineData("missed-native-event")] [InlineData("new-episode")] [InlineData("missed-native-frame")]
    [InlineData("no-distance-witness")] [InlineData("low-snr")]
    [InlineData("different-selection")] [InlineData("wrong-lock")] [InlineData("other-owner")]
    [InlineData("recalibrated")] [InlineData("second-loss")] [InlineData("bad-output")] [InlineData("stale")]
    [InlineData("reserve-spent")] [InlineData("binding-changed-after-frames")] [InlineData("unsafe")]
    [InlineData("duplicate-frame")] [InlineData("unmatched-crop")] [InlineData("projection")]
    [InlineData("other-component")] [InlineData("slit-changed")] [InlineData("cancel")]
    public async Task PassivePathCannotTurnAnyMissingEvidenceIntoASettledGuide(string mode)
    {
        var f = new Phd2ConvergenceFixture(true);
        var q = f.Request([5,5,5]);
        var ep = new Phd2Point(110,100);
        var exact = new Phd2ExactLockPositionResult(new(100,100),ep,ep,10,0,f.Now.AddSeconds(-2),true,false,false,false,false);
        var id = new Phd2IdentityValidation(q.Session.Calibration.Profile,new(new("g3",true),new("mount",true),null,null,null),[],[]);
        var before = Phd2StateSnapshot.Disconnected with { IsConnected=true, Profile=id.Profile, Equipment=id.Equipment,
            ConnectionEpoch=1,GuideEpoch=1,LockPosition=ep,SelectedStar=q.Session.SelectedGuide,AppState=Phd2AppState.Guiding,EventSequence=10 };
        var when = f.Now.AddSeconds(-1);
        var lostChange = new Phd2GuideEpochChange(1,2,"event:StarLost",11,when,Phd2AppState.Guiding,Phd2AppState.LostLock);
        var found = new Phd2GuideEpochChange(2,3,"event:GuideStep",12,f.Now,Phd2AppState.LostLock,Phd2AppState.Guiding);
        var dropped = new Phd2GuideStep(16,null,null,mode=="low-snr"?0:40.64,5.76,3.26,mode=="low-snr"?2:8);
        var failed = before with { GuideEpoch=2,AppState=Phd2AppState.LostLock,EventSequence=11,LastGuideStep=dropped,
            LastGuideEpochChange=lostChange,RecentGuideEpochChanges=[lostChange],LastPreviewStarMissSequence=11 };
        var loss = new Phd2GuidingFrameLostException(failed,dropped) { LostEventSequence=11,LostEventUtc=when };
        var next = new Phd2GuideStep(18,.2,.3,41,5,1,0);
        var resumed = failed with { AppState=Phd2AppState.Guiding,GuideEpoch=3,LastGuideStep=next,EventSequence=12,
            LastGuideEpochChange=found,RecentGuideEpochChanges=[lostChange,found],
            GuideOutput=new(18,0,f.Now,f.Now,false,next) };
        resumed = mode switch {
            "different-selection" => resumed with { SelectedStar=new(210,100) },
            "wrong-lock" => resumed with { LockPosition=new(311,100) },
            "other-owner" => resumed with { ConnectionEpoch=4 },
            "recalibrated" => resumed with { CalibrationChangeSequence=2 },
            "second-loss" => resumed with { LastPreviewStarMissSequence=13 },
            "bad-output" => resumed with { GuideOutput=resumed.GuideOutput! with {Failed=true} }, _ => resumed };
        var witnessReads=0; var bindings=0; var capture=0; var polls=0;
        Phd2StateSnapshot ReadState()
        {
            polls++;
            if (mode is "corroborated-repeated-veto" or "unwitnessed-repeat" or "repeat-low-snr" or "missed-native-event" or "new-episode" or "missed-native-frame")
            {
                var repeat=lostChange with { PreviousEpoch=2,Epoch=3,EventSequence=12,
                    PreviousState=mode=="new-episode"?Phd2AppState.Guiding:Phd2AppState.LostLock };
                var s=failed with { GuideEpoch=3,EventSequence=12,LastPreviewStarMissSequence=12,
                    LastNativeStarLoss=new(12,mode=="missed-native-event"?8:11,f.Now,
                        dropped with {Frame=mode=="missed-native-frame"?19:17,Snr=mode=="repeat-low-snr"?0:40}),
                    LastGuideEpochChange=repeat,RecentGuideEpochChanges=[lostChange,repeat] };
                if(polls==1) return s;
                var end=found with { PreviousEpoch=3, Epoch=4, EventSequence=13 };
                return resumed with {GuideEpoch=4, EventSequence=13, LastPreviewStarMissSequence=12,
                    LastNativeStarLoss=s.LastNativeStarLoss, LastGuideEpochChange=end, RecentGuideEpochChanges=[lostChange,repeat,end] };
            }
            if(mode=="after-wait" && polls==1) return failed;
            if(mode=="missing-output-until-timeout" || mode=="after-wait" && polls==2)
                return resumed with { GuideOutput=null, LastGuideStep=dropped };
            return resumed;
        }
        IReadOnlyList<Phd2GuidingResidualState> Frames()
        {
            capture++;
            return q.InitialWindow.Select((m,i) => {
                var n=19+i;
                return m with { NativePrimaryMatched=mode!="unmatched-crop",
                    Measurement=m.Measurement with { FrameSha256=mode=="duplicate-frame"?new('A',64):m.Frame.Sha256,
                        TargetPositionAuthority=mode=="projection"?Phd2TargetPositionAuthority.CatalogWcsProjection:m.Measurement.TargetPositionAuthority,
                        TargetCentroid=mode=="other-component"?new(550,400):m.Measurement.TargetCentroid,
                        RecognizedSlitAcquisitionPoint=mode=="slit-changed"?new(550,400):m.Measurement.RecognizedSlitAcquisitionPoint },
                    Frame=m.Frame with { Sha256=mode=="duplicate-frame"?new('A',64):m.Frame.Sha256, EventSequence=n,TriggerGuideFrame=n,
                        NativeGuideStep=next with {Frame=n}, NativeLockPosition=ep, PrimaryStarImage=new(n,2,2,new(.5,.5),[1,2,3,4]),
                        GuideStepUtc=f.Now.AddMilliseconds(n), CompletedUtc=f.Now.AddMilliseconds(n+1) } };
            }).ToArray();
        }
        Task<Phd2DistanceGuardEvidence?> Observe() => Phd2DistanceGuardRecovery.ObserveAsync(
            q.Session,q.Preset,id,exact,before,loss,mode=="reserve-spent"?f.Now:
                mode=="missing-output-until-timeout"?f.Now.AddMilliseconds(35):f.Now.AddSeconds(20),
            (step,_,_) => { witnessReads++; return Task.FromResult<Phd2DistanceGuardWitness?>(
                mode=="no-distance-witness" || mode=="unwitnessed-repeat" && step.Frame>16
                ?null:new(step.Frame!.Value,new(100,100),25.87,7.4)); },
            ReadState, _ => mode=="unsafe"?throw new PhysicalActionGateException(GateResult.Unknown("UNSAFE","test")):Task.CompletedTask,
            _ => Task.FromResult(++bindings < 2 || mode!="binding-changed-after-frames"),
            _ => Task.FromResult(Frames()), new CancellationToken(mode=="cancel"),new Clock(mode=="stale"?f.Now.AddSeconds(8):f.Now));
        if (mode=="unsafe") { await Assert.ThrowsAsync<PhysicalActionGateException>(Observe); Assert.Equal(0,capture); return; }
        if (mode=="cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(Observe); Assert.Equal(0,witnessReads); return; }
        var result=await Observe();
        Assert.Equal(mode is "native-recovers" or "after-wait" or "corroborated-repeated-veto", result is not null);
        if(mode=="corroborated-repeated-veto") { Assert.Single(result!.AdditionalVetoes!); Assert.Equal(2,witnessReads); }
        if (mode=="after-wait") Assert.True(polls>=5);
        if (mode=="missing-output-until-timeout") Assert.Equal(0,capture);
        if(result is not null) {Assert.Equal(3,result.Proof.AcceptedResidualFrames); Assert.Equal(1,capture); Assert.Equal(2,bindings);}
        if(mode is "low-snr" or "stale" or "reserve-spent") Assert.Equal(0,witnessReads);
    }

    [Fact]
    public void OriginalStageNightAndAllReservedReturnStagesLimitTheOpticalWindow()
    {
        var f=new Phd2ConvergenceFixture(); var l=f.Request([5,5,5]).Preset.BuildMotionLimits() with
            {MaximumElapsed=TimeSpan.FromSeconds(30),MaximumStageDuration=TimeSpan.FromSeconds(8)};
        var t=f.Now;
        Assert.Equal(t.AddSeconds(6),Phd2DistanceGuardRecovery.Deadline(t.AddSeconds(20),t,l,3,null,null));
        Assert.Equal(t.AddSeconds(4),Phd2DistanceGuardRecovery.Deadline(t.AddSeconds(20),t,l,3,null,t.AddSeconds(4)));
        Assert.Equal(t.AddSeconds(2),Phd2DistanceGuardRecovery.Deadline(t.AddSeconds(20),t,l,3,t.AddSeconds(2),null));
        Assert.Equal(t.AddSeconds(1),Phd2DistanceGuardRecovery.Deadline(t.AddSeconds(1),t,l,3,null,null));
    }

    [Theory] [InlineData("skip")] [InlineData("cleanup-failure")]
    public Task RejectedDistanceGuardUsesCheckedNativeBoundary(string mode) =>
        new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(mode,Phd2StageFailurePolicy.PlacementStarLost,
            failureStage:ObservationStage.PlaceTargetOnSlit);
}
