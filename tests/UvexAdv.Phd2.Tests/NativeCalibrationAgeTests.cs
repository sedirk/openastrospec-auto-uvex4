using System.Globalization;
using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class NativeCalibrationAgeTests
{
    private static readonly Phd2Profile Profile = new(2, "field-test");
    private static readonly Phd2CalibrationData Active = new(true, -132.7, 16.325, "+", 4.8, 18.106, "+", 35.5354);
    private static string Export(string originalTime) => $"""
        PHD Config 1
        /profile/2/name	1	field-test
        /profile/2/scope/calibration/timestamp	1	2099/1/1 12:00:00
        /profile/2/scope/calibration/orig_timestamp	1	{originalTime}
        /profile/2/scope/calibration/xAngle	1	-2.31682
        /profile/2/scope/calibration/yAngle	1	0.0836883
        /profile/2/scope/calibration/xRate	1	0.0163226
        /profile/2/scope/calibration/yRate	1	0.0181064
        /profile/2/scope/calibration/raGuideParity	3	1
        /profile/2/scope/calibration/decGuideParity	3	1
        /profile/2/scope/calibration/declination	1	0.620211
        """;

    [Fact]
    public void NativeOriginalTimeNotExportTimeSurvivesClientRestartAndRaCompensation()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("field", TimeSpan.FromHours(8), "field", "field");
        var utc = Phd2NativeCalibrationAge.Read(Export("2026/10/2 20:33:51"), Profile, Active,
            CultureInfo.InvariantCulture, zone);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 33, 51, TimeSpan.Zero), utc);
    }

    [Theory]
    [InlineData("missing-original")]
    [InlineData("bad-time")]
    [InlineData("bad-header")]
    [InlineData("duplicate")]
    [InlineData("profile")]
    [InlineData("ra-angle")]
    [InlineData("dec-angle")]
    [InlineData("dec-rate")]
    [InlineData("declination")]
    [InlineData("parity")]
    [InlineData("unknown-parity")]
    [InlineData("nonfinite")]
    [InlineData("uncalibrated")]
    public void ExportCannotLendTimeToDifferentOrIncompleteCalibration(string scenario)
    {
        var export = Export("2026/10/2 20:33:51");
        var active = Active;
        export = scenario switch
        {
            "missing-original" => export.Replace("orig_timestamp", "missing"),
            "bad-time" => export.Replace("2026/10/2 20:33:51", "unknown"),
            "bad-header" => export.Replace("PHD Config 1", "PHD Config 2"),
            "duplicate" => export + "\n/profile/2/name\t1\tfield-test",
            "profile" => export.Replace("field-test", "different"),
            "ra-angle" => export.Replace("-2.31682", "-2.4"),
            "dec-angle" => export.Replace("0.0836883", "0.1"),
            "dec-rate" => export.Replace("0.0181064", "0.019"),
            "declination" => export.Replace("0.620211", "0.621"),
            "parity" => export.Replace("raGuideParity\t3\t1", "raGuideParity\t3\t-1"),
            "unknown-parity" => export.Replace("decGuideParity\t3\t1", "decGuideParity\t3\t0"),
            "nonfinite" => export.Replace("0.0163226", "NaN"),
            _ => export,
        };
        if (scenario == "uncalibrated") active = active with { Calibrated = false };
        Assert.Throws<InvalidDataException>(() => Phd2NativeCalibrationAge.Read(export, Profile, active,
            CultureInfo.InvariantCulture, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("restart", true)]
    [InlineData("old-native", false)]
    [InlineData("future-native", false)]
    [InlineData("changed-native", false)]
    [InlineData("profile-race", false)]
    [InlineData("calibration-race", false)]
    [InlineData("missing-export", false)]
    [InlineData("wrong-path", false)]
    [InlineData("bad-rate", false)]
    [InlineData("bad-orthogonality", false)]
    public async Task FreshClientUsesOwnerExportWithoutGuidingOrMotion(string scenario, bool valid)
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-native-age-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "phd2_settings.txt");
        var time = DateTime.Now.AddMinutes(-3);
        if (scenario == "old-native") time = DateTime.Now.AddDays(-37);
        if (scenario == "future-native") time = DateTime.Now.AddDays(1);
        var export = Export(time.ToString("yyyy/M/d H:mm:ss", CultureInfo.InvariantCulture));
        if (scenario == "changed-native") export = export.Replace("-2.31682", "-2.4");
        await File.WriteAllTextAsync(path, export);
        try
        {
            await using var server = new FakePhd2Server(async (session, token) =>
            {
                for (var index = 0; index < 4; index++)
                {
                    var request = await session.ReadRequestAsync(token);
                    if (index == 1)
                        await session.ReplyResultAsync(request, new
                        {
                            calibrated = true, xAngle = Active.RaAngleDegrees,
                            xRate = scenario == "bad-rate" ? 2000 : Active.RaRatePixelsPerSecond,
                            xParity = "+", yAngle = Active.DecAngleDegrees,
                            yRate = Active.DecRatePixelsPerSecond, yParity = "+", declination = Active.DeclinationDegrees,
                        }, token);
                    else if (index == 2)
                    {
                        if (scenario == "calibration-race") await session.SendEventAsync(new { Event = "StartCalibration" }, token);
                        if (scenario == "missing-export") await session.ReplyErrorAsync(request, -32601, "unsupported", token);
                        else await session.ReplyResultAsync(request,
                            new { filename = scenario == "wrong-path" ? "relative-file.txt" : path }, token);
                    }
                    else await session.ReplyResultAsync(request,
                        new { id = scenario == "profile-race" && index == 3 ? 3 : 2, name = Profile.Name }, token);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            await using var client = new Phd2Client(new Phd2ClientOptions { Port = server.Port });
            await client.ConnectAsync(CancellationToken.None);
            var result = await client.ValidateCalibrationAsync(new Phd2CalibrationRequirement(
                2, Profile.Name, DateTimeOffset.UtcNow.AddDays(-37), TimeSpan.FromDays(30),
                MaximumOrthogonalityErrorDegrees: scenario == "bad-orthogonality" ? 15 : 60)
            { ReadNativeCalibrationTimestamp = true }, CancellationToken.None);
            Assert.Equal(valid, result.IsValid);
            if (valid)
            {
                Assert.InRange(result.CalibrationAge!.Value.TotalSeconds, 180, 190);
                Assert.Contains("orig_timestamp", result.CalibrationTimestampSource, StringComparison.Ordinal);
            }
            Assert.Equal(new[] { "get_profile", "get_calibration_data", "export_config_settings", "get_profile" },
                server.ReceivedMethods.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
