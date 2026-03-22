[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,

    [Parameter(Mandatory = $true)]
    [double]$Latitude,

    [Parameter(Mandatory = $true)]
    [double]$Longitude,

    [double]$RadiusMiles = 0.5,

    [int]$TopPerCategory = 5
)

$ErrorActionPreference = "Stop"

function Get-DistanceMiles {
    param(
        [double]$Lat1,
        [double]$Lon1,
        [double]$Lat2,
        [double]$Lon2
    )

    $degreesToRadians = [math]::PI / 180.0
    $cosLatitude = [math]::Cos($Lat2 * $degreesToRadians)
    $latMiles = ($Lat1 - $Lat2) * 69.0
    $lonMiles = ($Lon1 - $Lon2) * 69.0 * $cosLatitude
    return [math]::Sqrt(($latMiles * $latMiles) + ($lonMiles * $lonMiles))
}

$request = Get-Content $RequestPath | ConvertFrom-Json
$locations = @($request.locations)
$categories = $locations.category | Sort-Object -Unique

Write-Host ("Coordinate: {0}, {1}" -f $Latitude, $Longitude)
Write-Host ("RadiusMiles: {0}" -f $RadiusMiles)
Write-Host ""

$missingCategories = New-Object System.Collections.Generic.List[string]

foreach ($category in $categories) {
    $nearest = $locations |
        Where-Object { $_.category -eq $category } |
        ForEach-Object {
            [pscustomobject]@{
                category = $category
                label = if ($_.PSObject.Properties.Match('label').Count -gt 0 -and $_.label) { $_.label } else { $_.category }
                latitude = $_.latitude
                longitude = $_.longitude
                distanceMiles = Get-DistanceMiles -Lat1 $Latitude -Lon1 $Longitude -Lat2 $_.latitude -Lon2 $_.longitude
            }
        } |
        Sort-Object distanceMiles |
        Select-Object -First $TopPerCategory

    $withinRadius = @($nearest | Where-Object { $_.distanceMiles -le $RadiusMiles })
    if ($withinRadius.Count -eq 0) {
        $missingCategories.Add($category)
    }

    Write-Host "[$category]"
    $nearest |
        Select-Object label, @{ Name = "distanceMiles"; Expression = { "{0:N3}" -f $_.distanceMiles } }, latitude, longitude |
        Format-Table -AutoSize |
        Out-String |
        Write-Host
}

if ($missingCategories.Count -eq 0) {
    Write-Host "All categories have at least one point within radius."
}
else {
    Write-Host ("Missing within radius: {0}" -f ($missingCategories -join ", "))
}
