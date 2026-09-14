using System.Text.Json;
using UvexAdv.Observatory;
using Xunit;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StellariumCoordinateNormalizerTests
{
    [Fact]
    public void NinaEphemerisRemovesWr152RecordedAberrationWithoutMovingItsCatalogPoint()
    {
        // A recorded RemoteControl coordinate and independently published SIMBAD
        // astrometry, not a forward/inverse test with the same injected vector.
        const string nina = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
        var native = NativeLibrary.Load(Path.Combine(nina,"External","x64","NOVAS","NOVAS31lib.dll"));
        var assembly=typeof(NINA.Astrometry.NOVAS).Assembly;
        try { NativeLibrary.SetDllImportResolver(assembly,(name,_,_)=>name switch
        {
            "NOVAS31lib.dll"=>native,
            "SOFAlib.dll"=>NativeLibrary.Load(Path.Combine(nina,"External","x64","SOFA","SOFAlib.dll")),
            _=>IntPtr.Zero
        }); }
        catch(InvalidOperationException) { /* Existing native astrometry fixture owns the resolver. */ }
        // Initialization may have occurred earlier without a test-host ephemeris.
        // Open NINA's existing read-only ephemeris in this TEST process only.
        var method=typeof(NINA.Astrometry.NOVAS).GetMethod("EphemOpen",BindingFlags.NonPublic|BindingFlags.Static)!;
        object[] args=[Path.Combine(nina,"External","JPLEPH"),0d,0d,(short)0];
        Assert.Equal((short)0,(short)method.Invoke(null,args)!);
        var state=State with {JulianDay=2461298.1927,DeltaTDays=.00078943};
        var result=StellariumCoordinateNormalizer.Normalize(new(334.1089122226066,55.629439466874345),state,"CustomObject","SIMBAD; WR*");
        var distance=UvexAdv.Observatory.G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            334.10013285239,55.62687988062,result.Coordinates.RightAscensionDegrees,result.Coordinates.DeclinationDegrees);
        Assert.InRange(distance,0,.25); // Source time rounded; independent ephemerides may differ slightly.
        Assert.InRange(result.Provenance.CorrectionArcseconds,19.5,20.5);
    }

    private static readonly StellariumCoordinateNormalizer.State State = new(2461298.2,.00079,"Earth",true,1,true,1);
    private static readonly StellariumCoordinateNormalizer.Velocity Beta = new(.00003,.00008,.000035);

    [Theory]
    [InlineData(334.10013285239,55.62687988062)] // WR152, independent catalogue fixture
    [InlineData(0.001,-89.9)]
    [InlineData(359.999,89.9)]
    [InlineData(10.6847,41.26875)]
    public void InvertsStellariumVectorNotAnotherJnowPrecession(double ra,double dec)
    {
        var a=ra*Math.PI/180; var d=dec*Math.PI/180;
        var x=Math.Cos(d)*Math.Cos(a)+Beta.X;
        var y=Math.Cos(d)*Math.Sin(a)+Beta.Y;
        var z=Math.Sin(d)+Beta.Z;
        var raw=new ObservationTargetCoordinates(Math.Atan2(y,x)*180/Math.PI,Math.Atan2(z,Math.Sqrt(x*x+y*y))*180/Math.PI);
        var result=StellariumCoordinateNormalizer.Normalize(raw,State,"CustomObject","SIMBAD; WR*",_=>Beta);
        Assert.Equal(ra,result.Coordinates.RightAscensionDegrees,7);
        Assert.Equal(dec,result.Coordinates.DeclinationDegrees,7);
        Assert.InRange(result.Provenance.CorrectionArcseconds,0,21);
        Assert.Equal("AstrometricJ2000",result.Provenance.Convention);
    }

    [Fact]
    public void DisabledAberrationDoesNotCallEphemerisOrDoubleCorrect()
    {
        var result=StellariumCoordinateNormalizer.Normalize(new(-25.9,55.6),State with {AberrationEnabled=false},
            "star",null,_=>throw new Exception("Must not be called"));
        Assert.Equal(334.1,result.Coordinates.RightAscensionDegrees,8);
        Assert.Equal(55.6,result.Coordinates.DeclinationDegrees,8);
    }

    [Fact]
    public void LocalizedDsoUsesMachineReadableTypeButSolarSystemAndMarkersAreNotFixedCatalogues()
    {
        Assert.NotNull(StellariumCoordinateNormalizer.Normalize(new(10,40),State,"星系","galaxy",_=>Beta).Provenance);
        Assert.Throws<InvalidOperationException>(()=>StellariumCoordinateNormalizer.Normalize(new(10,40),State,"Planet","star",_=>Beta));
        Assert.Throws<InvalidOperationException>(()=>StellariumCoordinateNormalizer.Normalize(new(10,40),State,"CustomObject",null,_=>Beta));
    }

    [Fact]
    public void UsesApiDeltaTInDaysAndRejectsChangingSnapshot()
    {
        double called=0;
        _=StellariumCoordinateNormalizer.Normalize(new(10,40),State,"star",null,jd=>{called=jd;return Beta;});
        Assert.Equal(State.JulianDay+State.DeltaTDays,called,9);
        Assert.True(StellariumCoordinateNormalizer.SameState(State,State with {JulianDay=State.JulianDay+1d/86400}));
        Assert.False(StellariumCoordinateNormalizer.SameState(State,State with {AberrationEnabled=false}));
        Assert.False(StellariumCoordinateNormalizer.SameState(State,State with {JulianDay=State.JulianDay+1}));
        Assert.False(StellariumCoordinateNormalizer.SameState(State,State with {Planet="Mars"}));
    }

    [Fact]
    public void OptionalProvenanceDoesNotChangeOldMetadataSerialization()
    {
        var metadata=new TargetCatalogMetadata("M31","M31",10,40,"manual","galaxy",3,DateTimeOffset.UtcNow);
        Assert.DoesNotContain("CoordinateProvenance",JsonSerializer.Serialize(metadata));
    }
}
