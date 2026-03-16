[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter(Mandatory = $true)]
    [string]$PlacesOutputPath,

    [Parameter(Mandatory = $true)]
    [string]$RequestOutputPath,

    [string]$KmlOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $env:GoogleMaps__ApiKey) {
    throw "GoogleMaps__ApiKey is required."
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repoRoot "KmlSuite.slnx"

Write-Host "Building solution..."
dotnet build $solutionPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed."
}

Write-Host "Gathering Places points..."
dotnet run --project (Join-Path $repoRoot "PlacesGatherer.Console\\PlacesGatherer.Console.csproj") --no-build -- --config $ConfigPath --output $PlacesOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Places gatherer failed."
}

Write-Host "Assembling KML request..."
dotnet run --project (Join-Path $repoRoot "LocationAssembler.Console\\LocationAssembler.Console.csproj") --no-build -- --input $PlacesOutputPath --output $RequestOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Location assembler failed."
}

if ($KmlOutputPath) {
    Write-Host "Generating KML..."
    dotnet run --project (Join-Path $repoRoot "KmlGenerator.Console\\KmlGenerator.Console.csproj") --no-build -- --input $RequestOutputPath --output $KmlOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "KML generation failed."
    }
}

Write-Host "Workflow complete."
