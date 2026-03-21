[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $env:GoogleMaps__ApiKey) {
    throw "GoogleMaps__ApiKey is required."
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repoRoot "KmlSuite.slnx"

dotnet build $solutionPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed."
}

dotnet run --project (Join-Path $repoRoot "ResearchPointResolver.Console\\ResearchPointResolver.Console.csproj") --no-build -- --config $ConfigPath --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Research point resolver failed."
}
