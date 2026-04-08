[CmdletBinding()]
param(
    [string]$City = "atlanta",

    [string[]]$ArcInputPaths,

    [Parameter(Mandatory = $true)]
    [string]$ParkName,

    [string]$CategoryConfigPath,

    [string]$RunId,

    [string]$RunOutputDirectory,

    [string]$OutputPath,

    [string]$OriginalGeometryOutputPath,

    [switch]$NoBuild,

    [switch]$KeepIntermediateArtifacts
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$workflowRoot = Split-Path -Parent $scriptDirectory
$repoRoot = Split-Path -Parent $workflowRoot
. (Join-Path $workflowRoot "helpers\Common.ps1")
$cityKey = $City.Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($CategoryConfigPath)) {
    $CategoryConfigPath = ".\data\config\$cityKey\authority\category-config.with-gyms.json"
}
$extractorProjectPath = Join-Path $repoRoot "ingest\authority\ArcGeometryExtractor.Console\ArcGeometryExtractor.Console.csproj"
$arcSourceRoot = Join-Path $repoRoot "data\inputs\$cityKey\arc-sources"
$arcParksTrailsRoot = Join-Path $arcSourceRoot "parks-trails"
$categoryConfig = Get-Content -Path $CategoryConfigPath | ConvertFrom-Json

function Convert-LatLonToFeet {
    param(
        [Parameter(Mandatory = $true)]
        [double]$Latitude,

        [Parameter(Mandatory = $true)]
        [double]$Longitude
    )

    $referenceLatitudeDegrees = 33.75
    $feetPerDegreeLatitude = 364000.0
    $feetPerDegreeLongitude = $feetPerDegreeLatitude * [Math]::Cos($referenceLatitudeDegrees * [Math]::PI / 180.0)

    [pscustomobject]@{
        X = $Longitude * $feetPerDegreeLongitude
        Y = $Latitude * $feetPerDegreeLatitude
    }
}

function Select-DiagnosticPoints {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Points,

        [Parameter(Mandatory = $true)]
        [double]$SpacingMiles
    )

    if ($SpacingMiles -le 0) {
        return $Points
    }

    $spacingFeet = $SpacingMiles * 5280.0
    $seenCells = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $selected = [System.Collections.Generic.List[object]]::new()

    foreach ($point in $Points) {
        $projected = Convert-LatLonToFeet -Latitude ([double]$point.Latitude) -Longitude ([double]$point.Longitude)
        $cellX = [int][Math]::Floor($projected.X / $spacingFeet)
        $cellY = [int][Math]::Floor($projected.Y / $spacingFeet)
        $cellKey = "$cellX|$cellY"
        if ($seenCells.Add($cellKey)) {
            [void]$selected.Add($point)
        }
    }

    return $selected
}

