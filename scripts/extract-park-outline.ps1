[CmdletBinding()]
param(
    [string[]]$ArcInputPaths,

    [Parameter(Mandatory = $true)]
    [string]$ParkName,

    [string]$RunId,

    [string]$RunOutputDirectory,

    [string]$OutputPath
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

$allPointsOutputPath = Join-Path $RunOutputDirectory "arc\arc-parks-trails-points.jsonl"
$parkPointsOutputPath = Join-Path $RunOutputDirectory "arc\arc-park-points.jsonl"
$trailPointsOutputPath = Join-Path $RunOutputDirectory "arc\arc-trail-points.jsonl"
$featureOutputPath = Join-Path $RunOutputDirectory "arc\arc-features.jsonl"
$parkOutlineOutputPath = Join-Path $RunOutputDirectory "kml\all-park-outlines.kml"

foreach ($inputPath in $ArcInputPaths) {
    Assert-PathExists -Path $inputPath -FailureMessage "ARC input '$inputPath' does not exist."
}

# Solution builds are unstable from inside these Windows PowerShell scripts, but direct project builds are reliable.
# Build only the project this diagnostic runs, then execute it with --no-build.
Invoke-DotnetCommand -Description "Building ARC extractor project" -Arguments @("build", (Resolve-DisplayPath -Path $extractorProjectPath), "-v", "minimal") -FailureMessage "ARC extractor build failed."

# This diagnostic must flow through the same extractor code path as the real workflow so park filtering happens
# after ARC parsing, metadata classification, park-size filtering, and outline generation have already run.
$arguments = @("run", "--project", (Resolve-DisplayPath -Path $extractorProjectPath), "--no-build", "--")
foreach ($inputPath in $ArcInputPaths) {
    $arguments += "--input"
    $arguments += (Resolve-DisplayPath -Path $inputPath)
}

$arguments += "--output"
$arguments += (Resolve-DisplayPath -Path $allPointsOutputPath)
$arguments += "--park-output"
$arguments += (Resolve-DisplayPath -Path $parkPointsOutputPath)
$arguments += "--trail-output"
$arguments += (Resolve-DisplayPath -Path $trailPointsOutputPath)
$arguments += "--feature-output"
$arguments += (Resolve-DisplayPath -Path $featureOutputPath)
$arguments += "--park-outline-kml-output"
$arguments += (Resolve-DisplayPath -Path $parkOutlineOutputPath)

Ensure-ParentDirectory -Path $allPointsOutputPath
Ensure-ParentDirectory -Path $parkPointsOutputPath
Ensure-ParentDirectory -Path $trailPointsOutputPath
Ensure-ParentDirectory -Path $featureOutputPath
Ensure-ParentDirectory -Path $parkOutlineOutputPath
Invoke-DotnetCommand -Description "Extracting park outlines through ArcGeometryExtractor" -Arguments $arguments -FailureMessage "ARC geometry extractor failed."
Assert-PathExists -Path $parkOutlineOutputPath -FailureMessage "Extractor did not produce the park outline KML."

[xml]$xml = Get-Content $parkOutlineOutputPath
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$namespaceManager.AddNamespace("k", "http://www.opengis.net/kml/2.2")

$folders = @($xml.SelectNodes("//k:Folder", $namespaceManager))
foreach ($folder in $folders) {
    $nameNode = $folder.SelectSingleNode("./k:name", $namespaceManager)
    $name = if ($null -ne $nameNode) { $nameNode.InnerText } else { "" }
    if ($name -notlike "*$ParkName*") {
        [void]$folder.ParentNode.RemoveChild($folder)
    }
}

$remainingFolders = @($xml.SelectNodes("//k:Folder", $namespaceManager))
if ($remainingFolders.Count -eq 0) {
    throw "No park folders matched '$ParkName' in extractor output."
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$xml.Save($OutputPath)
$placemarkCount = @($xml.SelectNodes("//k:Placemark", $namespaceManager)).Count
Write-Host "Saved $placemarkCount placemarks for '$ParkName' to $OutputPath"
