[CmdletBinding()]
param(
    [string]$ArcSourceDirectory = ".\scripts\in\arc-sources",

    [string]$ConfigPath = ".\config\authority\arc-source-cleanup.json",

    [string]$LegacyRootDirectory = ".\scripts\in\arc-sources\legacy",

    [string]$RunId,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Xml.Linq

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory

function Resolve-AbsolutePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Get-RunId {
    if ($RunId) {
        return $RunId
    }

    return Get-Date -Format "yyyy-MM-ddTHH-mm-ss"
}

function Sanitize-XmlText {
    param([string]$RawText)

    $builder = [System.Text.StringBuilder]::new($RawText.Length)
    foreach ($character in $RawText.ToCharArray()) {
        $codePoint = [int][char]$character
        if ($codePoint -eq 0x9 -or $codePoint -eq 0xA -or $codePoint -eq 0xD -or ($codePoint -ge 0x20 -and $codePoint -le 0xD7FF) -or ($codePoint -ge 0xE000 -and $codePoint -le 0xFFFD)) {
            [void]$builder.Append($character)
        }
    }

    return $builder.ToString()
}

function Get-KmlDocument {
    param([string]$Path)

    $extension = [System.IO.Path]::GetExtension($Path)
    if ($extension.Equals(".kmz", [System.StringComparison]::OrdinalIgnoreCase)) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entry = $archive.GetEntry("doc.kml")
            if ($null -eq $entry) {
                throw "KMZ '$Path' does not contain doc.kml."
            }

            $entryStream = $entry.Open()
            try {
                $reader = [System.IO.StreamReader]::new($entryStream, [System.Text.Encoding]::UTF8, $true)
                try {
                    $rawText = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $entryStream.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    else {
        $rawText = [System.IO.File]::ReadAllText($Path)
    }

    $sanitized = Sanitize-XmlText -RawText $rawText
    return [System.Xml.Linq.XDocument]::Parse($sanitized)
}

function Save-KmlDocument {
    param(
        [System.Xml.Linq.XDocument]$Document,
        [string]$Path
    )

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"

    $extension = [System.IO.Path]::GetExtension($Path)
    if ($extension.Equals(".kmz", [System.StringComparison]::OrdinalIgnoreCase)) {
        $tempDirectory = Join-Path $env:TEMP ("arc-clean-" + [guid]::NewGuid().ToString("N"))
        [System.IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
        try {
            $docPath = Join-Path $tempDirectory "doc.kml"
            $writer = [System.Xml.XmlWriter]::Create($docPath, $settings)
            try {
                $Document.Save($writer)
            }
            finally {
                $writer.Dispose()
            }

            if (Test-Path $Path) {
                Remove-Item $Path -Force
            }

            [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDirectory, $Path)
        }
        finally {
            if (Test-Path $tempDirectory) {
                Remove-Item $tempDirectory -Recurse -Force
            }
        }
    }
    else {
        $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
        try {
            $Document.Save($writer)
        }
        finally {
            $writer.Dispose()
        }
    }
}

function Get-MetadataValue {
    param(
        [hashtable]$Metadata,
        [string]$Field
    )

    if ($Metadata.ContainsKey($Field)) {
        return [string]$Metadata[$Field]
    }

    return ""
}

function Normalize-Value {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return $Value.Replace("-", " ").Replace("_", " ").Trim().ToLowerInvariant()
}

function Get-PlacemarkMetadata {
    param([System.Xml.Linq.XElement]$Placemark)

    $metadata = @{}

    $nameElement = $Placemark.Elements() | Where-Object { $_.Name.LocalName -eq "name" } | Select-Object -First 1
    if ($null -ne $nameElement -and -not [string]::IsNullOrWhiteSpace($nameElement.Value)) {
        $metadata["Name"] = $nameElement.Value.Trim()
    }

    $descriptionElement = $Placemark.Elements() | Where-Object { $_.Name.LocalName -eq "description" } | Select-Object -First 1
    if ($null -ne $descriptionElement -and -not [string]::IsNullOrWhiteSpace($descriptionElement.Value)) {
        $rowPattern = '<tr>\s*<td>(?<key>.*?)</td>\s*<td>(?<value>.*?)</td>\s*</tr>'
        foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($descriptionElement.Value, $rowPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            $key = [System.Net.WebUtility]::HtmlDecode($match.Groups["key"].Value).Trim()
            $value = [System.Net.WebUtility]::HtmlDecode($match.Groups["value"].Value).Trim()
            if (-not [string]::IsNullOrWhiteSpace($key)) {
                $metadata[$key] = $value
            }
        }
    }

    foreach ($element in $Placemark.Descendants()) {
        if ($element.Name.LocalName -eq "Data") {
            $key = [string]$element.Attribute("name")
            $valueElement = $element.Elements() | Where-Object { $_.Name.LocalName -eq "value" } | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($key) -and $null -ne $valueElement -and -not [string]::IsNullOrWhiteSpace($valueElement.Value)) {
                $metadata[$key.Trim()] = $valueElement.Value.Trim()
            }
        }

        if ($element.Name.LocalName -eq "SimpleData") {
            $key = [string]$element.Attribute("name")
            if (-not [string]::IsNullOrWhiteSpace($key) -and -not [string]::IsNullOrWhiteSpace($element.Value)) {
                $metadata[$key.Trim()] = $element.Value.Trim()
            }
        }
    }

    return $metadata
}

function Test-Matcher {
    param(
        [hashtable]$Metadata,
        [pscustomobject]$Matcher
    )

    $value = Normalize-Value -Value (Get-MetadataValue -Metadata $Metadata -Field $Matcher.field)

    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    if ($Matcher.PSObject.Properties.Name -contains "equalsAny") {
        $equalsAny = @($Matcher.equalsAny | ForEach-Object { Normalize-Value -Value ([string]$_) })
        if ($equalsAny -contains $value) {
            return $true
        }
    }

    if ($Matcher.PSObject.Properties.Name -contains "containsAny") {
        foreach ($item in @($Matcher.containsAny | ForEach-Object { Normalize-Value -Value ([string]$_) })) {
            if (-not [string]::IsNullOrWhiteSpace($item) -and $value.IndexOf($item, [System.StringComparison]::Ordinal) -ge 0) {
                return $true
            }
        }
    }

    return $false
}

function Test-PlacemarkKeep {
    param(
        [hashtable]$Metadata,
        [pscustomobject]$SourceRule
    )

    if ($SourceRule.PSObject.Properties.Name -contains "excludeIfAny") {
        foreach ($matcher in $SourceRule.excludeIfAny) {
            if (Test-Matcher -Metadata $Metadata -Matcher $matcher) {
                return $false
            }
        }
    }

    if ($SourceRule.PSObject.Properties.Name -contains "keepIfAny") {
        foreach ($matcher in $SourceRule.keepIfAny) {
            if (Test-Matcher -Metadata $Metadata -Matcher $matcher) {
                return $true
            }
        }

        return $false
    }

    return $true
}

function Get-GeometrySummary {
    param([System.Xml.Linq.XElement]$Placemark)

    $geometryNames = $Placemark.Descendants() |
        Where-Object { $_.Name.LocalName -in @("Point", "LineString", "Polygon", "MultiGeometry") } |
        ForEach-Object { $_.Name.LocalName }

    return @($geometryNames | Select-Object -Unique)
}

$resolvedArcSourceDirectory = Resolve-AbsolutePath -Path $ArcSourceDirectory
$resolvedConfigPath = Resolve-AbsolutePath -Path $ConfigPath
$resolvedLegacyRootDirectory = Resolve-AbsolutePath -Path $LegacyRootDirectory
$effectiveRunId = Get-RunId
$legacyRunDirectory = Join-Path $resolvedLegacyRootDirectory $effectiveRunId
$reportPath = Join-Path $repoRoot ("scripts\out\runs\arc-source-cleanup-$effectiveRunId-report.json")

if (-not (Test-Path $resolvedArcSourceDirectory)) {
    throw "Arc source directory '$resolvedArcSourceDirectory' does not exist."
}

if (-not (Test-Path $resolvedConfigPath)) {
    throw "Cleanup config '$resolvedConfigPath' does not exist."
}

$config = Get-Content $resolvedConfigPath -Raw | ConvertFrom-Json
$sourceRuleMap = @{}
foreach ($source in $config.sources) {
    $sourceRuleMap[$source.fileName] = $source
}

$sourceFiles = Get-ChildItem -Path $resolvedArcSourceDirectory -File | Sort-Object Name
$reportEntries = New-Object System.Collections.Generic.List[object]

if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $legacyRunDirectory | Out-Null
}

foreach ($file in $sourceFiles) {
    $document = Get-KmlDocument -Path $file.FullName
    $placemarks = @($document.Descendants() | Where-Object { $_.Name.LocalName -eq "Placemark" })
    $statusFieldCounts = @{}
    $statusValueCounts = @{}
    $geometryCounts = @{}
    $configured = $sourceRuleMap.ContainsKey($file.Name)
    $rule = if ($configured) { $sourceRuleMap[$file.Name] } else { $null }
    $removed = 0

    foreach ($placemark in $placemarks) {
        $metadata = Get-PlacemarkMetadata -Placemark $placemark

        foreach ($key in $metadata.Keys) {
            $normalizedKey = Normalize-Value -Value $key
            if ($normalizedKey.IndexOf("status", [System.StringComparison]::Ordinal) -ge 0 -or $normalizedKey -eq "project type" -or $normalizedKey -eq "plan") {
                if (-not $statusFieldCounts.ContainsKey($key)) {
                    $statusFieldCounts[$key] = 0
                }

                $statusFieldCounts[$key]++
                $statusKey = "$key=$($metadata[$key])"
                if (-not $statusValueCounts.ContainsKey($statusKey)) {
                    $statusValueCounts[$statusKey] = 0
                }

                $statusValueCounts[$statusKey]++
            }
        }

        foreach ($geometryName in Get-GeometrySummary -Placemark $placemark) {
            if (-not $geometryCounts.ContainsKey($geometryName)) {
                $geometryCounts[$geometryName] = 0
            }

            $geometryCounts[$geometryName]++
        }

        if ($configured -and -not (Test-PlacemarkKeep -Metadata $metadata -SourceRule $rule)) {
            $placemark.Remove()
            $removed++
        }
    }

    $changed = $configured -and $removed -gt 0
    if ($changed -and -not $DryRun) {
        $legacyPath = Join-Path $legacyRunDirectory $file.Name
        Copy-Item $file.FullName $legacyPath -Force
        Save-KmlDocument -Document $document -Path $file.FullName
    }

    $reportEntries.Add([pscustomobject]@{
        fileName = $file.Name
        configured = $configured
        changed = $changed
        removedPlacemarkCount = $removed
        placemarkCountBefore = $placemarks.Count
        placemarkCountAfter = $placemarks.Count - $removed
        geometryCounts = [pscustomobject]$geometryCounts
        statusFieldCounts = [pscustomobject]$statusFieldCounts
        topStatusValues = @($statusValueCounts.GetEnumerator() |
            Sort-Object Value -Descending |
            Select-Object -First 20 |
            ForEach-Object {
                [pscustomobject]@{
                    key = $_.Key
                    count = $_.Value
                }
            })
    }) | Out-Null
}

$reportDirectory = Split-Path -Parent $reportPath
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$reportEntries | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding utf8

Write-Host "RunId: $effectiveRunId"
Write-Host "Report: $reportPath"
if (-not $DryRun) {
    Write-Host "Legacy backup directory: $legacyRunDirectory"
}

$reportEntries |
    Select-Object fileName, configured, changed, removedPlacemarkCount, placemarkCountBefore, placemarkCountAfter |
    Format-Table -AutoSize
