using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class RuntimeCalibrationAgeTests
{
    private static readonly Phd2Profile Profile = new(2, "field-test");
    private static readonly Phd2CalibrationData Native = new(true, -132.7, 16.323, "+", 4.8, 18.106, "+", 35.5354);

    [Theory]
    [InlineData("compensated", true)]
    [InlineData("cache-cleared", true)]
    [InlineData("unchanged", true)]
    [InlineData("axis-changed", false)]
    [InlineData("dec-rate-changed", false)]
    [InlineData("parity-changed", false)]
    [InlineData("calibration-declination-changed", false)]
    [InlineData("profile-changed", false)]
    [InlineData("connection-changed", false)]
    [InlineData("StartCalibration", false)]
    [InlineData("CalibrationComplete", false)]
    [InlineData("CalibrationFailed", false)]
    [InlineData("CalibrationDataFlipped", false)]
    [InlineData("rate-invalid", false)]
    [InlineData("proof-aged", false)]
    [InlineData("proof-future", false)]
    [InlineData("no-proof", false)]
    public async Task FreshNativeReadbackControlsTimeProvenanceWithoutWeakeningQuality(string scenario, bool valid)
    {
        var current = Native with { RaRatePixelsPerSecond = 16.325 };
        current = scenario switch
        {
            "unchanged" => Native,
            "axis-changed" => current with { RaAngleDegrees = -130 },
            "dec-rate-changed" => current with { DecRatePixelsPerSecond = 19 },
            "parity-changed" => current with { RaParity = "-" },
            "calibration-declination-changed" => current with { DeclinationDegrees = 36 },
            "rate-invalid" => current with { RaRatePixelsPerSecond = 2000 },
            _ => current,
        };
        await using var server = new FakePhd2Server(async (session, token) =>
        {
            for (var index = 0; index < 3; index++)
            {
                var request = await session.ReadRequestAsync(token);
                if (index == 1)
                {
                    if (scenario is "StartCalibration" or "CalibrationComplete" or "CalibrationFailed" or "CalibrationDataFlipped")
                        await session.SendEventAsync(new { Event = scenario }, token);
                    if (scenario == "cache-cleared")
                        await session.SendEventAsync(new { Event = "ConfigurationChange" }, token);
                    await session.ReplyResultAsync(request, new
                    {
                        calibrated = current.Calibrated, xAngle = current.RaAngleDegrees,
                        xRate = current.RaRatePixelsPerSecond, xParity = current.RaParity,
                        yAngle = current.DecAngleDegrees, yRate = current.DecRatePixelsPerSecond,
                        yParity = current.DecParity, declination = current.DeclinationDegrees,
                    }, token);
                }
                else
                    await session.ReplyResultAsync(request,
                        new { id = scenario == "profile-changed" && index == 2 ? 3 : Profile.Id, name = Profile.Name }, token);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var client = new Phd2Client(new Phd2ClientOptions
        {
            Host = "127.0.0.1", Port = server.Port, CommandTimeout = TimeSpan.FromSeconds(5),
        });
        await client.ConnectAsync(CancellationToken.None);
        Assert.Null(client.Snapshot.CalibrationValidation);
        var proofTime = DateTimeOffset.UtcNow.AddMinutes(-3);
        if (scenario == "proof-aged") proofTime = DateTimeOffset.UtcNow.AddDays(-37);
        if (scenario == "proof-future") proofTime = DateTimeOffset.UtcNow.AddDays(1);
        var proof = new Phd2RuntimeCalibrationProof(Profile, Native,
            client.Snapshot.ConnectionEpoch + (scenario == "connection-changed" ? 1 : 0),
            client.Snapshot.CalibrationChangeSequence, proofTime);
        var result = await client.ValidateCalibrationAsync(new Phd2CalibrationRequirement(
            Profile.Id, Profile.Name, DateTimeOffset.UtcNow.AddDays(-37), TimeSpan.FromDays(30),
            MaximumOrthogonalityErrorDegrees: 60)
        {
            RuntimeProof = scenario == "no-proof" ? null : proof,
        }, CancellationToken.None);

        Assert.Equal(valid, result.IsValid);
        if (valid) Assert.InRange(result.CalibrationAge!.Value.TotalSeconds, 180, 190);
        if (scenario == "rate-invalid") Assert.Contains(result.Failures, value => value.Contains("RA", StringComparison.Ordinal));
        Assert.Equal(new[] { "get_profile", "get_calibration_data", "get_profile" }, server.ReceivedMethods.ToArray());
    }
}
