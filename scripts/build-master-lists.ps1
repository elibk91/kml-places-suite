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

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory

Write-Host "Building master list builder..."
dotnet build (Join-Path $repoRoot "MasterListBuilder.Console\MasterListBuilder.Console.csproj")
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for master list builder."
}

dotnet run --project (Join-Path $repoRoot "MasterListBuilder.Console\\MasterListBuilder.Console.csproj") --no-build -- --config $ConfigPath --output-dir $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Master list builder failed."
}
