[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$arcSourceRoot = Join-Path $repoRoot "in\arc-sources"
$parksTrailsRoot = Join-Path $arcSourceRoot "parks-trails"
$martaRoot = Join-Path $arcSourceRoot "marta"
$runId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"
$legacyRoot = Join-Path $arcSourceRoot "legacy\$runId"

New-Item -ItemType Directory -Path $legacyRoot -Force | Out-Null
New-Item -ItemType Directory -Path $parksTrailsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $martaRoot -Force | Out-Null

$sourceFiles = @(
    "City_of_Atlanta_Parks.kml",
    "Kennesaw_Mountain_Trails_July_2015_FS.kml",
    "MARTA_Rail_Stations_-3250756205123355367.kmz",
    "Official_Atlanta_Beltline_Trails.kml",
    "Park_Layer_-4860561661912367000.kmz",
    "Parks (1).kml",
    "Parks (2).kml",
    "Parks.kml",
    "Parks_Layer.kml",
    "Parks_Trails_assets.kml",
    "Trail_Plan_Inventory_-7693997307718543817.kmz",
    "Trails.kml",
    "Trails_-8511739637663957148.kmz"
)

$renameMap = [ordered]@{
    "City_of_Atlanta_Parks.kml" = "city_of_atlanta_parks.kml"
    "Kennesaw_Mountain_Trails_July_2015_FS.kml" = "kennesaw_mountain_national_battlefield_park_trails.kml"
    "MARTA_Rail_Stations_-3250756205123355367.kmz" = "atlanta_regional_commission_marta_rail_stations.kmz"
    "Official_Atlanta_Beltline_Trails.kml" = "atlanta_beltline_official_trails.kml"
    "Park_Layer_-4860561661912367000.kmz" = "city_of_tucker_parks.kmz"
    "Parks (1).kml" = "city_of_sandy_springs_parks.kml"
    "Parks (2).kml" = "city_of_brookhaven_parks.kml"
    "Parks.kml" = "dekalb_county_parks.kml"
    "Parks_Layer.kml" = "city_of_dunwoody_parks.kml"
    "Parks_Trails_assets.kml" = "city_of_sandy_springs_park_trails_assets.kml"
    "Trail_Plan_Inventory_-7693997307718543817.kmz" = "atlanta_regional_commission_trail_plan_inventory.kmz"
    "Trails.kml" = "cobb_county_trails.kml"
    "Trails_-8511739637663957148.kmz" = "path_foundation_trails.kmz"
}

$trailPlanKeepNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    "Peachtree Creek Greenway",
    "Proctor Creek Greenway (High)",
    "South River Trail",
    "Southtowne Trail (High)",
    "Whetstone Creek Trail"
) | ForEach-Object { [void]$trailPlanKeepNames.Add($_) }

function Get-KmlNamespaceManager {
    param([xml]$XmlDocument)

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($XmlDocument.NameTable)
    $namespaceManager.AddNamespace("k", "http://www.opengis.net/kml/2.2")
    return $namespaceManager
}

function Get-KmlElementsByTagName {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$LocalName
    )

    return @($Node.GetElementsByTagName($LocalName, "http://www.opengis.net/kml/2.2"))
}