if (-not $ArcInputPaths -or $ArcInputPaths.Count -eq 0) {
    # The diagnostic should default to the same checked-in ARC source set the active workflow uses.
    $ArcInputPaths = @(Get-ChildItem -Path $arcParksTrailsRoot -File -Recurse |
        Where-Object {
            $_.Extension -in '.kml', '.kmz' `
                -and $_.Name -notmatch '^park-outlines\.'
        } |
        Sort-Object Name |
        ForEach-Object { $_.FullName })
}

if (-not $RunId) {
    $RunId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"
}

$safeParkName = ($ParkName -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($safeParkName)) {
    $safeParkName = "park-outline"
}

if (-not $RunOutputDirectory) {
    $RunOutputDirectory = Join-Path (Join-Path $workflowRoot "out\runs") $cityKey
}

if (-not $RunOutputDirectory.EndsWith($RunId, [System.StringComparison]::OrdinalIgnoreCase)) {
    $RunOutputDirectory = Join-Path $RunOutputDirectory "extract-park-outline-$safeParkName-$RunId"
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $RunOutputDirectory "$safeParkName.kml"

    if (-not $OriginalGeometryOutputPath) {
        $OriginalGeometryOutputPath = Join-Path $RunOutputDirectory "$safeParkName-original-geometry.kml"
    }
}

$intermediateRoot = if ($KeepIntermediateArtifacts) {
    $RunOutputDirectory
}
else {
    Join-Path ([System.IO.Path]::GetTempPath()) "kml-places-suite-extract-park-outline-$RunId"
}

$allPointsOutputPath = Join-Path $intermediateRoot "extract-park-outline-$RunId-arc-parks-trails-points.jsonl"
$parkPointsOutputPath = Join-Path $intermediateRoot "extract-park-outline-$RunId-arc-park-points.jsonl"
$featureOutputPath = Join-Path $intermediateRoot "extract-park-outline-$RunId-arc-features.jsonl"
$intermediateOriginalGeometryOutputPath = Join-Path $intermediateRoot "extract-park-outline-$RunId-arc-original-geometry.kml"

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
$arguments += "--minimum-park-square-feet"
$arguments += ([string]$categoryConfig.minimumParkSquareFeet)
$arguments += "--minimum-trail-miles"
$arguments += ([string]$categoryConfig.minimumTrailMiles)
$arguments += "--minimum-combined-park-trail-miles"
$arguments += ([string]$categoryConfig.minimumCombinedParkTrailMiles)
$arguments += "--point-spacing-miles"
$arguments += ([string]$categoryConfig.pointSpacingMiles)
$arguments += "--feature-output"
$arguments += (Resolve-DisplayPath -Path $featureOutputPath)
$arguments += "--original-geometry-kml-output"
$arguments += (Resolve-DisplayPath -Path $intermediateOriginalGeometryOutputPath)

Ensure-ParentDirectory -Path $allPointsOutputPath
Ensure-ParentDirectory -Path $parkPointsOutputPath
Ensure-ParentDirectory -Path $featureOutputPath
Ensure-ParentDirectory -Path $intermediateOriginalGeometryOutputPath
try {
    Invoke-DotnetCommand -Description "Extracting park points through ArcGeometryExtractor" -Arguments $arguments -FailureMessage "ARC geometry extractor failed."
    Assert-PathExists -Path $parkPointsOutputPath -FailureMessage "Extractor did not produce the park point JSONL."
    Assert-PathExists -Path $featureOutputPath -FailureMessage "Extractor did not produce the feature JSONL."
    Assert-PathExists -Path $intermediateOriginalGeometryOutputPath -FailureMessage "Extractor did not produce the original geometry KML."

    $matchingPoints = @(Get-Content $parkPointsOutputPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object {
            $_.Name -like "*$ParkName*" -or
            $_.Query -like "*$ParkName*" -or
            ($_.SearchNames | Where-Object { $_ -like "*$ParkName*" } | Select-Object -First 1)
        })

    if ($matchingPoints.Count -eq 0) {
        throw "No park points matched '$ParkName' in extractor output."
    }

    $diagnosticPointSpacingMiles = if ($categoryConfig.PSObject.Properties.Name -contains "diagnosticPointSpacingMiles") {
        [double]$categoryConfig.diagnosticPointSpacingMiles
    }
    else {
        [double]$categoryConfig.pointSpacingMiles
    }
    $displayPoints = @(Select-DiagnosticPoints -Points $matchingPoints -SpacingMiles $diagnosticPointSpacingMiles)

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

    foreach ($point in $displayPoints) {
        [void]$kmlBuilder.AppendLine('    <Placemark>')
        [void]$kmlBuilder.AppendLine('      <styleUrl>#park-point</styleUrl>')
        [void]$kmlBuilder.AppendLine('      <Point>')
        [void]$kmlBuilder.AppendLine("        <coordinates>$($point.Longitude),$($point.Latitude),0</coordinates>")
        [void]$kmlBuilder.AppendLine('      </Point>')
        [void]$kmlBuilder.AppendLine('    </Placemark>')
    }

    [void]$kmlBuilder.AppendLine('  </Document>')
    [void]$kmlBuilder.AppendLine('</kml>')
    Set-Content -Path $OutputPath -Value $kmlBuilder.ToString()
    Write-Host "Saved $($displayPoints.Count) diagnostic point placemarks for '$ParkName' to $OutputPath (from $($matchingPoints.Count) matched runtime points)"

    if ([string]::IsNullOrWhiteSpace($OriginalGeometryOutputPath)) {
        $OriginalGeometryOutputPath = Join-Path $RunOutputDirectory "$safeParkName-original-geometry.kml"
    }

    $originalGeometryDocument = [xml](Get-Content -Path $intermediateOriginalGeometryOutputPath -Raw)
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($originalGeometryDocument.NameTable)
    $namespaceManager.AddNamespace("kml", "http://www.opengis.net/kml/2.2")
    $placemarks = @($originalGeometryDocument.SelectNodes("//kml:Placemark", $namespaceManager))
    foreach ($placemark in $placemarks) {
        $nameNode = $placemark.SelectSingleNode("./kml:name", $namespaceManager)
        $placemarkName = if ($nameNode) { [string]$nameNode.InnerText } else { "" }
        if ($placemarkName -notlike "*$ParkName*") {
            [void]$placemark.ParentNode.RemoveChild($placemark)
        }
    }

    $emptyFolders = @($originalGeometryDocument.SelectNodes("//kml:Folder[not(kml:Placemark)]", $namespaceManager))
    foreach ($folder in $emptyFolders) {
        [void]$folder.ParentNode.RemoveChild($folder)
    }

    $originalGeometryDirectory = Split-Path -Parent $OriginalGeometryOutputPath
    if (-not [string]::IsNullOrWhiteSpace($originalGeometryDirectory)) {
        New-Item -ItemType Directory -Path $originalGeometryDirectory -Force | Out-Null
    }

    $originalGeometryDocument.Save($OriginalGeometryOutputPath)
    Write-Host "Saved original geometry KML for '$ParkName' to $OriginalGeometryOutputPath"
}
finally {
    if (-not $KeepIntermediateArtifacts -and (Test-Path $intermediateRoot)) {
        Remove-Item -Path $intermediateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
