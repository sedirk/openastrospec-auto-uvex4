using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CommissioningProfileCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uvex-commissioning-profile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoverUsesSavedOwnerConfigurationsWithoutConnectingEquipment()
    {
        var profiles = Path.Combine(root, "NINA", "Profiles");
        var programData = Path.Combine(root, "UVEX-ADV");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(Path.Combine(programData, "qhy"));
        Directory.CreateDirectory(Path.Combine(programData, "commissioning"));
        Directory.CreateDirectory(Path.Combine(programData, "commissioning", "station-profiles"));

        File.WriteAllText(Path.Combine(profiles, "saved.profile"), """
            <Profile>
              <Name>光谱观测</Name>
              <CameraSettings><Id>QHYminiCam8M-qhy123</Id><LastDeviceName>QHYminiCam8M</LastDeviceName></CameraSettings>
              <TelescopeSettings><Id>ASCOM.OnStep.Telescope</Id><LastDeviceName>On-Step</LastDeviceName></TelescopeSettings>
              <PluginSettings><pluginStorage>
                <KeyValueOfguidArrayOfKeyValueOfstringanyTypeox8ieOcg>
                  <Key>a4183531-55bd-4fd0-b04a-97ed7edc15da</Key>
                  <Value><KeyValueOfstringanyType><Key>BoundCameraId</Key><Value>ToupTek_ATR_STABLE</Value></KeyValueOfstringanyType></Value>
                </KeyValueOfguidArrayOfKeyValueOfstringanyTypeox8ieOcg>
              </pluginStorage></PluginSettings>
            </Profile>
            """);
        File.WriteAllText(Path.Combine(programData, "qhy", "appsettings.json"), """
            { "Qhy": { "ExpectedStableId": "QHYminiCam8M-qhy123" } }
            """);
        File.WriteAllText(Path.Combine(programData, "commissioning", "phd2-profile-2.json"), """
            {
              "ProfileId": 2,
              "ProfileName": "c11+slit+2210",
              "CameraName": "ToupTek Camera",
              "CameraStableIds": ["USB-G3-STABLE"],
              "MountName": "OnStep Telescope (ASCOM)",
              "Sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            }
            """);
        var operationalProfile = Path.Combine(programData, "commissioning", "station-profiles", "default.station-profile.json");
        File.WriteAllText(operationalProfile, """
            {
              "ProfileKind": "OperationalTemplate",
              "SchemaVersion": 1,
              "DisplayName": "默认台站配置（测试台）",
              "Description": "只读测试台运行模板。",
              "NinaProfileValues": { "G3ExposureMilliseconds": 10000 }
            }
            """);

        var result = CommissioningProfileCatalog.Discover(profiles, programData);

        var automatic = Assert.Single(result.Profiles, item => item.Id == CommissioningProfileChoice.AutomaticSiteProfileId);
        Assert.Equal("默认台站配置（测试台）", automatic.DisplayName);
        Assert.Equal(Path.GetFullPath(operationalProfile), automatic.BindingsPath);
        Assert.Contains(result.Telescopes, item => item.Id == "ASCOM.OnStep.Telescope");
        Assert.Contains(result.AtrCameras, item => item.Id == "ToupTek_ATR_STABLE");
        Assert.Single(result.QhyCameras, item => item.Id == "QHYminiCam8M-qhy123");
        var g3 = Assert.Single(result.G3Cameras);
        Assert.Equal("USB-G3-STABLE", g3.Id);
        Assert.Equal(2, g3.Phd2ProfileId);
        Assert.Equal("c11+slit+2210", g3.Phd2ProfileName);
        Assert.Equal("ToupTek Camera", g3.CameraName);
    }

    [Fact]
    public void CompleteBindingsBecomeSelectableProfilesButPhd2OnlyBindingsDoNot()
    {
        var profiles = Path.Combine(root, "NINA", "Profiles");
        var commissioning = Path.Combine(root, "UVEX-ADV", "commissioning");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(commissioning);
        var full = Path.Combine(commissioning, "site.bindings.json");
        File.WriteAllText(full, """
            {
              "NinaProfileValues": {
                "CommissioningPresetPath": "preset.json",
                "CommissioningPresetId": "DF-UVEX4-2026",
                "CommissioningPresetSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "NightSetupSnapshotPath": "night-setup.json",
                "Phd2ProfileEvidenceSha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                "ExpectedTelescopeId": "ASCOM.OnStep.Telescope"
              }
            }
            """);
        File.WriteAllText(Path.Combine(commissioning, "phd2.bindings.json"), """
            { "EvidencePath": "phd2.json", "Phd2ProfileEvidenceSha256": "ABC" }
            """);

        var result = CommissioningProfileCatalog.Discover(profiles, Path.Combine(root, "UVEX-ADV"));

        Assert.Equal(2, result.Profiles.Count);
        var selectable = Assert.Single(result.Profiles, item => !item.IsAutomatic);
        Assert.Contains("安装标定", selectable.DisplayName, StringComparison.Ordinal);
        Assert.Contains("DF-UVEX4-2026", selectable.Description, StringComparison.Ordinal);
        var values = CommissioningProfileCatalog.ReadProfileValues(full);
        Assert.Equal("ASCOM.OnStep.Telescope", values["ExpectedTelescopeId"].GetString());
    }

    [Fact]
    public void PlaceholderQhyIdentityIsNotOffered()
    {
        var profiles = Path.Combine(root, "NINA", "Profiles");
        var programData = Path.Combine(root, "UVEX-ADV");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(Path.Combine(programData, "qhy"));
        File.WriteAllText(Path.Combine(programData, "qhy", "appsettings.json"), """
            { "Qhy": { "ExpectedStableId": "QHYminiCam8M-<commissioned-stable-id>" } }
            """);

        var result = CommissioningProfileCatalog.Discover(profiles, programData);

        Assert.Empty(result.QhyCameras);
    }

    [Fact]
    public void InvalidOperationalTemplateFallsBackToGenericAutomaticProfile()
    {
        var profiles = Path.Combine(root, "NINA", "Profiles");
        var programData = Path.Combine(root, "UVEX-ADV");
        var stationProfiles = Path.Combine(programData, "commissioning", "station-profiles");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(stationProfiles);
        File.WriteAllText(Path.Combine(stationProfiles, "default.station-profile.json"), """
            { "ProfileKind": "FullCommissioning", "SchemaVersion": 1, "NinaProfileValues": {} }
            """);

        var result = CommissioningProfileCatalog.Discover(profiles, programData);

        var automatic = Assert.Single(result.Profiles);
        Assert.Equal("默认台站配置（自动发现）", automatic.DisplayName);
        Assert.Null(automatic.BindingsPath);
    }

    [Fact]
    public void StartupUsesNewestCompletePackageWhileAutomaticDiscoveryIsStillRemembered()
    {
        var olderPath = Path.Combine(root, "older.bindings.json");
        var newerPath = Path.Combine(root, "newer.bindings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(olderPath, "{}");
        File.WriteAllText(newerPath, "{}");
        File.SetLastWriteTimeUtc(olderPath, new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerPath, new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));
        CommissioningProfileChoice[] profiles =
        [
            new(CommissioningProfileChoice.AutomaticSiteProfileId, "自动", "自动发现", null, true),
            new(Path.GetFullPath(olderPath), "旧方案", "旧", Path.GetFullPath(olderPath), false),
            new(Path.GetFullPath(newerPath), "新方案", "新", Path.GetFullPath(newerPath), false),
        ];

        var selected = CommissioningProfileCatalog.SelectStartupProfile(
            profiles,
            CommissioningProfileChoice.AutomaticSiteProfileId);

        Assert.NotNull(selected);
        Assert.Equal(Path.GetFullPath(newerPath), selected!.Id);
    }

    [Fact]
    public void StartupPreservesAnExplicitlyRememberedCompletePackage()
    {
        var rememberedPath = Path.Combine(root, "remembered.bindings.json");
        var newerPath = Path.Combine(root, "newer.bindings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(rememberedPath, "{}");
        File.WriteAllText(newerPath, "{}");
        File.SetLastWriteTimeUtc(rememberedPath, new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerPath, new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));
        CommissioningProfileChoice[] profiles =
        [
            new(CommissioningProfileChoice.AutomaticSiteProfileId, "自动", "自动发现", null, true),
            new(Path.GetFullPath(rememberedPath), "已选", "已选", Path.GetFullPath(rememberedPath), false),
            new(Path.GetFullPath(newerPath), "新方案", "新", Path.GetFullPath(newerPath), false),
        ];

        var selected = CommissioningProfileCatalog.SelectStartupProfile(
            profiles,
            Path.GetFullPath(rememberedPath));

        Assert.NotNull(selected);
        Assert.Equal(Path.GetFullPath(rememberedPath), selected!.Id);
    }

    [Fact]
    public void StartupCanPreferNewestCompletePackageOverRememberedSelection()
    {
        var rememberedPath = Path.Combine(root, "remembered.bindings.json");
        var newerPath = Path.Combine(root, "newer.bindings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(rememberedPath, "{}");
        File.WriteAllText(newerPath, "{}");
        File.SetLastWriteTimeUtc(rememberedPath, new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerPath, new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));
        CommissioningProfileChoice[] profiles =
        [
            new(CommissioningProfileChoice.AutomaticSiteProfileId, "自动", "自动发现", null, true),
            new(Path.GetFullPath(rememberedPath), "已选", "已选", Path.GetFullPath(rememberedPath), false),
            new(Path.GetFullPath(newerPath), "新方案", "新", Path.GetFullPath(newerPath), false),
        ];

        var selected = CommissioningProfileCatalog.SelectStartupProfile(
            profiles,
            Path.GetFullPath(rememberedPath),
            preferNewestComplete: true);

        Assert.NotNull(selected);
        Assert.Equal(Path.GetFullPath(newerPath), selected!.Id);
    }

    [Fact]
    public void OperationalTemplateAllowsMeasuredOpticalAndBrightTargetValuesButNotAuthorityFlags()
    {
        string[] allowed =
        [
            nameof(UvexPluginSettings.BrightTargetMinimumG3ExposureMilliseconds),
            nameof(UvexPluginSettings.BrightTargetMaximumQhyWcsAgeMinutes),
            nameof(UvexPluginSettings.BrightTargetMaximumG3FrameAgeMinutes),
            nameof(UvexPluginSettings.BrightTargetMaximumQhyResidualArcseconds),
            nameof(UvexPluginSettings.BrightTargetMaximumCatalogMismatchArcseconds),
            nameof(UvexPluginSettings.G3FocalLengthMillimeters),
            nameof(UvexPluginSettings.G3PixelSizeMicrometers),
            nameof(UvexPluginSettings.QhyFocalLengthMillimeters),
            nameof(UvexPluginSettings.QhyPixelSizeMicrometers),
            nameof(UvexPluginSettings.QhyG3FastPairEnabled),
        ];

        Assert.All(allowed, name => Assert.True(CommissioningProfileCatalog.IsOperationalProfileSetting(name), name));
        Assert.True(CommissioningProfileCatalog.IsPostBindingsOperationalPreference(nameof(UvexPluginSettings.QhyG3FastPairEnabled)));
        Assert.False(CommissioningProfileCatalog.IsPostBindingsOperationalPreference(nameof(UvexPluginSettings.G3FocalLengthMillimeters)));
        Assert.False(CommissioningProfileCatalog.IsOperationalProfileSetting(nameof(UvexPluginSettings.RealModeCommissioned)));
        Assert.False(CommissioningProfileCatalog.IsOperationalProfileSetting(nameof(UvexPluginSettings.BrightTargetWingCentroidEnabled)));
    }

    [Fact]
    public void LocalInstallationPackagesRemainDiscoverableWithoutARememberedPath()
    {
        var local = Path.Combine(root, "Local");
        var directory = Path.Combine(local, "commissioning", "usb-rebind");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "current.bindings.json");
        File.WriteAllText(path, """
            {"NinaProfileValues":{"CommissioningPresetPath":"preset.json","CommissioningPresetId":"REBOUND",
            "CommissioningPresetSha256":"hash","NightSetupSnapshotPath":"night.json","Phd2ProfileEvidenceSha256":"hash",
            "ExpectedUvexSlitPosition":2,"G3ExposureMilliseconds":10000}}
            """);
        var result = CommissioningProfileCatalog.Discover(root, Path.Combine(root, "ProgramData"), localDataRoot: local);
        var package = Assert.Single(result.Profiles, item => !item.IsAutomatic);
        Assert.Equal(Path.GetFullPath(path), package.BindingsPath);
        Assert.Contains("狭缝 2", package.DisplayName);
        Assert.Contains("10000 ms", package.DisplayName);
    }

    [Fact]
    public void ActiveProfileTakesPrecedenceOverDuplicateHistoricalIdentity()
    {
        Directory.CreateDirectory(root);
        foreach (var id in new[] { "a-history", "z-active" })
            File.WriteAllText(Path.Combine(root, id + ".profile"), $"""
                <Profile><Name>{id}</Name><TelescopeSettings><Id>mount</Id><LastDeviceName>On-Step</LastDeviceName></TelescopeSettings></Profile>
                """);
        var result = CommissioningProfileCatalog.Discover(root, root, activeNinaProfileId: "z-active");
        Assert.Contains("z-active", Assert.Single(result.Telescopes).Source);
    }

    [Fact]
    public void HiddenHistoryNeverHidesTheCurrentSelectionAndIsExplicitlyRecoverable()
    {
        CommissioningProfileChoice[] profiles =
        [
            new("auto", "auto", "", null, true),
            new("current", "current", "", "current.json", false),
            new("historic", "historic", "", "historic.json", false),
        ];
        Assert.Contains(profiles[1], CommissioningProfileCatalog.VisibleProfiles(profiles, "current", false));
        Assert.Equal(profiles, CommissioningProfileCatalog.VisibleProfiles(profiles, "current", true));
    }

    [Fact]
    public void CandidateDiscoveryCannotReplaceExistingOrAmbiguousIdentities()
    {
        DeviceIdentityChoice[] candidates = [new("a", "a", ""), new("b", "b", "")];
        Assert.Equal(candidates[1], CommissioningProfileCatalog.SavedOrUniqueCandidate(candidates, "b"));
        Assert.Null(CommissioningProfileCatalog.SavedOrUniqueCandidate(candidates, "missing"));
        Assert.Null(CommissioningProfileCatalog.SavedOrUniqueCandidate(candidates, ""));
        Assert.Equal(candidates[0], CommissioningProfileCatalog.SavedOrUniqueCandidate([candidates[0]], ""));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
