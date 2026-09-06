using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2GuidingAdjustmentScriptTests
{
    private static string ReadScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "scripts", "invoke-phd2-guiding-adjustment.ps1");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("PHD2 maintenance script not found.");
    }

    [Fact]
    public void MaintenanceRequiresExplicitAuthorityAndAnIdleMatchingOwner()
    {
        var source = ReadScript();
        Assert.Contains("-not $OperatorAuthorizedHardwareChange", source);
        Assert.Contains("runState -notin @('Idle', 'Cancelled', 'Completed')", source);
        Assert.Contains("$profile.id -ne $ExpectedProfileId", source);
        Assert.Contains("$profile.name -cne $ExpectedProfileName", source);
        Assert.Contains("'get_app_state') -notin @('Stopped', 'Selected')", source);
    }

    [Fact]
    public void BackupPrecedesNativeWritesAndReadbacksAreChecked()
    {
        var source = ReadScript();
        var backup = source.IndexOf("'before.json'", StringComparison.Ordinal);
        var parameterWrite = source.IndexOf("Invoke-PhdRpc 'set_algo_param'", StringComparison.Ordinal);
        var calibrationClear = source.IndexOf("Invoke-PhdRpc 'clear_calibration'", StringComparison.Ordinal);
        Assert.True(backup >= 0 && parameterWrite > backup && calibrationClear > backup);
        Assert.Contains("registryBackupSha256", source);
        Assert.Contains("PHD2 parameter readback did not match", source);
        Assert.Contains("if ($actual.calibrated)", source);
        Assert.Contains("'adjustments.json'", source);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("stop_capture")]
    [InlineData("capture_single_frame")]
    [InlineData("set_lock_position")]
    [InlineData("set_connected")]
    [InlineData("set_profile")]
    public void MaintenanceDoesNotReplaceTheProductionWorkflow(string method)
    {
        Assert.DoesNotContain($"Invoke-PhdRpc '{method}'", ReadScript());
    }
}
