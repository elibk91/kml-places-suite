[CmdletBinding()]
param(
    [string]$InputDirectory = "C:\Users\elibk\Downloads",
    [string]$OutputDirectory = "C:\repos\kml-places-suite\data\inputs\houston\master-lists"
)

$ErrorActionPreference = "Stop"

$fileSpecs = @(
    @{ FileName = "Eos Fitness.json"; Query = "EoS Fitness"; Category = "gym" }
    @{ FileName = "Life Time.json"; Query = "Life Time"; Category = "gym" }
    @{ FileName = "YMCA.json"; Query = "YMCA"; Category = "gym" }
    @{ FileName = "24 Hour Fitness.json"; Query = "24 Hour Fitness"; Category = "gym" }
    @{ FileName = "la fitness.json"; Query = "LA Fitness"; Category = "gym" }
    @{ FileName = "planet fitness.json"; Query = "Planet Fitness"; Category = "gym" }
    @{ FileName = "Anytime Fitness.json"; Query = "Anytime Fitness"; Category = "gym" }
    @{ FileName = "Crunch Fitness.json"; Query = "Crunch Fitness"; Category = "gym" }
    @{ FileName = "Sams Club.json"; Query = "Sam's Club"; Category = "grocery" }
    @{ FileName = "Randalls.json"; Query = "Randalls"; Category = "grocery" }
    @{ FileName = "Sprouts Farmers Market.json"; Query = "Sprouts Farmers Market"; Category = "grocery" }
    @{ FileName = "Whole Foods Market.json"; Query = "Whole Foods Market"; Category = "grocery" }
    @{ FileName = "Trader Joes.json"; Query = "Trader Joe's"; Category = "grocery" }
    @{ FileName = "ALDI.json"; Query = "ALDI"; Category = "grocery" }
    @{ FileName = "Kroger.json"; Query = "Kroger"; Category = "grocery" }
    @{ FileName = "H-E-B.json"; Query = "H-E-B"; Category = "grocery" }
)

function Get-ArrayItems {
    param($Node)

    if ($null -eq $Node -or $Node -is [string]) {
        return @()
    }

    return @($Node)
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

function Get-NormalizedFileKey {
    param([string]$Value)

    $normalized = $Value.Normalize([Text.NormalizationForm]::FormD)
    $builder = New-Object System.Text.StringBuilder

    foreach ($char in $normalized.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($char) -ne [Globalization.UnicodeCategory]::NonSpacingMark) {
            [void]$builder.Append($char)
        }
    }

    return (($builder.ToString().ToLowerInvariant()) -replace "[^a-z0-9]+", "")
}

function Resolve-InputFilePath {
    param(
        [string]$Directory,
        [string]$ExpectedFileName
    )

    $expectedKey = Get-NormalizedFileKey -Value $ExpectedFileName
    $candidates = Get-ChildItem -LiteralPath $Directory -File

    foreach ($candidate in $candidates) {
        if ((Get-NormalizedFileKey -Value $candidate.Name) -eq $expectedKey) {
            return $candidate.FullName
        }
    }

    return $null
}

function Try-GetPlaceCandidate {
    param($Node)

    $items = Get-ArrayItems -Node $Node
    if ($items.Count -lt 3) {
        return $null
    }

    if (-not ($items[0] -is [string]) -or [string]::IsNullOrWhiteSpace($items[0])) {
        return $null
    }

    if (-not ($items[1] -is [string]) -or [string]::IsNullOrWhiteSpace($items[1])) {
        return $null
    }

    $coords = Get-ArrayItems -Node $items[2]
    if ($coords.Count -lt 4) {
        return $null
    }

    if ($coords[2] -isnot [double] -and $coords[2] -isnot [decimal]) {
        return $null
    }

    if ($coords[3] -isnot [double] -and $coords[3] -isnot [decimal]) {
        return $null
    }

    return [pscustomobject]@{
        Title = [string]$items[0]
        PlaceId = [string]$items[1]
        Latitude = [double]$coords[2]
        Longitude = [double]$coords[3]
    }
}

