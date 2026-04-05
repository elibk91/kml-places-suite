[CmdletBinding()]
param(
    [string]$GymInputPath = ".\scripts\in\master-lists\gyms-master.jsonl",
    [string]$GroceryInputPath = ".\scripts\in\master-lists\groceries-master.jsonl",
    [string]$GymOutputPath = ".\scripts\in\master-lists\gyms-master.jsonl",
    [string]$GroceryOutputPath = ".\scripts\in\master-lists\groceries-master.jsonl",
    [string]$LegacyDirectoryRoot = ".\scripts\in\master-lists\legacy",
    [string]$ReviewDirectory = ".\scripts\in\master-lists\official-one-time-2026-03-28\cleanup-review",
    [switch]$NoBackup
)

$ErrorActionPreference = "Stop"

function Read-Jsonl {
    param([string]$Path)

    return @(Get-Content $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Write-Jsonl {
    param(
        [string]$Path,
        [object[]]$Rows
    )

    $Rows | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 8 } | Set-Content $Path
}

function Normalize-Text {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.ToLowerInvariant()
    $normalized = $normalized -replace '&', ' and '
    $normalized = $normalized -replace '[^a-z0-9]+', ' '
    $normalized = $normalized -replace '\s+', ' '
    return $normalized.Trim()
}

function Normalize-Address {
    param([string]$Value)

    $normalized = Normalize-Text $Value
    $normalized = $normalized -replace '\bstreet\b', ' st '
    $normalized = $normalized -replace '\broad\b', ' rd '
    $normalized = $normalized -replace '\bdrive\b', ' dr '
    $normalized = $normalized -replace '\bavenue\b', ' ave '
    $normalized = $normalized -replace '\bboulevard\b', ' blvd '
    $normalized = $normalized -replace '\bparkway\b', ' pkwy '
    $normalized = $normalized -replace '\bhighway\b', ' hwy '
    $normalized = $normalized -replace '\bplace\b', ' pl '
    $normalized = $normalized -replace '\bcourt\b', ' ct '
    $normalized = $normalized -replace '\bnortheast\b', ' ne '
    $normalized = $normalized -replace '\bnorthwest\b', ' nw '
    $normalized = $normalized -replace '\bsoutheast\b', ' se '
    $normalized = $normalized -replace '\bsouthwest\b', ' sw '
    $normalized = $normalized -replace '\s+', ' '
    return $normalized.Trim()
}

function Matches-Brand {
    param(
        [string]$Query,
        [string]$Name
    )

    $normalizedName = Normalize-Text $Name
    $nameTokens = $normalizedName.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($alias in $script:BrandAliases[$Query]) {
        $normalizedAlias = Normalize-Text $alias
        if ([string]::IsNullOrWhiteSpace($normalizedAlias)) {
            continue
        }

        if ((" " + $normalizedName + " ").Contains(" " + $normalizedAlias + " ")) {
            return $true
        }

        $tokens = $normalizedAlias.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
        if ($tokens.Count -gt 0 -and ($tokens | Where-Object { $_ -notin $nameTokens }).Count -eq 0) {
            return $true
        }
    }

    return $false
}

function Get-DistanceMiles {
    param(
        [double]$LatitudeA,
        [double]$LongitudeA,
        [double]$LatitudeB,
        [double]$LongitudeB
    )

    $earthRadiusMiles = 3958.7613d
    $toRadians = [Math]::PI / 180d

    $lat1 = $LatitudeA * $toRadians
    $lon1 = $LongitudeA * $toRadians
    $lat2 = $LatitudeB * $toRadians
    $lon2 = $LongitudeB * $toRadians

    $dLat = $lat2 - $lat1
    $dLon = $lon2 - $lon1

    $a = [Math]::Sin($dLat / 2d) * [Math]::Sin($dLat / 2d) +
         [Math]::Cos($lat1) * [Math]::Cos($lat2) * [Math]::Sin($dLon / 2d) * [Math]::Sin($dLon / 2d)
    $c = 2d * [Math]::Atan2([Math]::Sqrt($a), [Math]::Sqrt(1d - $a))
    return $earthRadiusMiles * $c
}

function Is-GrocerySupportEntity {
    param([string]$Name)

    $normalizedName = Normalize-Text $Name
    return $script:GrocerySupportKeywords | Where-Object { $normalizedName.Contains($_) } | Select-Object -First 1
}

function Is-GymNonClubEntity {
    param(
        [string]$Query,
        [string]$Name
    )

    $normalizedName = Normalize-Text $Name
    if ($Query -eq "YMCA") {
        return $script:YmcaRejectKeywords | Where-Object { $normalizedName.Contains($_) } | Select-Object -First 1
    }

    return $null
}

function Choose-CanonicalScore {
    param([object]$Row)

    $score = 0
    if (Matches-Brand -Query $Row.query -Name $Row.name) { $score += 100 }
    if (-not (Is-GrocerySupportEntity -Name $Row.name)) { $score += 100 }
    if ($Row.types -contains "grocery_store" -or $Row.types -contains "supermarket" -or $Row.types -contains "gym") { $score += 25 }
    if ((Normalize-Text $Row.name).Contains("super market")) { $score += 10 }
    if ((Normalize-Text $Row.name) -eq (Normalize-Text $Row.query)) { $score += 10 }
    return $score
}

function Clean-Gyms {
    param([object[]]$Rows)

    $kept = New-Object System.Collections.Generic.List[object]
    $removed = New-Object System.Collections.Generic.List[object]

    foreach ($row in $Rows) {
        $brandMatch = Matches-Brand -Query $row.query -Name $row.name
        if (-not $brandMatch) {
            $removed.Add([pscustomobject]@{
                query = $row.query
                name = $row.name
                formattedAddress = $row.formattedAddress
                reason = "brand_mismatch"
            }) | Out-Null
            continue
        }

        $nonClubReason = Is-GymNonClubEntity -Query $row.query -Name $row.name
        if ($null -ne $nonClubReason) {
            $removed.Add([pscustomobject]@{
                query = $row.query
                name = $row.name
                formattedAddress = $row.formattedAddress
                reason = "non_club_entity:$nonClubReason"
            }) | Out-Null
            continue
        }

        $kept.Add($row) | Out-Null
    }

    return [pscustomobject]@{
        Kept = [object[]]$kept.ToArray()
        Removed = [object[]]$removed.ToArray()
    }
}

function Clean-Groceries {
    param([object[]]$Rows)

    $kept = New-Object System.Collections.Generic.List[object]
    $removed = New-Object System.Collections.Generic.List[object]

    foreach ($queryGroup in ($Rows | Group-Object query)) {
        $groupRows = @($queryGroup.Group)

        foreach ($row in $groupRows) {
            $supportKeyword = Is-GrocerySupportEntity -Name $row.name
            if (-not $supportKeyword) {
                $kept.Add($row) | Out-Null
                continue
            }

            $parentCandidate = $groupRows |
                Where-Object {
                    $_.placeId -ne $row.placeId -and
                    -not (Is-GrocerySupportEntity -Name $_.name) -and
                    (Matches-Brand -Query $_.query -Name $_.name)
                } |
                Sort-Object {
                    Get-DistanceMiles -LatitudeA $row.latitude -LongitudeA $row.longitude -LatitudeB $_.latitude -LongitudeB $_.longitude
                } |
                Select-Object -First 1

            if ($null -ne $parentCandidate) {
                $distance = Get-DistanceMiles -LatitudeA $row.latitude -LongitudeA $row.longitude -LatitudeB $parentCandidate.latitude -LongitudeB $parentCandidate.longitude
                if ($distance -le 0.5d) {
                    $removed.Add([pscustomobject]@{
                        query = $row.query
                        name = $row.name
                        formattedAddress = $row.formattedAddress
                        reason = "support_entity_collapsed:$supportKeyword"
                        parentName = $parentCandidate.name
                        parentAddress = $parentCandidate.formattedAddress
                        parentDistanceMiles = [Math]::Round($distance, 3)
                    }) | Out-Null
                    continue
                }
            }

            $kept.Add($row) | Out-Null
        }
    }

    return [pscustomobject]@{
        Kept = [object[]]$kept.ToArray()
        Removed = [object[]]$removed.ToArray()
    }
}

$script:BrandAliases = @{
    "Anytime Fitness" = @("Anytime Fitness")
    "Crunch Fitness" = @("Crunch Fitness", "Crunch")
    "LA Fitness" = @("LA Fitness")
    "Life Time" = @("Life Time")
    "Onelife Fitness" = @("Onelife Fitness", "Onelife")
    "Planet Fitness" = @("Planet Fitness")
    "Snap Fitness" = @("Snap Fitness")
    "Workout Anytime" = @("Workout Anytime")
    "YMCA" = @("YMCA")
    "ALDI" = @("ALDI")
    "Kroger" = @("Kroger")
    "Lidl" = @("Lidl")
    "Publix" = @("Publix")
    "Sprouts Farmers Market" = @("Sprouts Farmers Market", "Sprouts")
    "Target Grocery" = @("Target", "Target Grocery")
    "The Fresh Market" = @("The Fresh Market")
    "Trader Joe's" = @("Trader Joe's", "Trader Joes")
    "Walmart" = @("Walmart", "Walmart Supercenter")
    "Whole Foods Market" = @("Whole Foods Market", "Whole Foods")
}

$script:YmcaRejectKeywords = @(
    "head start",
    "academy",
    "soccer field",
    "soccer fields",
    "soccer complex",
    "pool",
    "youth and teen development center",
    "school of medicine",
    "is elanie clark"
)

$script:GrocerySupportKeywords = @(
    "pharmacy",
    "deli",
    "bakery",
    "fuel center",
    "money services",
    "vision center",
    "auto care center",
    "auto center"
)

New-Item -ItemType Directory -Force -Path $ReviewDirectory | Out-Null

if (-not $NoBackup -and (($GymInputPath -eq $GymOutputPath) -or ($GroceryInputPath -eq $GroceryOutputPath))) {
    $runId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"
    $backupDirectory = Join-Path $LegacyDirectoryRoot $runId
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    Copy-Item $GymInputPath $backupDirectory
    Copy-Item $GroceryInputPath $backupDirectory
    Write-Host "Backed up current master lists to $backupDirectory"
}

$gymRows = Read-Jsonl -Path $GymInputPath
$groceryRows = Read-Jsonl -Path $GroceryInputPath

$gymResult = Clean-Gyms -Rows $gymRows
$groceryResult = Clean-Groceries -Rows $groceryRows

Write-Jsonl -Path $GymOutputPath -Rows $gymResult.Kept
Write-Jsonl -Path $GroceryOutputPath -Rows $groceryResult.Kept

($gymResult.Removed | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $ReviewDirectory "gyms-removed.json")
($groceryResult.Removed | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $ReviewDirectory "groceries-removed.json")

$summary = [pscustomobject]@{
    gyms = [pscustomobject]@{
        beforeCount = $gymRows.Count
        afterCount = $gymResult.Kept.Count
        removedCount = $gymResult.Removed.Count
        beforeByQuery = @($gymRows | Group-Object query | Sort-Object Name | ForEach-Object { [pscustomobject]@{ query = $_.Name; count = $_.Count } })
        afterByQuery = @($gymResult.Kept | Group-Object query | Sort-Object Name | ForEach-Object { [pscustomobject]@{ query = $_.Name; count = $_.Count } })
    }
    groceries = [pscustomobject]@{
        beforeCount = $groceryRows.Count
        afterCount = $groceryResult.Kept.Count
        removedCount = $groceryResult.Removed.Count
        beforeByQuery = @($groceryRows | Group-Object query | Sort-Object Name | ForEach-Object { [pscustomobject]@{ query = $_.Name; count = $_.Count } })
        afterByQuery = @($groceryResult.Kept | Group-Object query | Sort-Object Name | ForEach-Object { [pscustomobject]@{ query = $_.Name; count = $_.Count } })
    }
}

($summary | ConvertTo-Json -Depth 8) | Set-Content (Join-Path $ReviewDirectory "cleanup-summary.json")

Write-Host "Saved cleaned gyms to $GymOutputPath"
Write-Host "Saved cleaned groceries to $GroceryOutputPath"
Write-Host "Saved review artifacts to $ReviewDirectory"
