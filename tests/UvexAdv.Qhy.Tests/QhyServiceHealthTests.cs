using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Tests;

public sealed class QhyServiceHealthTests
{
    private const string SdkHash = "5F0957E29FF510F19FA5DE8688162FB4B9F4562B0B79B693DCBB9377BF281D14";

    [Fact]
    public void CanonicalProofIsDeterministicAndSelfValidating()
    {
        var first = QhyServiceConfigurationProof.Create(
            simulator: false,
            adapter: " QHY-NATIVE ",
            expectedModel: " QHYminiCam8M ",
            expectedStableId: " QHYminiCam8M-device-id ",
            nativeSdkSha256: SdkHash.ToLowerInvariant(),
            nativeReadoutMode: 1,
            nativeFilterPositions: new Dictionary<string, int>
            {
                ["Red"] = 2,
                ["Luminance"] = 0,
            });
        var second = QhyServiceConfigurationProof.Create(
            simulator: false,
            adapter: "qhy-native",
            expectedModel: "QHYminiCam8M",
            expectedStableId: "QHYminiCam8M-device-id",
            nativeSdkSha256: SdkHash,
            nativeReadoutMode: 1,
            nativeFilterPositions: new Dictionary<string, int>
            {
                ["luminance"] = 0,
                ["red"] = 2,
            });

        Assert.Empty(first.Validate());
        Assert.Equal(second, first);
        Assert.True(QhyServiceConfigurationProof.IsSha256(first.ConfigurationSha256));
        Assert.Equal(SdkHash, first.NativeSdkSha256);
        Assert.True(QhyServiceConfigurationProof.IsSha256(first.NativeFilterPositionsSha256));
        Assert.Equal("F37C9C6F99CFA1A985723F1F48ADA1BD86CF0289F594CFE23273CF018DAE4DF6", first.ConfigurationSha256);
    }

    [Fact]
    public void EveryProductionIdentityFieldParticipatesInCanonicalHash()
    {
        var baseline = QhyServiceConfigurationProof.Create(
            false,
            "qhy-native",
            "QHYminiCam8M",
            "camera-a",
            SdkHash,
            1,
            new Dictionary<string, int> { ["Clear"] = 0 });

        var alternatives = new[]
        {
            QhyServiceConfigurationProof.Create(true, "qhy-native", "QHYminiCam8M", "camera-a", SdkHash, 1, new Dictionary<string, int> { ["Clear"] = 0 }),
            QhyServiceConfigurationProof.Create(false, "native-v2", "QHYminiCam8M", "camera-a", SdkHash, 1, new Dictionary<string, int> { ["Clear"] = 0 }),
            QhyServiceConfigurationProof.Create(false, "qhy-native", "QHYminiCam8M-v2", "camera-a", SdkHash, 1, new Dictionary<string, int> { ["Clear"] = 0 }),
            QhyServiceConfigurationProof.Create(false, "qhy-native", "QHYminiCam8M", "camera-b", SdkHash, 1, new Dictionary<string, int> { ["Clear"] = 0 }),
            QhyServiceConfigurationProof.Create(false, "qhy-native", "QHYminiCam8M", "camera-a", new string('A', 64), 1, new Dictionary<string, int> { ["Clear"] = 0 }),
            QhyServiceConfigurationProof.Create(false, "qhy-native", "QHYminiCam8M", "camera-a", SdkHash, 2, new Dictionary<string, int> { ["Clear"] = 0 }),
            QhyServiceConfigurationProof.Create(false, "qhy-native", "QHYminiCam8M", "camera-a", SdkHash, 1, new Dictionary<string, int> { ["Clear"] = 1 }),
        };

        Assert.All(alternatives, value => Assert.NotEqual(baseline.ConfigurationSha256, value.ConfigurationSha256));
    }

    [Fact]
    public void TamperedProofAndHardwareWithoutSdkHashAreRejected()
    {
        var proof = QhyServiceConfigurationProof.Create(
            false,
            "qhy-native",
            "QHYminiCam8M",
            "camera-a",
            SdkHash,
            1,
            new Dictionary<string, int> { ["R"] = 6 });

        Assert.Contains(
            (proof with { ExpectedStableId = "camera-b" }).Validate(),
            issue => issue.Contains("does not match", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => QhyServiceConfigurationProof.Create(
            false,
            "qhy-native",
            "QHYminiCam8M",
            "camera-a",
            string.Empty,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => QhyServiceConfigurationProof.Create(
            false,
            "qhy-native",
            "QHYminiCam8M",
            "camera-a",
            SdkHash,
            -1));
        Assert.Contains(
            (proof with { NativeReadoutMode = 2 }).Validate(),
            issue => issue.Contains("does not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HardwareFilterMapIsRequiredAndUnambiguous()
    {
        Assert.Throws<ArgumentException>(() => QhyServiceConfigurationProof.Create(
            false, "qhy-native", "QHYminiCam8M", "camera-a", SdkHash, 1));
        Assert.Throws<ArgumentException>(() => QhyServiceConfigurationProof.Create(
            false,
            "qhy-native",
            "QHYminiCam8M",
            "camera-a",
            SdkHash,
            1,
            new Dictionary<string, int> { ["Z"] = 4, ["I"] = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => QhyServiceConfigurationProof.Create(
            false,
            "qhy-native",
            "QHYminiCam8M",
            "camera-a",
            SdkHash,
            1,
            new Dictionary<string, int> { ["R"] = 16 }));
    }
}
