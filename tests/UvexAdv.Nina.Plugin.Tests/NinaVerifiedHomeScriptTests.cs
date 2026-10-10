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
    public void FreshIdleSessionMayOmitAnObservationRunId()
    {
        var source = Script();
        Assert.Contains("previousObservationRunId = if ($null -ne $snapshot.PSObject.Properties['observationRunId'])", source);
        Assert.Contains("{ $snapshot.observationRunId } else { $null }", source);
    }

    [Fact]
    public void ConfirmationRequiresRepeatedFreshStationaryHomeReadbacksWithoutLedgerChanges()
    {
        var source = Script();
        Assert.Contains("$mount.AtHome -and -not $mount.Slewing -and -not $mount.IsPulseGuiding", source);
        Assert.Contains("-not $mount.TrackingEnabled", source);
        Assert.Contains("$confirmations.Count -ge 2", source);
        Assert.Contains("oldLedgerModified = $false", source);
        Assert.Contains("[Diagnostics.Stopwatch]::StartNew()", source);
        Assert.Contains("$elapsed.Elapsed.TotalSeconds -lt $TimeoutSeconds", source);
        Assert.Contains("/equipment/mount/slew/stop", source);
        Assert.DoesNotContain("SerialPort", source);
        Assert.DoesNotContain("set_lock_position", source);
        Assert.DoesNotContain("/equipment/dome/", source);
        Assert.DoesNotContain("Remove-Item", source);
    }

    [Fact]
    public void FaultedTargetNeedsMatchingTerminalIdleNightBeforeExplicitMaintenance()
    {
        var source = Script();
        Assert.Contains("$snapshot.runState -eq 'Faulted' -and $TerminalNightJournal", source);
        Assert.Contains("$night.phase -ne 'Terminal'", source);
        Assert.Contains("$_.runId -eq $snapshot.observationRunId", source);
        Assert.Contains("$matching[0].idleErrors.Count -ne 0", source);
        Assert.Contains("$matching[0].settledErrors.Count -ne 0", source);
        Assert.Contains("/sequence/state", source);
    }
}
