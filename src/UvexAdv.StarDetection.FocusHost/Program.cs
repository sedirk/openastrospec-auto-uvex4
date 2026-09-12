using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot.Series;
using UvexAdv.Phd2;

if (args.Length == 3 && args[0] == "fit") {
    // Read-only reuse of the installed NINA curve implementation. Does not
    // instantiate its autofocus VM or select/open its science camera.
    var install = Path.GetFullPath(args[2]);
    AssemblyLoadContext.Default.Resolving += (context, name) => {
        var path = Path.Combine(install, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    };
    Fit(args[1]);
} else if (args.Length == 7 && args[0] == "capture" && args[1] == "--confirm-hardware") {
    var path = Path.GetFullPath(args[2]);
    if (File.Exists(path)) throw new IOException("Immutable output already exists");
    var exposure = int.Parse(args[3]);
    var gain = int.Parse(args[4]);
    var expectedProfile = int.Parse(args[5]);
    var expectedCamera = args[6];
    if (exposure < 50 || exposure > 10000 || gain < 0 || gain > 100)
        throw new ArgumentOutOfRangeException();
    await using var client = new Phd2Client(new Phd2ClientOptions { Host = "127.0.0.1", Port = 4400 });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await client.ConnectAsync(timeout.Token);
    var profile = await client.GetProfileAsync(timeout.Token);
    var equipment = await client.GetCurrentEquipmentAsync(timeout.Token);
    if (profile.Id != expectedProfile || equipment.Camera?.Name != expectedCamera || !equipment.Camera.Connected)
        throw new InvalidOperationException("Unexpected PHD2 owner profile/camera");
    // Native single-frame capture itself requires stopped PHD2 and an exact
    // SingleFrameComplete path/epoch. Never direct-open or reassign the camera.
    var result = await client.CaptureSingleFrameWithParametersAsync(
        new Phd2SingleFrameRequest(exposure, 1, gain, path), timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(result));
} else {
    throw new ArgumentException("fit points.json NINA-install-directory OR capture --confirm-hardware new.fit exposure-ms gain-percent profile-id camera-name (PHD2 must already be stopped)");
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void Fit(string pointsPath) {
    var rows = JsonSerializer.Deserialize<double[][]>(File.ReadAllText(pointsPath))!;
    if (rows.Length < 5 || rows.Length > 50 || rows.Any(r => r.Length != 3 || r.Any(v => !double.IsFinite(v)) || r[1] <= 0 || r[2] <= 0))
        throw new ArgumentException("Five to fifty finite positive points with nonzero errors required");
    var fit = new HyperbolicFitting().Calculate(rows.Select(r => new ScatterErrorPoint(r[0], r[1], 0, r[2])).ToList());
    if (fit.Fitting == null || !double.IsFinite(fit.RSquared) || fit.Minimum.X <= rows.Min(r => r[0]) || fit.Minimum.X >= rows.Max(r => r[0]))
        throw new InvalidOperationException("No valid bracketed native NINA hyperbola");
    Console.WriteLine(JsonSerializer.Serialize(new {
        position = fit.Minimum.X, minimum = fit.Minimum.Y, rSquared = fit.RSquared, expression = fit.Expression,
        assembly = typeof(HyperbolicFitting).Assembly.FullName,
        samples = Enumerable.Range(0, 101).Select(i => {
            var x = rows.Min(r => r[0]) + i / 100.0 * (rows.Max(r => r[0]) - rows.Min(r => r[0]));
            return new[] { x, fit.Fitting(x) };
        }).ToArray()
    }));
}
