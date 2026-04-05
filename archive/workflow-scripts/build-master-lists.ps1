[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [string]$RunId,

    [string]$RunOutputDirectory,

    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptsRoot = Split-Path -Parent $scriptDirectory
$repoRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common.ps1")

if (-not $env:GoogleMaps__ApiKey) {
    throw "GoogleMaps__ApiKey is required."
}

$projectPath = Join-Path $repoRoot "MasterListBuilder.Console\MasterListBuilder.Console.csproj"

if (-not $RunId) {
    $RunId = Get-Date -Format "yyyyMMdd-HHmmss"
}

if (-not $RunOutputDirectory) {
    $RunOutputDirectory = Join-Path $scriptsRoot "out\legacy\build-master-lists\$RunId"
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $RunOutputDirectory "master-lists"
}

$traceDirectory = Join-Path $RunOutputDirectory "trace"

Write-Section -Title "Build Master Lists"
# Solution builds are unstable from inside these Windows PowerShell scripts, but direct project builds are reliable.
# Build only the console app this legacy script runs, then execute it with --no-build.
Invoke-DotnetCommand -Description "Building master list builder project" -Arguments @("build", (Resolve-DisplayPath -Path $projectPath), "-v", "minimal") -FailureMessage "Master list builder build failed."
# Legacy generation still emits its own trace artifacts, but they belong under legacy outputs instead of the active proof surface.
Initialize-TraceArtifacts -RepoRoot $repoRoot -TraceDirectory $traceDirectory
try {
    Write-ParameterTrace -Values @{
        ConfigPath = (Resolve-DisplayPath -Path $ConfigPath)
        OutputDirectory = (Resolve-DisplayPath -Path $OutputDirectory)
        RunId = $RunId
        RunOutputDirectory = (Resolve-DisplayPath -Path $RunOutputDirectory)
    }
    Write-ProjectTrace -ProjectPath $projectPath -Description "master list builder"
    New-Item -ItemType Directory -Force -Path (Resolve-DisplayPath -Path $OutputDirectory) | Out-Null
    Invoke-DotnetCommand -Description "Running master list builder" -Arguments @("run", "--project", $projectPath, "--no-build", "--", "--config", (Resolve-DisplayPath -Path $ConfigPath), "--output-dir", (Resolve-DisplayPath -Path $OutputDirectory)) -FailureMessage "Master list builder failed."
}
finally {
    Complete-TraceArtifacts
}