function Get-PlaceCandidates {
    param($Node)

    $results = New-Object System.Collections.Generic.List[object]

    function Visit-Node {
        param($Current)

        if ($null -eq $Current -or $Current -is [string]) {
            return
        }

        $candidate = Try-GetPlaceCandidate -Node $Current
        if ($null -ne $candidate) {
            $results.Add($candidate)
        }

        if ($Current -is [System.Collections.IEnumerable]) {
            foreach ($child in @($Current)) {
                Visit-Node -Current $child
            }
        }
        elseif ($Current.PSObject -and $Current.PSObject.Properties) {
            foreach ($property in $Current.PSObject.Properties) {
                Visit-Node -Current $property.Value
            }
        }
    }

    Visit-Node -Current $Node
    return $results
}

function Test-IsSubEntityRow {
    param(
        [string]$Category,
        [string]$Title
    )

    if ($Category -ne "grocery") {
        return $false
    }

    return $Title -match "Pharmacy|Fuel Center|Money Services|Deli|Bakery|Vision Center|Auto Care|Liquor|Wine Shop"
}

function Convert-ToMasterListRecord {
    param(
        [hashtable]$Spec,
        [pscustomobject]$Candidate
    )

    $types = if ($Spec.Category -eq "gym") { ,@("gym") } else { ,@("grocery_store") }

    return [pscustomobject]@{
        query = $Spec.Query
        category = $Spec.Category
        placeId = $Candidate.PlaceId
        name = $Candidate.Title
        formattedAddress = $Candidate.Title
        latitude = $Candidate.Latitude
        longitude = $Candidate.Longitude
        types = $types
        sourceQueryType = "google_export"
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$recordsByCategory = @{
    gym = New-Object System.Collections.Generic.List[object]
    grocery = New-Object System.Collections.Generic.List[object]
}

$summary = New-Object System.Collections.Generic.List[object]

foreach ($spec in $fileSpecs) {
    $path = Resolve-InputFilePath -Directory $InputDirectory -ExpectedFileName $spec.FileName
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Missing input file matching '$($spec.FileName)' in '$InputDirectory'."
    }

    $payload = Get-JsonPayload -Path $path
    $candidates = Get-PlaceCandidates -Node $payload

    $seenPlaceIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $kept = New-Object System.Collections.Generic.List[object]
    $removed = New-Object System.Collections.Generic.List[object]

    foreach ($candidate in $candidates) {
        if (-not $seenPlaceIds.Add($candidate.PlaceId)) {
            continue
        }

        if ($candidate.Title -eq $spec.Query) {
            $removed.Add([pscustomobject]@{ reason = "brand_header"; title = $candidate.Title; placeId = $candidate.PlaceId })
            continue
        }

        if (Test-IsSubEntityRow -Category $spec.Category -Title $candidate.Title) {
            $removed.Add([pscustomobject]@{ reason = "sub_entity"; title = $candidate.Title; placeId = $candidate.PlaceId })
            continue
        }

        $record = Convert-ToMasterListRecord -Spec $spec -Candidate $candidate
        $kept.Add($record)
        $recordsByCategory[$spec.Category].Add($record)
    }

    $summary.Add([pscustomobject]@{
        fileName = $spec.FileName
        query = $spec.Query
        category = $spec.Category
        extractedCandidates = $candidates.Count
        kept = $kept.Count
        removed = $removed.Count
    })
}

$gymPath = Join-Path $OutputDirectory "gyms-master.jsonl"
$groceryPath = Join-Path $OutputDirectory "groceries-master.jsonl"
$summaryPath = Join-Path $OutputDirectory "houston-google-import.summary.json"

$recordsByCategory["gym"] | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content -LiteralPath $gymPath -Encoding UTF8
$recordsByCategory["grocery"] | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content -LiteralPath $groceryPath -Encoding UTF8
$summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Output "Gyms master list: $gymPath"
Write-Output "Groceries master list: $groceryPath"
Write-Output "Summary: $summaryPath"
Write-Output ("Gym rows: {0}" -f $recordsByCategory["gym"].Count)
Write-Output ("Grocery rows: {0}" -f $recordsByCategory["grocery"].Count)
