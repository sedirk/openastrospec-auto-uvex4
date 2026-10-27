using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record MeasuredAstapRuntime(string PythonPath, string AstapPath, string AstapSha256, string DatabasePath);
internal sealed record MeasuredAstapAttempt(DetectorWcsResult? Result, string Reason, string? EvidencePath);

/// <summary>One image-only fallback per raw hash; no device operations or budget grants.</summary>
internal sealed class MeasuredAstapRecovery(string? configuredRuntime = null, string? configuredWorker = null)
{
    internal const string Identity = "OpenAstroSpec.SEP-ASTAP.RawVerified.v1";
    private readonly HashSet<string> attempted = new(StringComparer.OrdinalIgnoreCase);
    private static int unjoined;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal bool Claim(string role, string primaryIdentity, bool primarySucceeded, string hash)
    {
        if (primarySucceeded || !role.StartsWith("PHD2/G3", StringComparison.Ordinal) ||
            !G3WcsTargetProjector.IsPlateSolve3(primaryIdentity) || hash.Length != 64) return false;
        lock (attempted) return attempted.Add(hash);
    }

    internal async Task<MeasuredAstapAttempt> SolveAsync(string source, string hash, int width, int height,
        Coordinates hint, double scale, double radius, string evidenceRoot, CancellationToken token)
    {
        if (Volatile.Read(ref unjoined) != 0) throw new IOException("ASTAP_CHILD_NOT_JOINED: Analysis disabled until restart.");
        var runtimePath = configuredRuntime ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV", "star-detection", "astap-runtime.json");
        var worker = configuredWorker ?? Path.Combine(Path.GetDirectoryName(typeof(MeasuredAstapRecovery).Assembly.Location)!, "Analysis", "astap_recovery.py");
        if (!File.Exists(runtimePath) || !File.Exists(worker)) return new(null, "Optional ASTAP runtime not installed", null);
        var runtime = JsonSerializer.Deserialize<MeasuredAstapRuntime>(await File.ReadAllTextAsync(runtimePath, token), JsonOptions);
        if (runtime is null || !File.Exists(runtime.PythonPath) || !File.Exists(runtime.AstapPath) ||
            !Directory.Exists(runtime.DatabasePath)) return new(null, "Optional ASTAP runtime incomplete", null);
        var root = Path.Combine(evidenceRoot, "astap-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var request = Path.Combine(root, "request.json");
        var output = Path.Combine(root, "analysis");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
        {
            sourcePath = Path.GetFullPath(source), sourceSha256 = hash, runtime.AstapPath, runtime.AstapSha256,
            runtime.DatabasePath, ra = hint.RADegrees, dec = hint.Dec, scale, radiusDegrees = radius,
        }, JsonOptions), token);
        var start = new ProcessStartInfo(runtime.PythonPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-E", "-s", worker, "--request", request, "--output", output }) start.ArgumentList.Add(argument);
        try
        {
            await OwnedPlateSolve3Solver.RunChildAsync(start, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.Message.Contains("CHILD_NOT_JOINED", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref unjoined, 1);
            throw;
        }
        catch (TimeoutException) { return new(null, "Optional ASTAP analysis timed out within original budget", root); }
        var evidence = Path.Combine(output, "result.json");
        if (!File.Exists(evidence) || new FileInfo(evidence).Length > 4_000_000) return new(null, "Optional ASTAP result missing/oversized", root);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(evidence, token).ConfigureAwait(false));
        var r = json.RootElement;
        var reason = r.TryGetProperty("reason", out var why) ? why.GetString() ?? "unknown" : "unknown";
        if (reason == "source_hash_changed") throw new IOException("PLATE_SOLVE_SOURCE_CHANGED: Immutable source changed during ASTAP recovery.");
        if (!r.GetProperty("accepted").GetBoolean()) return new(null, reason, evidence);
        return new(ParseVerified(r, hash, width, height), reason, evidence);
    }

