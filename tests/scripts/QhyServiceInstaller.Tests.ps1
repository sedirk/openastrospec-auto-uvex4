$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\scripts\install-qhy-service.ps1') -Encoding UTF8 -Raw
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Installer parse failed.' }
# Load only the pure deployment functions: no administrator requirement, service,
# camera, SDK, installed directory or hardware is touched by these tests.
foreach ($name in @('Wait-QhyOwnerExit', 'Switch-QhyBinaryDirectory')) {
    $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}

Describe 'QHY service deployment exit and transactional file boundary' {
    BeforeEach {
        $script:rejectOldRename = $false
        $script:rejectPromotion = $false
        $script:caseRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $script:installed = Join-Path $caseRoot 'installed'
        $script:staged = Join-Path $caseRoot 'staged'
        $script:backup = Join-Path $caseRoot 'backup'
        New-Item -ItemType Directory -Path $installed, $staged -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $installed 'clrjit.dll') -Value 'old runtime'
        Set-Content -LiteralPath (Join-Path $staged 'clrjit.dll') -Value 'new runtime'
    }

    It 'waits for a real child process to exit naturally without terminating it' {
        $child = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile -Command Start-Sleep -Milliseconds 250' -PassThru
        try {
            Wait-QhyOwnerExit -Process $child -TimeoutMilliseconds 10000
            $child.HasExited | Should Be $true
            $child.ExitCode | Should Be 0
        }
        finally { $child.Dispose() }
    }

    It 'refuses a non-exited process and does not kill or replace its tree' {
        $owner = [pscustomobject]@{ WaitCalled = $false }
        $owner | Add-Member ScriptMethod WaitForExit {param($timeout) $this.WaitCalled = $true; return $false}
        { Wait-QhyOwnerExit -Process $owner -TimeoutMilliseconds 1 } | Should Throw
        $owner.WaitCalled | Should Be $true
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'old runtime'
    }

    It 'switches complete trees and preserves the previous runtime' {
        Switch-QhyBinaryDirectory $caseRoot $installed $staged $backup
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'new runtime'
        Get-Content -LiteralPath (Join-Path $backup 'clrjit.dll') | Should Be 'old runtime'
        Test-Path -LiteralPath $staged | Should Be $false
    }

    It 'does not delete an old tree when its rename is denied' {
        $script:rejectOldRename = $true
        Mock Move-Item { throw [UnauthorizedAccessException]::new('simulated runtime still in use') } -ParameterFilter { $script:rejectOldRename -and $LiteralPath -eq $script:installed }
        { Switch-QhyBinaryDirectory $caseRoot $installed $staged $backup } | Should Throw
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'old runtime'
        Get-Content -LiteralPath (Join-Path $staged 'clrjit.dll') | Should Be 'new runtime'
        Test-Path -LiteralPath $backup | Should Be $false
    }

    It 'restores the old tree if promotion fails after the backup rename' {
        $script:rejectPromotion = $true
        Mock Move-Item { throw 'simulated promotion failure' } -ParameterFilter { $script:rejectPromotion -and $LiteralPath -eq $script:staged }
        { Switch-QhyBinaryDirectory $caseRoot $installed $staged $backup } | Should Throw
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'old runtime'
        Get-Content -LiteralPath (Join-Path $staged 'clrjit.dll') | Should Be 'new runtime'
    }

    It 'supports an absent installation after an interrupted legacy deployment' {
        $absent = Join-Path $caseRoot 'absent'
        Switch-QhyBinaryDirectory $caseRoot $absent $staged $backup
        Get-Content -LiteralPath (Join-Path $absent 'clrjit.dll') | Should Be 'new runtime'
    }

    It 'rejects a destination outside the declared installation root' {
        { Switch-QhyBinaryDirectory $caseRoot (Join-Path $TestDrive 'outside') $staged $backup } | Should Throw
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'old runtime'
    }

    It 'rejects coincident paths before touching the existing runtime' {
        { Switch-QhyBinaryDirectory $caseRoot $installed $staged $installed } | Should Throw
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'old runtime'
    }

    It 'does not overwrite a previous deployment backup' {
        New-Item -ItemType Directory -Path $backup | Out-Null
        { Switch-QhyBinaryDirectory $caseRoot $installed $staged $backup } | Should Throw
        Get-Content -LiteralPath (Join-Path $installed 'clrjit.dll') | Should Be 'old runtime'
    }

    It 'checks the service process exit before switching and preserves registration' {
        $source.IndexOf('Wait-QhyOwnerExit -Process $owner') | Should BeLessThan $source.IndexOf('Switch-QhyBinaryDirectory -InstallRoot')
        $source.Contains("'delete',") | Should Be $false
        $source | Should Not Match 'Remove-Item -LiteralPath \$installDirectory'
        $source | Should Not Match 'Stop-Process|taskkill|\.Kill\('
        $source | Should Match "'config' \} else \{ 'create'"
    }
}
