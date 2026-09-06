using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NinaVerifiedHomeScriptTests
{
    private static string Script()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "scripts", "invoke-nina-verified-home.ps1");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("NINA verified-home script not found.");
    }

    [Fact]
    public void HomeRequiresExplicitConsentAndIdleMatchingOwners()
    {
        var source = Script();
        Assert.Contains("-not $OperatorAuthorizedHoming", source);
        Assert.Contains("runState -notin @('Idle','Cancelled','Completed')", source);
        Assert.Contains("$mount.DeviceId -ne $ExpectedTelescopeId", source);
        Assert.Contains("$camera.IsExposing", source);
        Assert.Contains("$phd -notin @('Stopped','Selected')", source);
        Assert.True(source.IndexOf("Save-Audit 'intent.json'", StringComparison.Ordinal) <
            source.IndexOf("/equipment/mount/home'", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfirmationRequiresRepeatedFreshStationaryHomeReadbacksWithoutLedgerChanges()
    {
        var source = Script();
        Assert.Contains("$mount.AtHome -and -not $mount.Slewing -and -not $mount.IsPulseGuiding", source);
        Assert.Contains("-not $mount.TrackingEnabled", source);
        Assert.Contains("$confirmations.Count -ge 2", source);
        Assert.Contains("oldLedgerModified = $false", source);
        Assert.Contains("$deadline", source);
        Assert.DoesNotContain("SerialPort", source);
        Assert.DoesNotContain("set_lock_position", source);
        Assert.DoesNotContain("/equipment/dome/", source);
        Assert.DoesNotContain("Remove-Item", source);
    }
}
