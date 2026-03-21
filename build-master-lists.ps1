[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
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

dotnet run --project (Join-Path $repoRoot "MasterListBuilder.Console\\MasterListBuilder.Console.csproj") --no-build -- --config $ConfigPath --output-dir $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Master list builder failed."
}
