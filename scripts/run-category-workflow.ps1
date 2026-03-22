[CmdletBinding()]
param(
    [string]$MasterListOutputDirectory = ".\scripts\in\master-lists",

    [string]$RunId,

    [string]$RunOutputDirectory,

    [string[]]$ArcInputPaths,

    [string]$ArcOutputPath,

    [string]$ArcParkOutputPath,

    [string]$ArcTrailOutputPath,

    [string]$ArcFeatureOutputPath,

    [string[]]$ArcMartaInputPaths,

    [string]$ArcMartaOutputPath,

    [string]$FinalRequestOutputPath,

    [string]$KmlOutputPath,

    [string]$TileOutputDirectory,

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

if (-not $ArcInputPaths -or $ArcInputPaths.Count -eq 0) {
    # Keep durable source KML/KMZ in-repo so active workflows do not depend on developer Downloads folders.
    $ArcInputPaths = @(
        (Join-Path $arcSourceRoot "City_of_Atlanta_Parks.kml"),
        (Join-Path $arcSourceRoot "Official_Atlanta_Beltline_Trails.kml"),
        (Join-Path $arcSourceRoot "Park_Layer_-4860561661912367000.kmz"),
        (Join-Path $arcSourceRoot "Parks_Trails_assets.kml"),
        (Join-Path $arcSourceRoot "Trail_Plan_Inventory_-7693997307718543817.kmz"),
        (Join-Path $arcSourceRoot "Trails_-8511739637663957148.kmz")
    )
}

if (-not $ArcMartaInputPaths -or $ArcMartaInputPaths.Count -eq 0) {
    $ArcMartaInputPaths = @(
        (Join-Path $arcSourceRoot "MARTA_Rail_Stations_-3250756205123355367.kmz")
    )
}

if (-not $RunId) {
    $RunId = Get-Date -Format "yyyyMMdd-HHmmss"
}

if (-not $RunOutputDirectory) {
    $RunOutputDirectory = Join-Path $scriptDirectory "out\runs\category-workflow\$RunId"
}

if (-not $ArcOutputPath) {
    $ArcOutputPath = Join-Path $RunOutputDirectory "arc\arc-parks-trails-points.jsonl"
}

if (-not $ArcParkOutputPath) {
    $ArcParkOutputPath = Join-Path $RunOutputDirectory "arc\arc-park-points.jsonl"
}

if (-not $ArcTrailOutputPath) {
    $ArcTrailOutputPath = Join-Path $RunOutputDirectory "arc\arc-trail-points.jsonl"
}

if (-not $ArcFeatureOutputPath) {
    $ArcFeatureOutputPath = Join-Path $RunOutputDirectory "arc\arc-features.jsonl"
}

if (-not $ArcMartaOutputPath) {
    $ArcMartaOutputPath = Join-Path $RunOutputDirectory "arc\marta-stations.arc.jsonl"
}

if (-not $FinalRequestOutputPath) {
    $FinalRequestOutputPath = Join-Path $RunOutputDirectory "requests\atlanta-category-request.arc.json"
}

if (-not $KmlOutputPath) {
    $KmlOutputPath = Join-Path $RunOutputDirectory "kml\atlanta-category-outline.arc.kml"
}

if (-not $TileOutputDirectory) {
    $TileOutputDirectory = Join-Path $RunOutputDirectory "tiles"
}

function Assert-WorkflowPrerequisites {
    Write-FunctionTrace -Name $MyInvocation.MyCommand.Name

    Assert-PathExists -Path $MasterListOutputDirectory -FailureMessage "MasterListOutputDirectory does not exist."
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

    Ensure-ParentDirectory -Path $ArcOutputPath
    if ($ArcParkOutputPath) { Ensure-ParentDirectory -Path $ArcParkOutputPath }
    if ($ArcTrailOutputPath) { Ensure-ParentDirectory -Path $ArcTrailOutputPath }
    if ($ArcFeatureOutputPath) { Ensure-ParentDirectory -Path $ArcFeatureOutputPath }
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

    $arguments = @("run", "--project", (Resolve-DisplayPath -Path $ProjectPath), "--no-build", "--")
    foreach ($inputPath in $Inputs) {
        $arguments += "--input"
        $arguments += (Resolve-DisplayPath -Path $inputPath)
    }

    $arguments += "--output"
    $arguments += (Resolve-DisplayPath -Path $FinalRequestOutputPath)

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
$traceDirectory = Join-Path $RunOutputDirectory "trace"

Write-Section -Title "Category Workflow Trace"
# Solution builds are unstable from inside these Windows PowerShell scripts, but direct project builds are reliable.
# Build only the projects this workflow executes, then keep every app run on --no-build.
Invoke-DotnetCommand -Description "Building ARC extractor project" -Arguments @("build", (Resolve-DisplayPath -Path $arcExtractorProjectPath), "-v", "minimal") -FailureMessage "ARC extractor build failed."
Invoke-DotnetCommand -Description "Building location assembler project" -Arguments @("build", (Resolve-DisplayPath -Path $assemblerProjectPath), "-v", "minimal") -FailureMessage "Location assembler build failed."
Invoke-DotnetCommand -Description "Building KML generator project" -Arguments @("build", (Resolve-DisplayPath -Path $kmlGeneratorProjectPath), "-v", "minimal") -FailureMessage "KML generator build failed."
Invoke-DotnetCommand -Description "Building KML tiler project" -Arguments @("build", (Resolve-DisplayPath -Path $kmlTilerProjectPath), "-v", "minimal") -FailureMessage "KML tiler build failed."
# Keep durable workflow data separate from per-run outputs and only enable trace capture for the runtime execution phase.
Initialize-TraceArtifacts -RepoRoot $repoRoot -TraceDirectory $traceDirectory
try {
    Write-ParameterTrace -Values @{
        ArcFeatureOutputPath = $ArcFeatureOutputPath
        ArcMartaOutputPath = $ArcMartaOutputPath
        ArcOutputPath = $ArcOutputPath
        ArcParkOutputPath = $ArcParkOutputPath
        ArcTrailOutputPath = $ArcTrailOutputPath
        FinalRequestOutputPath = $FinalRequestOutputPath
        KmlOutputPath = $KmlOutputPath
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
}
finally {
    Complete-TraceArtifacts
}
Write-Section -Title "Category Workflow Complete"


