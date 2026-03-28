[CmdletBinding()]
param(
    [string]$MasterListOutputDirectory = ".\scripts\in\master-lists",

    [string]$CategoryConfigPath = ".\config\authority\category-config.json",

    [string]$RunId,

    [string]$RunOutputDirectory,

    [string[]]$ArcInputPaths,

    [string]$ArcOutputPath,

    [string]$ArcParkOutputPath,

    [string]$ArcTrailOutputPath,

    [string]$ArcFeatureOutputPath,

    [string]$ArcOriginalGeometryKmlOutputPath,

    [string[]]$ArcMartaInputPaths,

    [string]$ArcMartaOutputPath,

    [string]$FinalRequestOutputPath,

    [string]$KmlOutputPath,

    [string]$TileOutputDirectory,

    [switch]$NoBuild,

    [switch]$KeepIntermediateArtifacts,

    [double]$TileNorth = 33.952876,

    [double]$TileSouth = 33.698669,

    [double]$TileWest = -84.54903,

    [double]$TileEast = -84.095141,

    [double]$TileLatitudeStep = 0.07,

    [double]$TileLongitudeStep = 0.09
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
. (Join-Path $scriptDirectory "Common.ps1")
$arcSourceRoot = Join-Path $scriptDirectory "in\arc-sources"
$arcParksTrailsRoot = Join-Path $arcSourceRoot "parks-trails"
$arcMartaRoot = Join-Path $arcSourceRoot "marta"
$categoryConfig = Get-Content -Path $CategoryConfigPath | ConvertFrom-Json

if (-not $ArcInputPaths -or $ArcInputPaths.Count -eq 0) {
    # Keep durable source KML/KMZ in-repo so active workflows do not depend on developer Downloads folders.
    $ArcInputPaths = @(Get-ChildItem -Path $arcParksTrailsRoot -File -Recurse |
        Where-Object {
            $_.Extension -in '.kml', '.kmz' `
                -and $_.Name -notmatch '^park-outlines\.'
        } |
        Sort-Object Name |
        ForEach-Object { $_.FullName })
}

if (-not $ArcMartaInputPaths -or $ArcMartaInputPaths.Count -eq 0) {
    $ArcMartaInputPaths = @(
        (Join-Path $arcMartaRoot "atlanta_regional_commission_marta_rail_stations.kmz")
    )
}

if (-not $RunId) {
    $RunId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"
}

if (-not $RunOutputDirectory) {
    $RunOutputDirectory = Join-Path $scriptDirectory "out\runs"
}

if (-not $RunOutputDirectory.EndsWith($RunId, [System.StringComparison]::OrdinalIgnoreCase)) {
    $RunOutputDirectory = Join-Path $RunOutputDirectory "category-workflow-$RunId"
}

if ($KeepIntermediateArtifacts) {
    $intermediateRoot = $RunOutputDirectory
}
else {
    $intermediateRoot = Join-Path ([System.IO.Path]::GetTempPath()) "kml-places-suite-category-workflow-$RunId"
}

if (-not $ArcOutputPath) {
    $ArcOutputPath = Join-Path $intermediateRoot "category-workflow-$RunId-arc-parks-trails-points.jsonl"
}

if (-not $ArcParkOutputPath) {
    $ArcParkOutputPath = Join-Path $intermediateRoot "category-workflow-$RunId-arc-park-points.jsonl"
}

if (-not $ArcTrailOutputPath) {
    $ArcTrailOutputPath = Join-Path $intermediateRoot "category-workflow-$RunId-arc-trail-points.jsonl"
}

if (-not $ArcFeatureOutputPath) {
    $ArcFeatureOutputPath = Join-Path $intermediateRoot "category-workflow-$RunId-arc-features.jsonl"
}

if (-not $ArcOriginalGeometryKmlOutputPath) {
    $ArcOriginalGeometryKmlOutputPath = Join-Path $RunOutputDirectory "arc-original-geometry.kml"
}

if (-not $ArcMartaOutputPath) {
    $ArcMartaOutputPath = Join-Path $intermediateRoot "category-workflow-$RunId-marta-stations.arc.jsonl"
}

if (-not $FinalRequestOutputPath) {
    $FinalRequestOutputPath = Join-Path $RunOutputDirectory "atlanta-category-request.arc.json"
}

if (-not $KmlOutputPath) {
    $KmlOutputPath = Join-Path $RunOutputDirectory "atlanta-category-outline.arc.kml"
}

if (-not $TileOutputDirectory) {
    $TileOutputDirectory = Join-Path $RunOutputDirectory "tiles"
}

function Assert-WorkflowPrerequisites {
    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name

    Assert-PathExists -Path $MasterListOutputDirectory -FailureMessage "MasterListOutputDirectory does not exist."
    Assert-PathExists -Path $CategoryConfigPath -FailureMessage "CategoryConfigPath does not exist."
}

function Assert-RequiredMasterLists {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$RequiredFiles
    )

    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name -Arguments @{
        Count = $RequiredFiles.Count
    }

    foreach ($fileName in $RequiredFiles) {
        $path = Join-Path $MasterListOutputDirectory $fileName
        Assert-PathExists -Path $path -FailureMessage "Expected master list '$fileName' was not produced."
    }
}

function Invoke-ArcMartaExtraction {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name -Arguments @{
        InputCount = if ($ArcMartaInputPaths) { $ArcMartaInputPaths.Count } else { 0 }
        OutputPath = (Resolve-DisplayPath -Path $ArcMartaOutputPath)
    }

    if (-not $ArcMartaInputPaths -or $ArcMartaInputPaths.Count -eq 0) {
        throw "ArcMartaInputPaths are required for MARTA data."
    }

    if (-not $ArcMartaOutputPath) {
        throw "ArcMartaOutputPath is required when ArcMartaInputPaths are provided."
    }

    foreach ($inputPath in $ArcMartaInputPaths) {
        Assert-PathExists -Path $inputPath -FailureMessage "ARC MARTA input '$inputPath' does not exist."
    }

    $arguments = @("run", "--project", (Resolve-DisplayPath -Path $ProjectPath), "--no-build", "--")
    foreach ($inputPath in $ArcMartaInputPaths) {
        $arguments += "--input"
        $arguments += (Resolve-DisplayPath -Path $inputPath)
    }

    $arguments += "--output"
    $arguments += (Resolve-DisplayPath -Path $ArcMartaOutputPath)

    Ensure-ParentDirectory -Path $ArcMartaOutputPath
    Invoke-DotnetCommand -Description "Extracting authoritative MARTA points" -Arguments $arguments -FailureMessage "Authoritative MARTA extraction failed."
    Assert-PathExists -Path $ArcMartaOutputPath -FailureMessage "ARC MARTA output was not produced."
}

function Invoke-ArcParkTrailExtraction {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name -Arguments @{
        InputCount = if ($ArcInputPaths) { $ArcInputPaths.Count } else { 0 }
        OutputPath = $ArcOutputPath
    }

    if (-not $ArcInputPaths -or $ArcInputPaths.Count -eq 0) {
        throw "ArcInputPaths are required for park/trail data."
    }

    if (-not $ArcOutputPath) {
        throw "ArcOutputPath is required when ArcInputPaths are provided."
    }

    foreach ($inputPath in $ArcInputPaths) {
        Assert-PathExists -Path $inputPath -FailureMessage "ARC park/trail input '$inputPath' does not exist."
    }

    $arguments = @("run", "--project", (Resolve-DisplayPath -Path $ProjectPath), "--no-build", "--")
    foreach ($inputPath in $ArcInputPaths) {
        $arguments += "--input"
        $arguments += (Resolve-DisplayPath -Path $inputPath)
    }

    $arguments += "--output"
    $arguments += (Resolve-DisplayPath -Path $ArcOutputPath)
    $arguments += "--minimum-park-square-feet"
    $arguments += ([string]$categoryConfig.minimumParkSquareFeet)
    $arguments += "--minimum-trail-miles"
    $arguments += ([string]$categoryConfig.minimumTrailMiles)
    $arguments += "--minimum-combined-park-trail-miles"
    $arguments += ([string]$categoryConfig.minimumCombinedParkTrailMiles)
    $arguments += "--point-spacing-miles"
    $arguments += ([string]$categoryConfig.pointSpacingMiles)
    if ($categoryConfig.entityCollapsing -and $categoryConfig.entityCollapsing.enabled) {
        $arguments += "--enable-entity-collapse"
        $arguments += "--maximum-collapse-gap-miles"
        $arguments += ([string]$categoryConfig.entityCollapsing.maximumGapMiles)
        foreach ($collapseCategory in $categoryConfig.entityCollapsing.eligibleCategories) {
            $arguments += "--collapse-category"
            $arguments += ([string]$collapseCategory)
        }
    }

    if ($ArcParkOutputPath) {
        $arguments += "--park-output"
        $arguments += (Resolve-DisplayPath -Path $ArcParkOutputPath)
    }

    if ($ArcTrailOutputPath) {
        $arguments += "--trail-output"
        $arguments += (Resolve-DisplayPath -Path $ArcTrailOutputPath)
    }

    if ($ArcFeatureOutputPath) {
        $arguments += "--feature-output"
        $arguments += (Resolve-DisplayPath -Path $ArcFeatureOutputPath)
    }

    if ($ArcOriginalGeometryKmlOutputPath) {
        $arguments += "--original-geometry-kml-output"
        $arguments += (Resolve-DisplayPath -Path $ArcOriginalGeometryKmlOutputPath)
    }

    Ensure-ParentDirectory -Path $ArcOutputPath
    if ($ArcParkOutputPath) { Ensure-ParentDirectory -Path $ArcParkOutputPath }
    if ($ArcTrailOutputPath) { Ensure-ParentDirectory -Path $ArcTrailOutputPath }
    if ($ArcFeatureOutputPath) { Ensure-ParentDirectory -Path $ArcFeatureOutputPath }
    if ($ArcOriginalGeometryKmlOutputPath) { Ensure-ParentDirectory -Path $ArcOriginalGeometryKmlOutputPath }
    Invoke-DotnetCommand -Description "Extracting ARC park/trail geometry" -Arguments $arguments -FailureMessage "ARC geometry extractor failed."
    Assert-PathExists -Path $ArcOutputPath -FailureMessage "ARC park/trail output was not produced."
    return $ArcOutputPath
}

function Invoke-LocationAssembly {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Inputs
    )

    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name -Arguments @{
        InputCount = $Inputs.Count
        OutputPath = (Resolve-DisplayPath -Path $FinalRequestOutputPath)
    }

    foreach ($inputPath in $Inputs) {
        Assert-PathExists -Path $inputPath -FailureMessage "Assembler input '$inputPath' does not exist."
    }
    Assert-PathExists -Path $CategoryConfigPath -FailureMessage "Category config '$CategoryConfigPath' does not exist."

    $arguments = @("run", "--project", (Resolve-DisplayPath -Path $ProjectPath), "--no-build", "--")
    foreach ($inputPath in $Inputs) {
        $arguments += "--input"
        $arguments += (Resolve-DisplayPath -Path $inputPath)
    }

    $arguments += "--output"
    $arguments += (Resolve-DisplayPath -Path $FinalRequestOutputPath)
    $arguments += "--category-config"
    $arguments += (Resolve-DisplayPath -Path $CategoryConfigPath)

    Ensure-ParentDirectory -Path $FinalRequestOutputPath
    Invoke-DotnetCommand -Description "Assembling final category dataset" -Arguments $arguments -FailureMessage "Location assembler failed."
    Assert-PathExists -Path $FinalRequestOutputPath -FailureMessage "Final request output was not produced."
}

function Invoke-KmlGeneration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name -Arguments @{
        InputPath = (Resolve-DisplayPath -Path $FinalRequestOutputPath)
        OutputPath = (Resolve-DisplayPath -Path $KmlOutputPath)
    }

    Assert-PathExists -Path $FinalRequestOutputPath -FailureMessage "Final request output does not exist before KML generation."
    Ensure-ParentDirectory -Path $KmlOutputPath
    Invoke-DotnetCommand `
        -Description "Generating whole-area KML" `
        -Arguments @(
            "run", "--project", (Resolve-DisplayPath -Path $ProjectPath), "--no-build", "--",
            "--input", (Resolve-DisplayPath -Path $FinalRequestOutputPath),
            "--output", (Resolve-DisplayPath -Path $KmlOutputPath)
        ) `
        -FailureMessage "KML generation failed."
    Assert-PathExists -Path $KmlOutputPath -FailureMessage "Whole-area KML was not produced."
}

function Invoke-TileGeneration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name -Arguments @{
        InputPath = (Resolve-DisplayPath -Path $FinalRequestOutputPath)
        OutputDirectory = (Resolve-DisplayPath -Path $TileOutputDirectory)
    }

    if (-not $TileOutputDirectory) {
        Write-Log -Message "Tile output directory not provided; skipping tile generation." -Level "TRACE"
        return
    }

    New-Item -ItemType Directory -Force -Path (Resolve-DisplayPath -Path $TileOutputDirectory) | Out-Null
    Invoke-DotnetCommand `
        -Description "Generating tiled KML outputs" `
        -Arguments @(
            "run", "--project", (Resolve-DisplayPath -Path $ProjectPath), "--no-build", "--",
            "--input", (Resolve-DisplayPath -Path $FinalRequestOutputPath),
            "--output-dir", (Resolve-DisplayPath -Path $TileOutputDirectory),
            "--north", $TileNorth.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "--south", $TileSouth.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "--west", $TileWest.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "--east", $TileEast.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "--lat-step", $TileLatitudeStep.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "--lon-step", $TileLongitudeStep.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        ) `
        -FailureMessage "KML tiler failed."
    Assert-PathExists -Path $TileOutputDirectory -FailureMessage "Tile output directory was not produced."
}

$arcExtractorProjectPath = Join-Path $repoRoot "ArcGeometryExtractor.Console\ArcGeometryExtractor.Console.csproj"
$assemblerProjectPath = Join-Path $repoRoot "LocationAssembler.Console\LocationAssembler.Console.csproj"
$kmlGeneratorProjectPath = Join-Path $repoRoot "KmlGenerator.Console\KmlGenerator.Console.csproj"
$kmlTilerProjectPath = Join-Path $repoRoot "KmlTiler.Console\KmlTiler.Console.csproj"
$requiredMasterLists = @("gyms-master.jsonl", "groceries-master.jsonl")
$traceDirectory = Join-Path $intermediateRoot "category-workflow-$RunId-trace"

Write-Section -Title "Category Workflow Trace"
# Allow callers to skip the upfront builds so multiple scripts can run in parallel without competing on shared
# project outputs. The workflow still executes every console app with --no-build either way.
if (-not $NoBuild) {
    # Solution builds are unstable from inside these Windows PowerShell scripts, but direct project builds are reliable.
    # Build only the projects this workflow executes, then keep every app run on --no-build.
    Invoke-DotnetCommand -Description "Building ARC extractor project" -Arguments @("build", (Resolve-DisplayPath -Path $arcExtractorProjectPath), "-v", "minimal") -FailureMessage "ARC extractor build failed."
    Invoke-DotnetCommand -Description "Building location assembler project" -Arguments @("build", (Resolve-DisplayPath -Path $assemblerProjectPath), "-v", "minimal") -FailureMessage "Location assembler build failed."
    Invoke-DotnetCommand -Description "Building KML generator project" -Arguments @("build", (Resolve-DisplayPath -Path $kmlGeneratorProjectPath), "-v", "minimal") -FailureMessage "KML generator build failed."
    Invoke-DotnetCommand -Description "Building KML tiler project" -Arguments @("build", (Resolve-DisplayPath -Path $kmlTilerProjectPath), "-v", "minimal") -FailureMessage "KML tiler build failed."
}
try {
    if ($KeepIntermediateArtifacts) {
        # Keep durable workflow data separate from per-run outputs and only enable trace capture when the caller
        # explicitly asks to retain proof/intermediate artifacts.
        Initialize-TraceArtifacts -RepoRoot $repoRoot -TraceDirectory $traceDirectory
    }

    Write-ParameterTrace -Values @{
        ArcFeatureOutputPath = $ArcFeatureOutputPath
        ArcMartaOutputPath = $ArcMartaOutputPath
        ArcOutputPath = $ArcOutputPath
        ArcParkOutputPath = $ArcParkOutputPath
        ArcTrailOutputPath = $ArcTrailOutputPath
        FinalRequestOutputPath = $FinalRequestOutputPath
        KmlOutputPath = $KmlOutputPath
        CategoryConfigPath = $CategoryConfigPath
        MasterListOutputDirectory = $MasterListOutputDirectory
        RunId = $RunId
        RunOutputDirectory = $RunOutputDirectory
        TileOutputDirectory = $TileOutputDirectory
    }

    Write-ProjectTrace -ProjectPath $arcExtractorProjectPath -Description "ARC geometry extractor"
    Write-ProjectTrace -ProjectPath $assemblerProjectPath -Description "location assembler"
    Write-ProjectTrace -ProjectPath $kmlGeneratorProjectPath -Description "KML generator"
    Write-ProjectTrace -ProjectPath $kmlTilerProjectPath -Description "KML tiler"
    Assert-WorkflowPrerequisites
    Assert-RequiredMasterLists -RequiredFiles $requiredMasterLists
    Invoke-ArcMartaExtraction -ProjectPath $arcExtractorProjectPath
    $parkTrailInputPath = Invoke-ArcParkTrailExtraction -ProjectPath $arcExtractorProjectPath
    $assemblerInputPaths = @(
        (Join-Path $MasterListOutputDirectory "gyms-master.jsonl"),
        (Join-Path $MasterListOutputDirectory "groceries-master.jsonl"),
        $ArcMartaOutputPath,
        $parkTrailInputPath
    )
    Invoke-LocationAssembly -ProjectPath $assemblerProjectPath -Inputs $assemblerInputPaths
    Invoke-KmlGeneration -ProjectPath $kmlGeneratorProjectPath
    Invoke-TileGeneration -ProjectPath $kmlTilerProjectPath

    if (-not $KeepIntermediateArtifacts) {
        Get-ChildItem -Path (Resolve-DisplayPath -Path $TileOutputDirectory) -File -Filter *.request.json -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path (Resolve-DisplayPath -Path $TileOutputDirectory) "tiles-summary.json") -Force -ErrorAction SilentlyContinue
    }
}
finally {
    if ($KeepIntermediateArtifacts) {
        Complete-TraceArtifacts
    }
    else {
        Remove-Item Env:KMLSUITE_TRACE_EVENTS_PATH -ErrorAction SilentlyContinue
        if (Test-Path $intermediateRoot) {
            Remove-Item -Path $intermediateRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Write-Section -Title "Category Workflow Complete"


