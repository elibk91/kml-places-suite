[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,

    [Parameter(Mandatory = $true)]
    [double]$Latitude,

    [Parameter(Mandatory = $true)]
    [double]$Longitude,

    [double]$RadiusMiles = 0.5,

    [int]$TopPerCategory = 5,

    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$workflowRoot = Split-Path -Parent $scriptDirectory
$repoRoot = Split-Path -Parent $workflowRoot
. (Join-Path $workflowRoot "helpers\Common.ps1")
$kmlConsoleProjectPath = Join-Path $repoRoot "generate\KmlGenerator.Console\KmlGenerator.Console.csproj"

Assert-PathExists -Path $RequestPath -FailureMessage "RequestPath does not exist."

if (-not $NoBuild) {
    # Keep the script thin and let the real C# diagnostic path do the category lookup work.
    Invoke-DotnetCommand -Description "Building KML generator project" -Arguments @("build", (Resolve-DisplayPath -Path $kmlConsoleProjectPath), "-v", "minimal") -FailureMessage "KML generator build failed."
}

Invoke-DotnetCommand -Description "Diagnosing coordinate coverage" -Arguments @(
    "run", "--project", (Resolve-DisplayPath -Path $kmlConsoleProjectPath), "--no-build", "--",
    "--input", (Resolve-DisplayPath -Path $RequestPath),
    "--diagnose-latitude", $Latitude.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--diagnose-longitude", $Longitude.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--diagnose-radius-miles", $RadiusMiles.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--diagnose-top-per-category", $TopPerCategory.ToString([System.Globalization.CultureInfo]::InvariantCulture)
) -FailureMessage "Coordinate coverage diagnostic failed."
