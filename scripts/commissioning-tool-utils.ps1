$ErrorActionPreference = 'Stop'

function Invoke-UvexCommissioningTool {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments)

    $root = Split-Path $PSScriptRoot -Parent
    $publishedExe = Join-Path $root 'artifacts\commissioning-tool\UvexAdv.Commissioning.Tool.exe'
    if (Test-Path -LiteralPath $publishedExe) {
        & $publishedExe @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Commissioning evidence tool exited with code $LASTEXITCODE." }
        return
    }

    $dotnet = Join-Path $root '.dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $project = Join-Path $root 'src\UvexAdv.Commissioning.Tool\UvexAdv.Commissioning.Tool.csproj'
    & $dotnet run --project $project --configuration Release -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Commissioning evidence tool exited with code $LASTEXITCODE." }
}
