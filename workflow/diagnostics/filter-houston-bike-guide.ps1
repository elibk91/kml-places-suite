[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputKmlPath,

    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

function Get-KmlElementsByTagName {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$LocalName
    )

    return @($Node.GetElementsByTagName($LocalName, "http://www.opengis.net/kml/2.2"))
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

if (-not (Test-Path -LiteralPath $InputKmlPath)) {
    throw "Input KML not found: $InputKmlPath"
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runId = Get-Date -Format "yyyy-MM-ddTHH-mm-ss"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "workflow\out\diagnostics\houston-bike-guide-$runId"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$document = [xml](Get-Content -LiteralPath $InputKmlPath -Raw)
$ns = "http://www.opengis.net/kml/2.2"
$folders = @(Get-KmlElementsByTagName -Node $document -LocalName "Folder")

$allowedFolders = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
[void]$allowedFolders.Add("BIKEWAYS")
[void]$allowedFolders.Add("UNPAVED BIKABLE")

$keptPlacemarks = 0
$removedPlacemarks = 0
$folderSummary = New-Object System.Collections.Generic.List[object]

foreach ($folder in $folders) {
    $folderNameNode = Get-KmlElementsByTagName -Node $folder -LocalName "name" | Select-Object -First 1
    $folderName = if ($null -ne $folderNameNode) { $folderNameNode.InnerText.Trim() } else { "" }

    $placemarks = @($folder.ChildNodes | Where-Object { $_.LocalName -eq "Placemark" })
    if ($placemarks.Count -eq 0) {
        continue
    }

    $keepFolder = $allowedFolders.Contains($folderName)
    $keptInFolder = 0
    $removedInFolder = 0

    for ($i = $placemarks.Count - 1; $i -ge 0; $i--) {
        $placemark = $placemarks[$i]
        if ($keepFolder) {
            $keptInFolder++
            $keptPlacemarks++
            continue
        }

        [void]$folder.RemoveChild($placemark)
        $removedInFolder++
        $removedPlacemarks++
    }

    $folderSummary.Add([pscustomobject]@{
        folder = $folderName
        kept = $keptInFolder
        removed = $removedInFolder
    })
}

$outputKmlPath = Join-Path $OutputDirectory "houston-bike-guide.bikeways-unpaved-bikable.kml"
$summaryPath = Join-Path $OutputDirectory "houston-bike-guide.bikeways-unpaved-bikable.summary.json"

Save-KmlDocument -Document $document -Path $outputKmlPath

[pscustomobject]@{
    inputKmlPath = (Resolve-Path -LiteralPath $InputKmlPath).Path
    outputKmlPath = $outputKmlPath
    keptFolders = @("BIKEWAYS", "UNPAVED BIKABLE")
    keptPlacemarks = $keptPlacemarks
    removedPlacemarks = $removedPlacemarks
    folderSummary = $folderSummary
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Output "Filtered KML: $outputKmlPath"
Write-Output "Summary JSON: $summaryPath"
Write-Output "Kept placemarks: $keptPlacemarks"
Write-Output "Removed placemarks: $removedPlacemarks"
