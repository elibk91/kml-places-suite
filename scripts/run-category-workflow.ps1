[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MasterListConfigPath,

    [Parameter(Mandatory = $true)]
    [string]$MasterListOutputDirectory,

    [string]$ResearchConfigPath,

    [string]$ResolvedResearchOutputPath,

    [string[]]$ArcInputPaths,

    [string]$ArcOutputPath,

    [string]$ArcParkOutputPath,

    [string]$ArcTrailOutputPath,

    [string]$ArcFeatureOutputPath,

    [string]$MartaConfigPath,

    [string]$MartaOutputPath,

    [string]$MartaOverridePath,

    [string[]]$ArcMartaInputPaths = @(
        "C:\Users\elibk\Downloads\MARTA_Rail_Stations_-3250756205123355367.kmz"
    ),

    [string]$ArcMartaOutputPath = ".\out\authority\arc\marta-stations.arc.jsonl",

    [Parameter(Mandatory = $true)]
    [string]$FinalRequestOutputPath,

    [Parameter(Mandatory = $true)]
    [string]$KmlOutputPath,

    [string]$TileOutputDirectory,

    [double]$TileNorth = 33.952876,

    [double]$TileSouth = 33.698669,

    [double]$TileWest = -84.54903,

    [double]$TileEast = -84.095141,

    [double]$TileLatitudeStep = 0.07,

    [double]$TileLongitudeStep = 0.09
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $env:GoogleMaps__ApiKey) {
    throw "GoogleMaps__ApiKey is required."
}

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
$solutionPath = Join-Path $repoRoot "KmlSuite.slnx"

$requiredMasterLists = @(
    "gyms-master.jsonl",
    "groceries-master.jsonl",
    "marta-master.jsonl"
)

Write-Host "Building solution..."
dotnet build $solutionPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed."
}

Write-Host "Building category master lists..."
dotnet run --project (Join-Path $repoRoot "MasterListBuilder.Console\MasterListBuilder.Console.csproj") --no-build -- --config $MasterListConfigPath --output-dir $MasterListOutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Master list builder failed."
}

foreach ($fileName in $requiredMasterLists) {
    $path = Join-Path $MasterListOutputDirectory $fileName
    if (-not (Test-Path $path)) {
        throw "Expected master list '$fileName' was not produced."
    }
}

if ($ArcMartaInputPaths -and $ArcMartaInputPaths.Count -gt 0) {
    if (-not $ArcMartaOutputPath) {
        throw "ArcMartaOutputPath is required when ArcMartaInputPaths are provided."
    }

    $arcMartaArguments = @()
    foreach ($inputPath in $ArcMartaInputPaths) {
        $arcMartaArguments += "--input"
        $arcMartaArguments += $inputPath
    }

    $arcMartaArguments += "--output"
    $arcMartaArguments += $ArcMartaOutputPath

    Write-Host "Extracting authoritative MARTA points..."
    dotnet run --project (Join-Path $repoRoot "ArcGeometryExtractor.Console\ArcGeometryExtractor.Console.csproj") --no-build -- @arcMartaArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Authoritative MARTA extraction failed."
    }
}
elseif ($MartaConfigPath) {
    if (-not $MartaOutputPath) {
        throw "MartaOutputPath is required when MartaConfigPath is provided."
    }

    Write-Host "Resolving curated MARTA stations..."
    dotnet run --project (Join-Path $repoRoot "ResearchPointResolver.Console\ResearchPointResolver.Console.csproj") --no-build -- --config $MartaConfigPath --output $MartaOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Curated MARTA resolver failed."
    }

    if ($MartaOverridePath) {
        Write-Host "Applying curated MARTA station overrides..."
        $overrides = Get-Content $MartaOverridePath | ConvertFrom-Json
        $records = Get-Content $MartaOutputPath | ForEach-Object { $_ | ConvertFrom-Json }
        $byName = @{}

        foreach ($override in $overrides) {
            $byName[$override.name] = $override
        }

        $updated = foreach ($record in $records) {
            $override = $byName[$record.name]
            if ($null -eq $override) {
                $record
                continue
            }

            [pscustomobject]@{
                query = $record.query
                category = $record.category
                placeId = if ($override.PSObject.Properties.Match('placeId').Count -gt 0 -and $override.placeId) { $override.placeId } else { $record.placeId }
                name = $record.name
                formattedAddress = $override.formattedAddress
                latitude = $override.latitude
                longitude = $override.longitude
                types = $override.types
                sourceQueryType = $record.sourceQueryType
            }
        }

        $updated | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content $MartaOutputPath
    }
}

