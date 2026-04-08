[CmdletBinding()]
param(
    [string]$GymInputPath = "C:\Users\elibk\Downloads\Houston gyms.json",
    [string]$GroceryInputPath = "C:\Users\elibk\Downloads\Houston grocery store.json",
    [string]$OutputDirectory = "C:\repos\kml-places-suite\workflow\out\diagnostics\houston-google-import-source"
)

$ErrorActionPreference = "Stop"

$gymBrandMap = [ordered]@{
    "24 hour fitness" = "24 Hour Fitness.json"
    "anytime fitness" = "Anytime Fitness.json"
    "crunch fitness" = "Crunch Fitness.json"
    "eos fitness" = "Eos Fitness.json"
    "la fitness" = "la fitness.json"
    "life time" = "Life Time.json"
    "planet fitness" = "planet fitness.json"
    "ymca" = "YMCA.json"
}

$groceryBrandMap = [ordered]@{
    "aldi" = "ALDI.json"
    "h-e-b" = "H-E-B.json"
    "kroger" = "Kroger.json"
    "randalls" = "Randalls.json"
    "sam's club" = "Sams Club.json"
    "sprouts farmers market" = "Sprouts Farmers Market.json"
    "trader joe's" = "Trader Joes.json"
    "whole foods market" = "Whole Foods Market.json"
}

function Get-JsonPayload {
    param([string]$Path)

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $start = $raw.IndexOf('[')
    if ($start -lt 0) {
        throw "Could not find JSON array start in '$Path'."
    }

    return $raw.Substring($start) | ConvertFrom-Json
}

function Get-ArrayItems {
    param($Node)

    if ($null -eq $Node -or $Node -is [string]) {
        return @()
    }

    return @($Node)
}

function Get-NormalizedName {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.Normalize([Text.NormalizationForm]::FormD)
    $builder = New-Object System.Text.StringBuilder

    foreach ($char in $normalized.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($char) -ne [Globalization.UnicodeCategory]::NonSpacingMark) {
            [void]$builder.Append($char)
        }
    }

    return ($builder.ToString().ToLowerInvariant() -replace '\s+', ' ').Trim()
}

function Get-BrandOutputFile {
    param(
        [string]$Title,
        [hashtable]$BrandMap
    )

    $normalizedTitle = Get-NormalizedName -Value $Title
    foreach ($brandName in $BrandMap.Keys) {
        if ($normalizedTitle.StartsWith($brandName, [System.StringComparison]::Ordinal)) {
            return $BrandMap[$brandName]
        }
    }

    return $null
}

function Get-AggregateRows {
    param($Payload)

    $topLevel = Get-ArrayItems -Node $Payload
    if ($topLevel.Count -eq 0) {
        return @()
    }

    $header = Get-ArrayItems -Node $topLevel[0]
    if ($header.Count -le 8) {
        return @()
    }

    return Get-ArrayItems -Node $header[8]
}

function Convert-AggregateRowToCandidate {
    param($Row)

    $items = Get-ArrayItems -Node $Row
    if ($items.Count -lt 3) {
        return $null
    }

    $details = Get-ArrayItems -Node $items[1]
    if ($details.Count -lt 8) {
        return $null
    }

    $title = [string]$details[2]
    if ([string]::IsNullOrWhiteSpace($title)) {
        $title = [string]$items[2]
    }
    $address = [string]$details[4]
    $coords = Get-ArrayItems -Node $details[5]
    $entityIds = Get-ArrayItems -Node $details[6]
    $entityPath = [string]$details[7]

    if ([string]::IsNullOrWhiteSpace($title) -or $coords.Count -lt 4) {
        return $null
    }

    $latitude = $coords[2]
    $longitude = $coords[3]
    if ($latitude -isnot [double] -and $latitude -isnot [decimal]) {
        return $null
    }

    if ($longitude -isnot [double] -and $longitude -isnot [decimal]) {
        return $null
    }

    $placeId = if (-not [string]::IsNullOrWhiteSpace($entityPath)) {
        $entityPath
    }
    elseif ($entityIds.Count -ge 2) {
        "{0}:{1}" -f $entityIds[0], $entityIds[1]
    }
    else {
        $null
    }

    if ([string]::IsNullOrWhiteSpace($placeId)) {
        return $null
    }

    return [pscustomobject]@{
        Title = $title
        Address = $address
        PlaceId = $placeId
        Latitude = [double]$latitude
        Longitude = [double]$longitude
    }
}

function Export-AggregateCategory {
    param(
        [string]$InputPath,
        [hashtable]$BrandMap,
        [string]$OutputDirectory
    )

    $payload = Get-JsonPayload -Path $InputPath
    $rows = Get-AggregateRows -Payload $payload
    $recordsByFile = @{}

    foreach ($fileName in $BrandMap.Values) {
        $recordsByFile[$fileName] = @()
    }

    foreach ($row in $rows) {
        $candidate = Convert-AggregateRowToCandidate -Row $row
        if ($null -eq $candidate) {
            continue
        }

        $fileName = Get-BrandOutputFile -Title $candidate.Title -BrandMap $BrandMap
        if ([string]::IsNullOrWhiteSpace($fileName)) {
            continue
        }

        $recordsByFile[$fileName] += ,@(
            $candidate.Title,
            $candidate.PlaceId,
            @(
                $null,
                $null,
                $candidate.Latitude,
                $candidate.Longitude
            )
        )
    }

    foreach ($entry in $recordsByFile.GetEnumerator()) {
        $outputPath = Join-Path $OutputDirectory $entry.Key
        ConvertTo-Json -InputObject ([object[]]$entry.Value) -Depth 6 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    }

    return $recordsByFile.GetEnumerator() |
        Sort-Object Name |
        ForEach-Object {
            [pscustomobject]@{
                fileName = $_.Key
                kept = $_.Value.Count
            }
        }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$summary = @()
$summary += Export-AggregateCategory -InputPath $GymInputPath -BrandMap $gymBrandMap -OutputDirectory $OutputDirectory
$summary += Export-AggregateCategory -InputPath $GroceryInputPath -BrandMap $groceryBrandMap -OutputDirectory $OutputDirectory

$summaryPath = Join-Path $OutputDirectory "conversion-summary.json"
$summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Output "Converted source directory: $OutputDirectory"
Write-Output "Summary: $summaryPath"
$summary | ForEach-Object { Write-Output ("{0}: {1}" -f $_.fileName, $_.kept) }
