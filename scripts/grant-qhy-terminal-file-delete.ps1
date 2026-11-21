[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$List,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Digest,
    [Parameter(Mandatory)][ValidatePattern('^S-1-5-21-(\d+-){3}\d+$')][string]$Sid,
    [string]$SimulationRoot
)
# Windows PowerShell 5.1. An exact-list, one-time DACL repair; never deletes
# files, never grants inherited rights, never traverses a tree for candidates.
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.SecurityIdentifier]::new($Sid)
if ($Sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
    throw 'Recipient must be the same interactive user; do not switch accounts.'
}
$root = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'UVEX-ADV\qhy\data\runs'))
if ($SimulationRoot) {
    $root = [IO.Path]::GetFullPath($SimulationRoot)
    if ([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or
        [IO.Path]::GetFileName($root) -notmatch '^uvex-acl-sim-[0-9a-f]{32}$') { throw 'Invalid simulation sandbox.' }
}
$bytes = [IO.File]::ReadAllBytes($List)
$sha = [Security.Cryptography.SHA256]::Create()
function Get-Hex([byte[]]$value) { [BitConverter]::ToString($value).Replace('-','') }
if ((Get-Hex $sha.ComputeHash($bytes)) -ne $Digest) { throw 'Exact list digest mismatch.' }
$entries = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff))
if ($entries -isnot [array] -or !$entries.Count) { throw 'Expected a nonempty exact-file JSON array.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class UvexAclFileInfo {
    [StructLayout(LayoutKind.Sequential)] public struct Info {
        public uint attributes, creationLow, creationHigh, accessLow, accessHigh, writeLow, writeHigh,
            volume, sizeHigh, sizeLow, links, indexHigh, indexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
}
'@
$rights = [Security.AccessControl.FileSystemRights]'Read,ChangePermissions'
$rule = [Security.AccessControl.FileSystemAccessRule]::new($principal, 'Delete', 'None', 'None', 'Allow')
foreach ($e in $entries) {
    $p = [IO.Path]::GetFullPath([string]$e.path)
    if ($p -ne $e.path -or !$p.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $p.Substring($root.Length+1) -notmatch '^UVEX-\d{8}T\d{6}Z-[0-9a-f]+\\qhy-[0-9a-f]{32}\\raw\\\d+_ACQUISITION(-WIDE-STREAM)?_\d{8}T\d{6}\.\d+Z_[0-9a-f]{32}\.fits$' -or
        $e.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$e.size -le 0) { throw 'Unsafe path or incomplete identity.' }
    for ($part=$p; $part; $part=[IO.Path]::GetDirectoryName($part)) {
        if ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse path denied.' }
    }
    $f = [IO.FileStream]::new($p,[IO.FileMode]::Open,$rights,[IO.FileShare]::Read,65536,[IO.FileOptions]::SequentialScan)
    try {
        $info=New-Object 'UvexAclFileInfo+Info'
        if (![UvexAclFileInfo]::GetFileInformationByHandle($f.SafeFileHandle,[ref]$info) -or $info.links -ne 1 -or
            ($info.attributes -band 0x410) -or $f.Length -ne [long]$e.size -or
            (Get-Hex $sha.ComputeHash($f)) -ne $e.sha256) { throw 'Changed, linked or unverified file.' }
        $acl = $f.GetAccessControl()
        $acl.AddAccessRule($rule)
        $f.SetAccessControl($acl)
    } finally { $f.Dispose() }
}
$sha.Dispose()
Write-Output ("Granted file DELETE only: {0} exact files." -f $entries.Count)
