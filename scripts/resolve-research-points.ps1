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

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory

Write-Host "Building research point resolver..."
dotnet build (Join-Path $repoRoot "ResearchPointResolver.Console\ResearchPointResolver.Console.csproj")
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for research point resolver."
}

dotnet run --project (Join-Path $repoRoot "ResearchPointResolver.Console\\ResearchPointResolver.Console.csproj") --no-build -- --config $ConfigPath --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Research point resolver failed."
}
