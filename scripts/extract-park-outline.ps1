[CmdletBinding()]
param(
    [string[]]$ArcInputPaths,

    [Parameter(Mandatory = $true)]
    [string]$ParkName,

    [string]$RunId,

    [string]$RunOutputDirectory,

    [string]$OutputPath,

    [switch]$NoBuild,

    [switch]$KeepIntermediateArtifacts
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
. (Join-Path $scriptDirectory "Common.ps1")
$extractorProjectPath = Join-Path $repoRoot "ArcGeometryExtractor.Console\ArcGeometryExtractor.Console.csproj"
$arcSourceRoot = Join-Path $scriptDirectory "in\arc-sources"

if (-not $ArcInputPaths -or $ArcInputPaths.Count -eq 0) {
    # The diagnostic should default to the same checked-in ARC source set the active workflow uses.
    $ArcInputPaths = @(
        (Join-Path $arcSourceRoot "City_of_Atlanta_Parks.kml"),
        (Join-Path $arcSourceRoot "Official_Atlanta_Beltline_Trails.kml"),
        (Join-Path $arcSourceRoot "Park_Layer_-4860561661912367000.kmz"),
        (Join-Path $arcSourceRoot "Parks_Trails_assets.kml"),
        (Join-Path $arcSourceRoot "Trail_Plan_Inventory_-7693997307718543817.kmz"),
        (Join-Path $arcSourceRoot "Trails_-8511739637663957148.kmz")
    )
}

if (-not $RunId) {
    $RunId = Get-Date -Format "yyyyMMdd-HHmmss"
}

if (-not $RunOutputDirectory) {
    $RunOutputDirectory = Join-Path $scriptDirectory "out\runs\extract-park-outline\$RunId"
}

if (-not $OutputPath) {
    $safeParkName = ($ParkName -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safeParkName)) {
        $safeParkName = "park-outline"
    }

    $OutputPath = Join-Path $RunOutputDirectory "kml\$safeParkName.kml"
}

$intermediateRoot = if ($KeepIntermediateArtifacts) {
    $RunOutputDirectory
}
else {
    Join-Path ([System.IO.Path]::GetTempPath()) "kml-places-suite-extract-park-outline-$RunId"
}

$allPointsOutputPath = Join-Path $intermediateRoot "arc\arc-parks-trails-points.jsonl"
$parkPointsOutputPath = Join-Path $intermediateRoot "arc\arc-park-points.jsonl"

foreach ($inputPath in $ArcInputPaths) {
    Assert-PathExists -Path $inputPath -FailureMessage "ARC input '$inputPath' does not exist."
}

# Allow callers to skip the upfront build so multiple scripts can run in parallel without competing on shared
# project outputs. The app execution still stays on --no-build either way.
if (-not $NoBuild) {
    # Solution builds are unstable from inside these Windows PowerShell scripts, but direct project builds are reliable.
    # Build only the project this diagnostic runs, then execute it with --no-build.
    Invoke-DotnetCommand -Description "Building ARC extractor project" -Arguments @("build", (Resolve-DisplayPath -Path $extractorProjectPath), "-v", "minimal") -FailureMessage "ARC extractor build failed."
}

# This diagnostic must flow through the same extractor code path as the real workflow so park filtering happens
# after ARC parsing, metadata classification, and park-size filtering have already run. The final KML should show
# the exact point set the main workflow consumes, not a separate polygon-outline diagnostic artifact.
$arguments = @("run", "--project", (Resolve-DisplayPath -Path $extractorProjectPath), "--no-build", "--")
foreach ($inputPath in $ArcInputPaths) {
    $arguments += "--input"
    $arguments += (Resolve-DisplayPath -Path $inputPath)
}

$arguments += "--park-output"
$arguments += (Resolve-DisplayPath -Path $parkPointsOutputPath)
$arguments += "--output"
$arguments += (Resolve-DisplayPath -Path $allPointsOutputPath)

Ensure-ParentDirectory -Path $allPointsOutputPath
Ensure-ParentDirectory -Path $parkPointsOutputPath
try {
    Invoke-DotnetCommand -Description "Extracting park points through ArcGeometryExtractor" -Arguments $arguments -FailureMessage "ARC geometry extractor failed."
    Assert-PathExists -Path $parkPointsOutputPath -FailureMessage "Extractor did not produce the park point JSONL."

    $matchingPoints = @(Get-Content $parkPointsOutputPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object {
            $_.Name -like "*$ParkName*" -or $_.Query -like "*$ParkName*"
        })

    if ($matchingPoints.Count -eq 0) {
        throw "No park points matched '$ParkName' in extractor output."
    }

    $directory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $escapedParkName = [System.Security.SecurityElement]::Escape($ParkName)
    $kmlBuilder = [System.Text.StringBuilder]::new()
    [void]$kmlBuilder.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$kmlBuilder.AppendLine('<kml xmlns="http://www.opengis.net/kml/2.2">')
    [void]$kmlBuilder.AppendLine('  <Document>')
    [void]$kmlBuilder.AppendLine("    <name>$escapedParkName</name>")
    [void]$kmlBuilder.AppendLine('    <Style id="park-point"><IconStyle><scale>0.5</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png</href></Icon><color>ff00aa00</color></IconStyle></Style>')

    foreach ($point in $matchingPoints) {
        $label = [System.Security.SecurityElement]::Escape([string]$point.Name)
        [void]$kmlBuilder.AppendLine('    <Placemark>')
        [void]$kmlBuilder.AppendLine("      <name>$label</name>")
        [void]$kmlBuilder.AppendLine('      <styleUrl>#park-point</styleUrl>')
        [void]$kmlBuilder.AppendLine('      <Point>')
        [void]$kmlBuilder.AppendLine("        <coordinates>$($point.Longitude),$($point.Latitude),0</coordinates>")
        [void]$kmlBuilder.AppendLine('      </Point>')
        [void]$kmlBuilder.AppendLine('    </Placemark>')
    }

    [void]$kmlBuilder.AppendLine('  </Document>')
    [void]$kmlBuilder.AppendLine('</kml>')
    Set-Content -Path $OutputPath -Value $kmlBuilder.ToString()
    Write-Host "Saved $($matchingPoints.Count) point placemarks for '$ParkName' to $OutputPath"
}
finally {
    if (-not $KeepIntermediateArtifacts -and (Test-Path $intermediateRoot)) {
        Remove-Item -Path $intermediateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