    internal static DetectorWcsResult ParseVerified(JsonElement r, string hash, int width, int height)
    {
        if (r.GetProperty("schema").GetInt32() != 1 || r.GetProperty("algorithm").GetString() != "sep-bin2-astap-raw-verified-v1" ||
            r.GetProperty("targetIdentityConfirmed").GetBoolean() || r.GetProperty("motionAuthorized").GetBoolean() ||
            !string.Equals(r.GetProperty("sourceSha256").GetString(), hash, StringComparison.OrdinalIgnoreCase) ||
            r.GetProperty("width").GetInt32() != width || r.GetProperty("height").GetInt32() != height)
            throw new InvalidDataException("ASTAP recovery provenance is inconsistent.");
        var pairs = r.GetProperty("matches").EnumerateArray().ToArray();
        var span = r.GetProperty("spanPixels").Deserialize<double[]>()!;
        var negatives = r.GetProperty("negativeCounts").Deserialize<int[]>()!;
        if (pairs.Length < 5 || pairs.Length > 300 || span.Length != 2 || !span.All(double.IsFinite) || span[0] < .2*width || span[1] < .2*height ||
            negatives.Length != 100 || negatives.Any(n => n < 0) || negatives.Max() >= pairs.Length ||
            pairs.Select(p => p.GetProperty("catalogueIndex").GetInt32()).Distinct().Count() != pairs.Length ||
            pairs.Select(p => p.GetProperty("detectionIndex").GetInt32()).Distinct().Count() != pairs.Length)
            throw new InvalidDataException("ASTAP raw support is incomplete.");
        var origin = r.GetProperty("crpix").Deserialize<double[]>()!;
        var cd = r.GetProperty("cd").Deserialize<double[][]>()!;
        var result = new DetectorWcsResult(r.GetProperty("ra").GetDouble(), r.GetProperty("dec").GetDouble(), origin, cd, width, height);
        foreach (var p in pairs)
        {
            var measured = p.GetProperty("measured").Deserialize<double[]>()!;
            var point = result.Project(new Coordinates(p.GetProperty("catalogueRa").GetDouble(), p.GetProperty("catalogueDec").GetDouble(), Epoch.J2000, Coordinates.RAType.Degrees));
            if (measured.Length != 2 || !measured.All(double.IsFinite) || measured[0] < 0 || measured[0] >= width || measured[1] < 0 || measured[1] >= height ||
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) || Math.Sqrt(Math.Pow(point.X-measured[0], 2)+Math.Pow(point.Y-measured[1], 2)) > 3.00001)
                throw new InvalidDataException("ASTAP original-pixel projection did not reproduce verified matches.");
        }
        return result;
    }
}

/// <summary>Retain TAN/CD geometry, skew, parity and zero-based reference pixel.
/// NINA's scalar angle/scale approximation loses several pixels in sparse fits.</summary>
internal sealed class DetectorWcsResult : PlateSolveResult
{
    internal double[] ReferencePixel { get; }
    internal double[][] Cd { get; }
    internal Coordinates ReferenceSky { get; private set; }
    internal DetectorWcsResult(double ra, double dec, double[] pixel, double[][] cd, int width, int height)
    {
        if (pixel.Length != 2 || cd.Length != 2 || cd.Any(row => row.Length != 2) ||
            !pixel.Concat(cd.SelectMany(row => row)).Concat([ra, dec]).All(double.IsFinite) || Math.Abs(dec) > 90)
            throw new InvalidDataException("Invalid TAN geometry.");
        var determinant = cd[0][0]*cd[1][1]-cd[0][1]*cd[1][0];
        if (Math.Abs(determinant) < 1e-14) throw new InvalidDataException("Singular TAN geometry.");
        ReferencePixel = pixel.ToArray(); Cd = cd.Select(row => row.ToArray()).ToArray();
        Coordinates = ReferenceSky = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
        Success = true; Pixscale = Math.Sqrt(Math.Abs(determinant))*3600; Flipped = determinant < 0;
        var native = new WorldCoordinateSystem(ra, dec, pixel[0]+1, pixel[1]+1, cd[0][0], cd[0][1], cd[1][0], cd[1][1]);
        PositionAngle = (540-native.Rotation)%360;
        Radius = Math.Sqrt((double)width*width+(double)height*height)*Pixscale/7200;
    }

    internal PixelPoint Project(Coordinates target)
    {
        var sky = target.Epoch == ReferenceSky.Epoch ? target : target.Transform(ReferenceSky.Epoch);
        const double rad = Math.PI/180;
        var d = sky.Dec*rad; var d0 = ReferenceSky.Dec*rad; var a = (sky.RADegrees-ReferenceSky.RADegrees)*rad;
        var z = Math.Sin(d)*Math.Sin(d0)+Math.Cos(d)*Math.Cos(d0)*Math.Cos(a);
        if (z <= 0) throw new ArgumentException("Target is outside TAN hemisphere.");
        var east = Math.Cos(d)*Math.Sin(a)/z/rad;
        var north = (Math.Sin(d)*Math.Cos(d0)-Math.Cos(d)*Math.Sin(d0)*Math.Cos(a))/z/rad;
        var det = Cd[0][0]*Cd[1][1]-Cd[0][1]*Cd[1][0];
        return new(ReferencePixel[0]+(east*Cd[1][1]-north*Cd[0][1])/det,
            ReferencePixel[1]+(north*Cd[0][0]-east*Cd[1][0])/det);
    }

    internal DetectorWcsResult WithCenter(Coordinates center, int width, int height) =>
        new(ReferenceSky.RADegrees+center.RADegrees-Coordinates.RADegrees,
            ReferenceSky.Dec+center.Dec-Coordinates.Dec, ReferencePixel, Cd, width, height);
}
