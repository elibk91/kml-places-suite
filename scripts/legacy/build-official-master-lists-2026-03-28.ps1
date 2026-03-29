[CmdletBinding()]
param(
    [string]$WorkingDirectory = ".\scripts\in\master-lists\official-one-time-2026-03-28",
    [string]$LegacyDirectoryRoot = ".\scripts\in\master-lists\legacy",
    [string]$GymOutputPath = ".\scripts\in\master-lists\gyms-master.jsonl",
    [string]$GroceryOutputPath = ".\scripts\in\master-lists\groceries-master.jsonl",
    [switch]$NoBackup
)

$ErrorActionPreference = "Stop"

function Normalize-AddressKey {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.ToLowerInvariant()
    $normalized = $normalized -replace '\bbuilding\s+[a-z0-9]+\b', ''
    $normalized = $normalized -replace '[^a-z0-9]+', ' '
    $normalized = $normalized -replace '\b(usa|united states)\b', ''
    $normalized = $normalized -replace '\b(suite|ste|unit)\b', ' ste '
    $normalized = $normalized -replace '\bstreet\b', ' st '
    $normalized = $normalized -replace '\broad\b', ' rd '
    $normalized = $normalized -replace '\bdrive\b', ' dr '
    $normalized = $normalized -replace '\bboulevard\b', ' blvd '
    $normalized = $normalized -replace '\bhighway\b', ' hwy '
    $normalized = $normalized -replace '\bparkway\b', ' pkwy '
    $normalized = $normalized -replace '\bavenue\b', ' ave '
    $normalized = $normalized -replace '\bplace\b', ' pl '
    $normalized = $normalized -replace '\bcourt\b', ' ct '
    $normalized = $normalized -replace '\bnortheast\b', ' ne '
    $normalized = $normalized -replace '\bnorthwest\b', ' nw '
    $normalized = $normalized -replace '\bsoutheast\b', ' se '
    $normalized = $normalized -replace '\bsouthwest\b', ' sw '
    $normalized = $normalized -replace '\s+', ' '
    return $normalized.Trim()
}

function Read-Jsonl {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return @()
    }

    return @(Get-Content $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Build-LegacyIndex {
    param([object[]]$LegacyRows)

    $index = @{}
    foreach ($row in $LegacyRows) {
        $formattedKey = Normalize-AddressKey $row.formattedAddress
        if (-not [string]::IsNullOrWhiteSpace($formattedKey) -and -not $index.ContainsKey($formattedKey)) {
            $index[$formattedKey] = $row
        }
    }

    return $index
}

function Resolve-CoordinateRecord {
    param(
        [object]$OfficialRow,
        [hashtable]$LegacyIndex
    )

    if ($null -ne $OfficialRow.latitude -and $null -ne $OfficialRow.longitude) {
        return [pscustomobject]@{
            LegacyMatch = $null
            Latitude = [double]$OfficialRow.latitude
            Longitude = [double]$OfficialRow.longitude
            Resolution = "official"
        }
    }

    $formattedKey = Normalize-AddressKey $OfficialRow.formattedAddress
    if (-not [string]::IsNullOrWhiteSpace($formattedKey) -and $LegacyIndex.ContainsKey($formattedKey)) {
        $legacyMatch = $LegacyIndex[$formattedKey]
        return [pscustomobject]@{
            LegacyMatch = $legacyMatch
            Latitude = [double]$legacyMatch.latitude
            Longitude = [double]$legacyMatch.longitude
            Resolution = "legacy-address-match"
        }
    }

    return [pscustomobject]@{
        LegacyMatch = $null
        Latitude = $null
        Longitude = $null
        Resolution = "unresolved"
    }
}

if (-not (Test-Path $WorkingDirectory)) {
    throw "Working directory '$WorkingDirectory' does not exist."
}

$officialRowsPath = Join-Path $WorkingDirectory "official-brand-records.json"
if (-not (Test-Path $officialRowsPath)) {
    throw "Expected official records file '$officialRowsPath' was not found."
}

if (-not $NoBackup) {
    $runId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"
    $backupDirectory = Join-Path $LegacyDirectoryRoot $runId
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    Copy-Item $GymOutputPath $backupDirectory
    Copy-Item $GroceryOutputPath $backupDirectory
    Write-Host "Backed up current master lists to $backupDirectory"
}

$legacyGymRows = Read-Jsonl -Path $GymOutputPath
$legacyGroceryRows = Read-Jsonl -Path $GroceryOutputPath
$legacyAllRows = @($legacyGymRows) + @($legacyGroceryRows)
$legacyIndex = Build-LegacyIndex -LegacyRows $legacyAllRows

$officialPayload = Get-Content $officialRowsPath -Raw | ConvertFrom-Json
$officialRows = @($officialPayload.records)

$resolvedGymRows = New-Object System.Collections.Generic.List[object]
$resolvedGroceryRows = New-Object System.Collections.Generic.List[object]
$reviewRows = New-Object System.Collections.Generic.List[object]

foreach ($officialRow in $officialRows) {
    $resolved = Resolve-CoordinateRecord -OfficialRow $officialRow -LegacyIndex $legacyIndex
    $normalized = [pscustomobject]@{
        query = $officialRow.brand
        category = $officialRow.category
        placeId = if ($officialRow.sourceUrl) { "official::$($officialRow.brand)::$($officialRow.sourceUrl)" } else { "official::$($officialRow.brand)::$($officialRow.formattedAddress)" }
        name = $officialRow.name
        formattedAddress = $officialRow.formattedAddress
        latitude = $resolved.Latitude
        longitude = $resolved.Longitude
        types = @($officialRow.types)
        sourceQueryType = "official-one-time"
        searchNames = @($officialRow.name)
    }

    $reviewRows.Add([pscustomobject]@{
        brand = $officialRow.brand
        category = $officialRow.category
        name = $officialRow.name
        formattedAddress = $officialRow.formattedAddress
        sourceUrl = $officialRow.sourceUrl
        coordinateResolution = $resolved.Resolution
        latitude = $resolved.Latitude
        longitude = $resolved.Longitude
    }) | Out-Null

    if ($officialRow.category -eq "gym") {
        $resolvedGymRows.Add($normalized) | Out-Null
    }
    elseif ($officialRow.category -eq "grocery") {
        $resolvedGroceryRows.Add($normalized) | Out-Null
    }
}

$reviewPath = Join-Path $WorkingDirectory "official-brand-records.review.json"
$reviewRows | ConvertTo-Json -Depth 6 | Set-Content $reviewPath

$resolvedGymRows | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 6 } | Set-Content $GymOutputPath
$resolvedGroceryRows | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 6 } | Set-Content $GroceryOutputPath

Write-Host "Wrote $($resolvedGymRows.Count) gym rows to $GymOutputPath"
Write-Host "Wrote $($resolvedGroceryRows.Count) grocery rows to $GroceryOutputPath"
Write-Host "Wrote review artifact to $reviewPath"
