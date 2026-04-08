[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputKmzPath,

    [string]$OutputDirectory,

    [switch]$IncludeProposedOffStreet,

    [double]$North,

    [double]$South,

    [double]$West,

    [double]$East
)

$ErrorActionPreference = "Stop"

function Get-KmlElementsByTagName {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$LocalName
    )

    return @($Node.GetElementsByTagName($LocalName, "http://www.opengis.net/kml/2.2"))
}

function Get-DescriptionFields {
    param([System.Xml.XmlElement]$Placemark)

    $descriptionNode = Get-KmlElementsByTagName -Node $Placemark -LocalName "description" | Select-Object -First 1
    $fields = @{}

    if ($null -eq $descriptionNode) {
        return $fields
    }

    foreach ($match in [regex]::Matches($descriptionNode.InnerText, "<tr><td>([^<]+)</td><td>(.*?)</td></tr>", [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        $fields[$match.Groups[1].Value.Trim()] = $match.Groups[2].Value.Trim()
    }

    return $fields
}

function Test-IsOffStreet {
    param([hashtable]$Fields)

    return $Fields["HC_Class"] -eq "Off-Street" -or $Fields["LC_Class"] -eq "Off-Street"
}

function Test-IsExisting {
    param([hashtable]$Fields)

    return $Fields["HC_Status"] -eq "Existing" -or $Fields["LC_Status"] -eq "Existing"
}

function Test-HasBoundsFilter {
    return $PSBoundParameters.ContainsKey("North") -or
        $PSBoundParameters.ContainsKey("South") -or
        $PSBoundParameters.ContainsKey("West") -or
        $PSBoundParameters.ContainsKey("East")
}

function Test-CoordinateInsideBounds {
    param(
        [double]$Longitude,
        [double]$Latitude
    )

    return $Latitude -ge $South -and $Latitude -le $North -and $Longitude -ge $West -and $Longitude -le $East
}

function Test-PlacemarkIntersectsBounds {
    param([System.Xml.XmlElement]$Placemark)

    $coordinateNodes = Get-KmlElementsByTagName -Node $Placemark -LocalName "coordinates"
    foreach ($coordinateNode in $coordinateNodes) {
        foreach ($token in ($coordinateNode.InnerText -split "\s+")) {
            if ([string]::IsNullOrWhiteSpace($token)) {
                continue
            }

            $parts = $token.Split(',')
            if ($parts.Length -lt 2) {
                continue
            }

            $longitude = 0.0
            $latitude = 0.0
            if (-not [double]::TryParse($parts[0], [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$longitude)) {
                continue
            }

            if (-not [double]::TryParse($parts[1], [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$latitude)) {
                continue
            }

            if (Test-CoordinateInsideBounds -Longitude $longitude -Latitude $latitude) {
                return $true
            }
        }
    }

    return $false
}

function Save-KmlDocument {
    param(
        [xml]$Document,
        [string]$Path
    )

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)

    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $InputKmzPath)) {
    throw "Input KMZ not found: $InputKmzPath"
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "workflow\out\diagnostics\houston-bikeways-$runId"
}

if (Test-HasBoundsFilter) {
    if (-not ($PSBoundParameters.ContainsKey("North") -and $PSBoundParameters.ContainsKey("South") -and $PSBoundParameters.ContainsKey("West") -and $PSBoundParameters.ContainsKey("East"))) {
        throw "North, South, West, and East must all be provided when applying a bounds filter."
    }

    if ($South -gt $North) {
        throw "South cannot be greater than North."
    }

    if ($West -gt $East) {
        throw "West cannot be greater than East."
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("houston-bikeways-" + [System.Guid]::NewGuid().ToString("N"))
$tempZipPath = Join-Path ([System.IO.Path]::GetTempPath()) ("houston-bikeways-" + [System.Guid]::NewGuid().ToString("N") + ".zip")

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Copy-Item -LiteralPath $InputKmzPath -Destination $tempZipPath -Force
    Expand-Archive -LiteralPath $tempZipPath -DestinationPath $tempRoot -Force

    $docPath = Join-Path $tempRoot "doc.kml"
    if (-not (Test-Path -LiteralPath $docPath)) {
        throw "KMZ did not contain doc.kml: $InputKmzPath"
    }

    $originalDocument = [xml](Get-Content -LiteralPath $docPath -Raw)
    $filteredDocument = [xml](Get-Content -LiteralPath $docPath -Raw)

    $placemarks = @(Get-KmlElementsByTagName -Node $originalDocument -LocalName "Placemark")
    $filteredPlacemarks = @(Get-KmlElementsByTagName -Node $filteredDocument -LocalName "Placemark")

    if ($placemarks.Count -ne $filteredPlacemarks.Count) {
        throw "Placemark count mismatch while loading source document."
    }

    $summary = [ordered]@{
        inputKmzPath = (Resolve-Path -LiteralPath $InputKmzPath).Path
        outputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
        includeProposedOffStreet = [bool]$IncludeProposedOffStreet
        boundsApplied = (Test-HasBoundsFilter)
        bounds = if (Test-HasBoundsFilter) {
            [ordered]@{
                north = $North
                south = $South
                west = $West
                east = $East
            }
        }
        else {
            $null
        }
        totalPlacemarks = $placemarks.Count
        keptExistingOffStreet = 0
        keptAdditionalOffStreetNonExisting = 0
        removedOnStreetOrUnknown = 0
        removedOutsideBounds = 0
    }

    $keptRows = New-Object System.Collections.Generic.List[object]
    $removedRows = New-Object System.Collections.Generic.List[object]

    for ($i = $filteredPlacemarks.Count - 1; $i -ge 0; $i--) {
        $originalPlacemark = $placemarks[$i]
        $filteredPlacemark = $filteredPlacemarks[$i]
        $fields = Get-DescriptionFields -Placemark $originalPlacemark
        $nameNode = Get-KmlElementsByTagName -Node $originalPlacemark -LocalName "name" | Select-Object -First 1
        $name = if ($null -ne $nameNode) { $nameNode.InnerText } else { "" }

        $isOffStreet = Test-IsOffStreet -Fields $fields
        $isExisting = Test-IsExisting -Fields $fields
        $insideBounds = (-not (Test-HasBoundsFilter)) -or (Test-PlacemarkIntersectsBounds -Placemark $originalPlacemark)
        $keep = $isOffStreet -and ($IncludeProposedOffStreet -or $isExisting) -and $insideBounds

        $row = [pscustomobject]@{
            Name = $name
            HC_Status = $fields["HC_Status"]
            HC_Class = $fields["HC_Class"]
            LC_Status = $fields["LC_Status"]
            LC_Class = $fields["LC_Class"]
            Description = $fields["Description"]
        }

        if ($keep) {
            $keptRows.Add($row)
            if ($isExisting) {
                $summary.keptExistingOffStreet++
            }
            else {
                $summary.keptAdditionalOffStreetNonExisting++
            }

            continue
        }

        $removedRows.Add($row)
        if (-not $insideBounds) {
            $summary.removedOutsideBounds++
        }
        else {
            $summary.removedOnStreetOrUnknown++
        }
        [void]$filteredPlacemark.ParentNode.RemoveChild($filteredPlacemark)
    }

    $filteredKmlPath = Join-Path $OutputDirectory "houston-bikeways.off-street-existing.kml"
    if ($IncludeProposedOffStreet) {
        $filteredKmlPath = Join-Path $OutputDirectory "houston-bikeways.off-street-all-statuses.kml"
    }

    $variantName = "off-street-existing"
    if ($IncludeProposedOffStreet) {
        $variantName = "off-street-all-statuses"
    }

    $summaryPath = Join-Path $OutputDirectory ("houston-bikeways.{0}.summary.json" -f $variantName)
    $keptPath = Join-Path $OutputDirectory ("houston-bikeways.{0}.kept.json" -f $variantName)
    $removedPath = Join-Path $OutputDirectory ("houston-bikeways.{0}.removed.json" -f $variantName)

    Save-KmlDocument -Document $filteredDocument -Path $filteredKmlPath
    $summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    $keptRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $keptPath -Encoding UTF8
    $removedRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $removedPath -Encoding UTF8

    Write-Output "Filtered KML: $filteredKmlPath"
    Write-Output "Summary JSON: $summaryPath"
    Write-Output "Kept placemarks: $($summary.keptExistingOffStreet + $summary.keptAdditionalOffStreetNonExisting)"
    Write-Output "Removed placemarks: $($summary.removedOnStreetOrUnknown)"

    if (-not $IncludeProposedOffStreet) {
        & $PSCommandPath -InputKmzPath $InputKmzPath -OutputDirectory $OutputDirectory -IncludeProposedOffStreet
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $tempZipPath) {
        Remove-Item -LiteralPath $tempZipPath -Force
    }
}