$parkTrailInputPath = $null

if ($ArcInputPaths -and $ArcInputPaths.Count -gt 0) {
    if (-not $ArcOutputPath) {
        throw "ArcOutputPath is required when ArcInputPaths are provided."
    }

    $arcArguments = @()
    foreach ($inputPath in $ArcInputPaths) {
        $arcArguments += "--input"
        $arcArguments += $inputPath
    }

    $arcArguments += "--output"
    $arcArguments += $ArcOutputPath

    if ($ArcParkOutputPath) {
        $arcArguments += "--park-output"
        $arcArguments += $ArcParkOutputPath
    }

    if ($ArcTrailOutputPath) {
        $arcArguments += "--trail-output"
        $arcArguments += $ArcTrailOutputPath
    }

    if ($ArcFeatureOutputPath) {
        $arcArguments += "--feature-output"
        $arcArguments += $ArcFeatureOutputPath
    }

    Write-Host "Extracting ARC park/trail geometry..."
    dotnet run --project (Join-Path $repoRoot "ArcGeometryExtractor.Console\ArcGeometryExtractor.Console.csproj") --no-build -- @arcArguments
    if ($LASTEXITCODE -ne 0) {
        throw "ARC geometry extractor failed."
    }

    $parkTrailInputPath = $ArcOutputPath
}
elseif ($ResearchConfigPath -and $ResolvedResearchOutputPath) {
    Write-Host "Resolving researched park/trail targets..."
    dotnet run --project (Join-Path $repoRoot "ResearchPointResolver.Console\ResearchPointResolver.Console.csproj") --no-build -- --config $ResearchConfigPath --output $ResolvedResearchOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Research point resolver failed."
    }

    $parkTrailInputPath = $ResolvedResearchOutputPath
}
else {
    throw "Either ARC inputs or research target inputs are required for park/trail data."
}

$martaInputPath = if ($ArcMartaOutputPath) { $ArcMartaOutputPath } elseif ($MartaOutputPath) { $MartaOutputPath } else { Join-Path $MasterListOutputDirectory "marta-master.jsonl" }

$assemblerInputs = @(
    "--input", (Join-Path $MasterListOutputDirectory "gyms-master.jsonl"),
    "--input", (Join-Path $MasterListOutputDirectory "groceries-master.jsonl"),
    "--input", $martaInputPath,
    "--input", $parkTrailInputPath,
    "--output", $FinalRequestOutputPath
)

Write-Host "Assembling final category dataset..."
dotnet run --project (Join-Path $repoRoot "LocationAssembler.Console\LocationAssembler.Console.csproj") --no-build -- @assemblerInputs
if ($LASTEXITCODE -ne 0) {
    throw "Location assembler failed."
}

Write-Host "Generating whole-area KML..."
dotnet run --project (Join-Path $repoRoot "KmlGenerator.Console\KmlGenerator.Console.csproj") --no-build -- --input $FinalRequestOutputPath --output $KmlOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "KML generation failed."
}

if ($TileOutputDirectory) {
    Write-Host "Generating tiled KML outputs..."
    dotnet run --project (Join-Path $repoRoot "KmlTiler.Console\KmlTiler.Console.csproj") --no-build -- --input $FinalRequestOutputPath --output-dir $TileOutputDirectory --north $TileNorth --south $TileSouth --west $TileWest --east $TileEast --lat-step $TileLatitudeStep --lon-step $TileLongitudeStep
    if ($LASTEXITCODE -ne 0) {
        throw "KML tiler failed."
    }
}

Write-Host "Category workflow complete."
