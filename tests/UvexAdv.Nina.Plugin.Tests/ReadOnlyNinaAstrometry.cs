using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

// NOVAS and AstroUtil expose process-wide native/cache state. These ephemeris
// boundary tests pass in isolation but must not overlap other native consumers
// in the full suite. This changes test scheduling only, never production gates.
[CollectionDefinition("Read-only NINA ephemeris boundary", DisableParallelization = true)]
public sealed class ReadOnlyNinaEphemerisCollection { }

/// <summary>Initialize NINA's existing ephemeris once in the test process only.</summary>
internal static class ReadOnlyNinaAstrometry
{
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        const string nina = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NINA.Astrometry.NOVAS).Assembly,
                (name, _, _) => name switch
                {
                    "NOVAS31lib.dll" => NativeLibrary.Load(Path.Combine(nina, "External", "x64", "NOVAS", name)),
                    "SOFAlib.dll" => NativeLibrary.Load(Path.Combine(nina, "External", "x64", "SOFA", name)),
                    _ => IntPtr.Zero
                });
        }
        catch (InvalidOperationException) { /* Another read-only fixture owns the resolver. */ }

        // The test host has no External/JPLEPH. Opening the installed read-only
        // file avoids NaN positions and accidental dependence on test order.
        var open = typeof(NINA.Astrometry.NOVAS).GetMethod("EphemOpen", BindingFlags.NonPublic | BindingFlags.Static)!;
        object[] args = [Path.Combine(nina, "External", "JPLEPH"), 0d, 0d, (short)0];
        Assert.Equal((short)0, (short)open.Invoke(null, args)!);

        // Tests must not need the running application's EOP database/provider.
        // A fixed DUT1 of 0.1 s keeps these degree-scale twilight fixtures
        // deterministic; it is NOT a fallback used by the production gate.
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var astro = typeof(NINA.Astrometry.AstroUtil);
        astro.GetField("DeltaUTReference", flags)!.SetValue(null, DateTime.UtcNow.Date);
        foreach (var field in new[] { "DeltaUTYesterday", "DeltaUTToday", "DeltaUTTomorrow" })
            astro.GetField(field, flags)!.SetValue(null, 0.1d);
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<DateTime, double>)
            astro.GetField("DeltaUTCache", flags)!.GetValue(null)!;
        cache[new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)] = 0.1;
        cache[new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc)] = 0.1;
        // Later historical fixtures may change AstroUtil's rolling reference.
        // Pin the current-night fixture dates too; otherwise tests executed at
        // the end of the full suite fall back to an unavailable EOP database.
        for (var offset = -1; offset <= 1; offset++) cache[DateTime.UtcNow.Date.AddDays(offset)] = 0.1;
        return true;
    });

    internal static void Initialize() => _ = Initialized.Value;
}
