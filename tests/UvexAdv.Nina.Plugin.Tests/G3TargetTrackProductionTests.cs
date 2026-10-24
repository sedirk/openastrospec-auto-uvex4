using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;
using Xunit.Abstractions;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3TargetTrackProductionTests(ITestOutputHelper output)
{
    [Fact]
    public void IndependentRegistrationCannotUseTargetOrThreeStarSelfValidation()
    {
        var target = new PixelPoint(500, 400);
        var stars = new[] { Star(70, 65), Star(280, 80), Star(770, 120), Star(880, 700), Star(150, 600), Star(600, 750) };
        var current = stars.Select(s => s with { Centroid = new(s.Centroid.X + 12, s.Centroid.Y - 4) }).ToArray();
        var slit = new SlitGeometry("slit", new(500, 400), 0, 80, 3, .5, "camera", 1, 1);
        var registered = G3TargetTrackPolicy.Register(stars.Append(Star(500,400)).ToArray(),
            current.Append(Star(550,430)).ToArray(), target, new(512,396), slit, 1000, 800, 100, default);
        Assert.True(registered.Accepted, registered.Reason);
        Assert.Equal(3, registered.FitPairs.Count);
        Assert.True(registered.HoldoutPairs.Count >= 2);
        Assert.All(registered.FitPairs.Concat(registered.HoldoutPairs), p => Assert.True(Distance(p.Reference,target)>100));
        Assert.InRange(Distance(registered.Transform!.Apply(target), new(512,396)), 0, .001);
        Assert.False(G3TargetTrackPolicy.Register(stars.Take(3).ToArray(), current.Take(3).ToArray(),
            target, new(512,396), slit,1000,800,100,default).Accepted);
    }

    [Fact]
    public void DuplicateReversedStaleAndFutureMeasurementsCannotAdvanceTrack()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        Assert.True(G3TargetTrackPolicy.IsFresh(now.AddSeconds(-20),now.AddSeconds(-2),now,"a","b"));
        Assert.False(G3TargetTrackPolicy.IsFresh(now.AddSeconds(-20),now.AddSeconds(-2),now,"a","A"));
        Assert.False(G3TargetTrackPolicy.IsFresh(now.AddSeconds(-20),now.AddSeconds(-21),now,"a","b"));
        Assert.False(G3TargetTrackPolicy.IsFresh(now.AddSeconds(-121),now.AddSeconds(-2),now,"a","b"));
        Assert.False(G3TargetTrackPolicy.IsFresh(now.AddSeconds(-20),now.AddSeconds(1),now,"a","b"));
        var reference = new G3SparseExposureTiming(now.AddSeconds(-70),now.AddSeconds(-65),2,now.AddSeconds(-64));
        var current = new G3SparseExposureTiming(now.AddSeconds(-5),now.AddSeconds(-2),2,now);
        Assert.False(G3SparseFrameTimingPolicy.Evaluate(current,now,G3SparseTimingPurpose.RegisteredCurrentFrame,
            1,100,[reference]).Usable); // 60s adjacent bound is NOT extended by track TTL.
    }

    [Fact]
    public void ShapeIsComponentEvidenceNotAReplacementCentroidOrFlux()
    {
        var a = Image(64,64,32,32);
        var b = Image(64,64,34,31);
        Assert.True(G3TargetTrackPolicy.AcceptMeasuredCandidate(a,new(32,32),b,new(34,31),new(34,31),6,true,out var correlation));
        Assert.InRange(correlation,.99,1.000001);
        Assert.False(G3TargetTrackPolicy.AcceptMeasuredCandidate(a,new(32,32),b,new(42,31),new(34,31),6,true,out _));
        Assert.False(G3TargetTrackPolicy.AcceptMeasuredCandidate(a,new(32,32),Image(64,64,20,31),new(34,31),new(34,31),6,true,out _));
        Assert.True(G3TargetTrackPolicy.AcceptMeasuredCandidate(a,new(32,32),b,new(34,31),new(34,31),6,false,out var notComparable));
        Assert.True(double.IsNaN(notComparable));
        var raw=new ushort[64*64];
        Assert.False(G3TargetTrackPolicy.MatchesImmutablePixels(a,raw));
        Assert.True(G3TargetTrackPolicy.MatchesImmutablePixels(new(64,64,raw),raw));
    }

    [Theory]
    [InlineData("CaptureAndSelectPhd2GuideAtExposureAsync")]
    [InlineData("CapturePhd2GuidingMeasurementsAsync")]
    public void CompiledProductionEntrypointsConsumeAndCommitTheSameTrajectory(string method)
    {
        var calls = Calls(AsyncBody(typeof(RealObservationStageRunner),method)).Select(m=>m.Name).ToArray();
        Assert.Contains("PredictStellarTargetTrackAsync",calls);
        Assert.Contains("RejectStellarTargetTrackCandidate",calls);
        Assert.Contains("RememberStellarTargetTrackAsync",calls);
        Assert.Contains("RetainSparseG3FrameAsync",calls);
        Assert.Contains("TryWideFieldStellarMeasurementAsync",calls);
        // Both real async call sites, not just helper existence or source-text matching.
        Assert.Contains(Calls(AsyncBody(typeof(RealObservationStageRunner),"PredictStellarTargetTrackAsync")),
            m=>m.DeclaringType==typeof(G3TargetTrackPolicy) && m.Name=="Register");
    }

    [Fact]
    public void WideHintIsTestedAgainstActualPixelsAndCannotEnlargeTheOriginalRecognitionCircle()
    {
        var frame=Image(300,220,190,100); var candidates=StarFieldDetector.Detect(frame);
        var recovered=G3TargetTrackPolicy.MeasureSearchHint(frame,candidates,new(185,100),new(100,100),100,8,1.5);
        Assert.NotNull(recovered); Assert.InRange(Distance(recovered.Target!.Centroid,new(190,100)),0,1);
        Assert.Null(G3TargetTrackPolicy.MeasureSearchHint(frame,candidates,new(185,100),new(50,100),100,8,1.5));
        var outside=Image(300,220,260,100);
        Assert.Null(G3TargetTrackPolicy.MeasureSearchHint(outside,StarFieldDetector.Detect(outside),new(190,100),new(100,100),100,8,1.5));
        var blank=new MonochromeFrame(300,220,new ushort[300*220]);
        Assert.Null(G3TargetTrackPolicy.MeasureSearchHint(blank,StarFieldDetector.Detect(blank),new(185,100),new(100,100),100,8,1.5));
    }

    [Fact]
    public void HistoricalSuccessFalsePeakWingAndNoIndependentStarsUseProductionTrackAndDetector()
    {
        var report = Environment.GetEnvironmentVariable("UVEX_REPLAY_TARGET_TRACK_REPORT");
        if (string.IsNullOrWhiteSpace(report)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(report));
        var registeredCount=0; var wrongPeakCount=0; var unchanged=0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var frames = c.GetProperty("frames").EnumerateArray().ToArray();
            var sources = c.GetProperty("sourceVerification").EnumerateArray()
                .ToDictionary(s=>Path.GetFileName(s.GetProperty("path").GetString()!),s=>s.GetProperty("path").GetString()!);
            var reference = frames[0]; var referenceFrame=Read(reference);
            var point=Point(reference.GetProperty("position"));
            foreach (var fresh in frames.Skip(1))
            {
                var frame=Read(fresh); var diagnosticPoint=Point(fresh.GetProperty("position"));
                var registered=G3TargetTrackPolicy.Register(Stars(reference),Stars(fresh),point,diagnosticPoint,
                    new("slit",new(817,431),0,100,3,.5,"G3",1,1),frame.Width,frame.Height,100,default);
                if (!registered.Accepted)
                {
                    output.WriteLine($"Not registered {c.GetProperty("name")} {fresh.GetProperty("file")}: {registered.Code} {registered.Reason}");
                    Assert.Contains(c.GetProperty("name").GetString(),new[] { "hd218393_oct21_short_misses", "hd224559_oct21_multilobe", "hd219937_oct23_false_peak" });
                    if(c.GetProperty("name").GetString()=="hd219937_oct23_false_peak")
                    {
                        // This retained report has no native-primary receipt for
                        // these two images. Do not manufacture an end-to-end
                        // fallback success from the exploratory body coordinate.
                        // Production retains its existing primary/no-new-motion
                        // route, tested separately in recorded continuity tests.
                        continue;
                    }
                    Assert.False(fresh.GetProperty("positionWasIndependentlyUpdated").GetBoolean());
                    continue; // Forced signal never promoted to a measured position.
                }
                registeredCount++;
                var predicted=registered.Transform!.Apply(point);
                // The production registrar also masks the slit and detector-fixed
                // correspondences; its independently checked fit need not equal
                // the exploratory report's hand-picked graph edge bit-for-bit.
                Assert.InRange(Distance(predicted,diagnosticPoint),0,2.5);
                var measured=SlitTargetIdentifier.Identify(frame,StarFieldDetector.Detect(frame),predicted,6,8,1.5);
                Assert.True(measured.Gate.Disposition==GateDisposition.Passed,$"{c.GetProperty("name")} {fresh.GetProperty("file")}: {measured.Gate.Code} {measured.Gate.Message}");
                Assert.NotNull(measured.Target);
                Assert.True(G3TargetTrackPolicy.AcceptMeasuredCandidate(referenceFrame,point,frame,
                    measured.Target.Centroid,predicted,6,true,out var correlation),$"{c.GetProperty("name")} {fresh.GetProperty("file")} corr={correlation}");
                foreach (var wrong in fresh.GetProperty("recordedAlternativeDiagnostics").EnumerateArray())
                {
                    wrongPeakCount++;
                    Assert.False(G3TargetTrackPolicy.AcceptMeasuredCandidate(referenceFrame,point,frame,
                        Point(wrong.GetProperty("oldPoint")),predicted,6,true,out _));
                }
                output.WriteLine($"{c.GetProperty("name")} {fresh.GetProperty("file")}: independent={registered.MatchedStars}, holdout={registered.MaximumHoldoutResidualPixels:F3}px, measured={measured.Target.Centroid}, correlation={correlation:F3}.");
                // Exactly the production commit rule: roll only after this new
                // measured candidate passed; rejected peaks never re-anchor.
                reference=fresh; referenceFrame=frame; point=measured.Target.Centroid;
            }
            foreach(var source in sources.Values)
            {
                var expected=frames.Single(f=>f.GetProperty("file").GetString()==Path.GetFileName(source)).GetProperty("sha256").GetString();
                Assert.Equal(expected,Hash(source)); unchanged++;
            }

            MonochromeFrame Read(JsonElement row)
            {
                var path=sources[row.GetProperty("file").GetString()!];
                Assert.Equal(row.GetProperty("sha256").GetString(),Hash(path));
                var raw=QhyFitsCodec.Read(path); return new(raw.Width,raw.Height,raw.Pixels,
                    G3FrameInputPolicy.ResolveEffectiveSaturationAdu(raw.Pixels,4095));
            }
        }
        Assert.InRange(registeredCount,7,9); Assert.Equal(2,wrongPeakCount); Assert.Equal(17,unchanged);
        output.WriteLine($"17 immutable originals unchanged; {registeredCount} independent registered measurements, two false-peak controls; remaining frames are honest unavailable-registration controls, no sky/controller success claimed.");
    }

    private static StarCandidate Star(double x,double y)=>new(new(x,y),3000,20000,30,4,.1,0,20);
    private static double Distance(PixelPoint a,PixelPoint b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static PixelPoint Point(JsonElement p)=>new(p[0].GetDouble(),p[1].GetDouble());
    private static StarCandidate[] Stars(JsonElement row)=>row.GetProperty("registrationStars").EnumerateArray()
        .Select(s=>new StarCandidate(new(s.GetProperty("x").GetDouble(),s.GetProperty("y").GetDouble()),3000,
            s.GetProperty("flux").GetDouble(),s.GetProperty("snr").GetDouble(),4,.1,0,20)).ToArray();
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static MonochromeFrame Image(int width,int height,int x,int y)
    {
        var pixels=new ushort[width*height];
        for(var yy=0;yy<height;yy++) for(var xx=0;xx<width;xx++)
            pixels[yy*width+xx]=(ushort)(100+50000*Math.Exp(-(Math.Pow(xx-x,2)+Math.Pow(yy-y,2))/12));
        return new(width,height,pixels);
    }
    private static MethodInfo AsyncBody(Type type,string name)=>type.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Instance)!;
    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode))
            .Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(c=>(ushort)c.Value);
        var bytes=method.GetMethodBody()!.GetILAsByteArray()!;
        for(var i=0;i<bytes.Length;)
        {
            ushort value=bytes[i++]; if(value==0xfe) value=(ushort)(0xfe00|bytes[i++]);
            var op=codes[value]; var start=i;
            i+=op.OperandType switch { OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(bytes,i),_=>4 };
            if(op.OperandType==OperandType.InlineMethod)
            {
                var resolved=method.Module.ResolveMethod(BitConverter.ToInt32(bytes,start));
                if(resolved is not null) yield return resolved;
            }
        }
    }
}