function Get-KmlDocument {
    param([string]$Path)

    $content = Get-Content -Path $Path -Raw
    return [xml]$content
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

function Get-DestinationPath {
    param([string]$SourceFileName)

    $destinationFileName = $renameMap[$SourceFileName]
    if ($SourceFileName -like "MARTA_*") {
        return Join-Path $martaRoot $destinationFileName
    }

    return Join-Path $parksTrailsRoot $destinationFileName
}

function Get-SimpleDataValue {
    param(
        [System.Xml.XmlElement]$Placemark,
        [string]$Name
    )

    foreach ($node in (Get-KmlElementsByTagName -Node $Placemark -LocalName "SimpleData")) {
        if ($node.GetAttribute("name") -eq $Name) {
            return $node.InnerText
        }
    }

    return ""
}

function Get-DescriptionFieldValue {
    param(
        [System.Xml.XmlElement]$Placemark,
        [string]$FieldName
    )

    $descriptionNode = (Get-KmlElementsByTagName -Node $Placemark -LocalName "description" | Select-Object -First 1)
    if ($null -eq $descriptionNode) {
        return ""
    }

    $match = [regex]::Match(
        $descriptionNode.InnerText,
        "<td>$([regex]::Escape($FieldName))</td><td>(.*?)</td>",
        [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    if (-not $match.Success) {
        return ""
    }

    return $match.Groups[1].Value
}

function Get-NodeInnerText {
    param([System.Xml.XmlNode]$Node)

    if ($null -eq $Node) {
        return ""
    }

    return $Node.InnerText
}

function Remove-Placemarks {
    param(
        [xml]$Document,
        [scriptblock]$ShouldRemove
    )

    $placemarks = @(Get-KmlElementsByTagName -Node $Document -LocalName "Placemark")

    foreach ($placemark in $placemarks) {
        if (& $ShouldRemove $placemark) {
            [void]$placemark.ParentNode.RemoveChild($placemark)
        }
    }
}

function Write-KmzFromSource {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [scriptblock]$MutateDocument
    )

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("kml-clean-" + [System.Guid]::NewGuid().ToString("N"))
    $tempZipSource = Join-Path ([System.IO.Path]::GetTempPath()) ("kml-clean-source-" + [System.Guid]::NewGuid().ToString("N") + ".zip")
    $tempZipOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("kml-clean-output-" + [System.Guid]::NewGuid().ToString("N") + ".zip")
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    try {
        Copy-Item -Path $SourcePath -Destination $tempZipSource -Force
        Expand-Archive -Path $tempZipSource -DestinationPath $tempRoot -Force
        $docPath = Join-Path $tempRoot "doc.kml"
        $document = Get-KmlDocument -Path $docPath
        & $MutateDocument $document
        Save-KmlDocument -Document $document -Path $docPath

        if (Test-Path $DestinationPath) {
            Remove-Item -Path $DestinationPath -Force
        }

        if (Test-Path $tempZipOutput) {
            Remove-Item -Path $tempZipOutput -Force
        }

        Compress-Archive -Path (Join-Path $tempRoot "*") -DestinationPath $tempZipOutput -CompressionLevel Optimal
        Move-Item -Path $tempZipOutput -Destination $DestinationPath -Force
    }
    finally {
        if (Test-Path $tempRoot) {
            Remove-Item -Path $tempRoot -Recurse -Force
        }

        if (Test-Path $tempZipSource) {
            Remove-Item -Path $tempZipSource -Force
        }

        if (Test-Path $tempZipOutput) {
            Remove-Item -Path $tempZipOutput -Force
        }
    }
}

foreach ($sourceFile in $sourceFiles) {
    Copy-Item -Path (Join-Path $arcSourceRoot $sourceFile) -Destination (Join-Path $legacyRoot $sourceFile) -Force
}

$cityAtlantaSource = Join-Path $arcSourceRoot "City_of_Atlanta_Parks.kml"
$cityAtlantaDestination = Get-DestinationPath -SourceFileName "City_of_Atlanta_Parks.kml"
$cityAtlantaDocument = Get-KmlDocument -Path $cityAtlantaSource
Remove-Placemarks -Document $cityAtlantaDocument -ShouldRemove {
    param($placemark)
    (Get-SimpleDataValue -Placemark $placemark -Name "AllParks_Fields_Park_Status") -ne "Open"
}
Save-KmlDocument -Document $cityAtlantaDocument -Path $cityAtlantaDestination

$beltlineSource = Join-Path $arcSourceRoot "Official_Atlanta_Beltline_Trails.kml"
$beltlineDestination = Get-DestinationPath -SourceFileName "Official_Atlanta_Beltline_Trails.kml"
$beltlineDocument = Get-KmlDocument -Path $beltlineSource
Remove-Placemarks -Document $beltlineDocument -ShouldRemove {
    param($placemark)
    $useStatus = Get-SimpleDataValue -Placemark $placemark -Name "usestatus"
    $status2 = Get-SimpleDataValue -Placemark $placemark -Name "status2"
    $useStatus -ne "Open Paved" -or $status2 -ne "Completed and Open"
}
Save-KmlDocument -Document $beltlineDocument -Path $beltlineDestination

$parksLayerSource = Join-Path $arcSourceRoot "Parks_Layer.kml"
$parksLayerDestination = Get-DestinationPath -SourceFileName "Parks_Layer.kml"
$parksLayerDocument = Get-KmlDocument -Path $parksLayerSource
Remove-Placemarks -Document $parksLayerDocument -ShouldRemove {
    param($placemark)
    (Get-SimpleDataValue -Placemark $placemark -Name "Status") -ne "COMPLETED"
}
Save-KmlDocument -Document $parksLayerDocument -Path $parksLayerDestination

$dekalbParksSource = Join-Path $arcSourceRoot "Parks.kml"
$dekalbParksDestination = Get-DestinationPath -SourceFileName "Parks.kml"
$dekalbParksDocument = Get-KmlDocument -Path $dekalbParksSource
Remove-Placemarks -Document $dekalbParksDocument -ShouldRemove {
    param($placemark)
    (Get-SimpleDataValue -Placemark $placemark -Name "FCODE") -ne "Park"
}
Save-KmlDocument -Document $dekalbParksDocument -Path $dekalbParksDestination

$cobbTrailsSource = Join-Path $arcSourceRoot "Trails.kml"
$cobbTrailsDestination = Get-DestinationPath -SourceFileName "Trails.kml"
$cobbTrailsDocument = Get-KmlDocument -Path $cobbTrailsSource
Remove-Placemarks -Document $cobbTrailsDocument -ShouldRemove {
    param($placemark)
    $name = Get-NodeInnerText -Node ((Get-KmlElementsByTagName -Node $placemark -LocalName "name" | Select-Object -First 1))
    $status = Get-SimpleDataValue -Placemark $placemark -Name "STATUS"
    $facility = Get-SimpleDataValue -Placemark $placemark -Name "FACILITY"

    if ($status -in @("Programmed", "Under Construction", "No Public Map Display")) {
        return $true
    }

    if ($facility -eq "Bike Lane") {
        return $true
    }

    return $name -match "Bike Lane|Service Road|Streetscape|Complete Streets"
}
Save-KmlDocument -Document $cobbTrailsDocument -Path $cobbTrailsDestination

$pathTrailsSource = Join-Path $arcSourceRoot "Trails_-8511739637663957148.kmz"
$pathTrailsDestination = Get-DestinationPath -SourceFileName "Trails_-8511739637663957148.kmz"
Write-KmzFromSource -SourcePath $pathTrailsSource -DestinationPath $pathTrailsDestination -MutateDocument {
    param($document)
    Remove-Placemarks -Document $document -ShouldRemove {
        param($placemark)
        $status = Get-DescriptionFieldValue -Placemark $placemark -FieldName "Status"
        $status -notin @("built", "trail")
    }
}

$trailPlanInputPath = Join-Path $arcSourceRoot "Trail_Plan_Inventory_-7693997307718543817.cleaned.kmz"
if (-not (Test-Path $trailPlanInputPath)) {
    throw "Expected cleaned Trail Plan KMZ at '$trailPlanInputPath'."
}

$trailPlanDestination = Get-DestinationPath -SourceFileName "Trail_Plan_Inventory_-7693997307718543817.kmz"
Write-KmzFromSource -SourcePath $trailPlanInputPath -DestinationPath $trailPlanDestination -MutateDocument {
    param($document)
    Remove-Placemarks -Document $document -ShouldRemove {
        param($placemark)
        $name = Get-NodeInnerText -Node ((Get-KmlElementsByTagName -Node $placemark -LocalName "name" | Select-Object -First 1))
        -not $trailPlanKeepNames.Contains($name)
    }
}

$copyOnlyFiles = @(
    "Kennesaw_Mountain_Trails_July_2015_FS.kml",
    "MARTA_Rail_Stations_-3250756205123355367.kmz",
    "Park_Layer_-4860561661912367000.kmz",
    "Parks (1).kml",
    "Parks (2).kml",
    "Parks_Trails_assets.kml"
)

foreach ($fileName in $copyOnlyFiles) {
    Copy-Item -Path (Join-Path $arcSourceRoot $fileName) -Destination (Get-DestinationPath -SourceFileName $fileName) -Force
}

$filesToRemove = @(
    $sourceFiles +
    "Trail_Plan_Inventory_-7693997307718543817.cleaned.kmz"
) | Sort-Object -Unique

foreach ($fileName in $filesToRemove) {
    $path = Join-Path $arcSourceRoot $fileName
    if (Test-Path $path) {
        $item = Get-Item -Path $path -Force
        if (-not $item.PSIsContainer) {
            $item.IsReadOnly = $false
        }
        Remove-Item -Path $path -Force
    }
}

Write-Output "Legacy backup: $legacyRoot"
