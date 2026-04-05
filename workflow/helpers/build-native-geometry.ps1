[CmdletBinding()]
param(
    [string]$Configuration = "Release",

    [string]$BuildRoot = ".\.native\build\platform-native-kml_geometry_native",

    [string]$VcpkgRoot = ".\.native\vcpkg",

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$workflowRoot = Split-Path -Parent $scriptDirectory
$repoRoot = Split-Path -Parent $workflowRoot
$cmakeExe = "C:\Program Files\CMake\bin\cmake.exe"
$sourceRoot = Join-Path $repoRoot "platform\native\KmlGeometry.Native"
$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $BuildRoot))
$vcpkgRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $VcpkgRoot))
$toolchainFile = Join-Path $vcpkgRoot "scripts\buildsystems\vcpkg.cmake"

if (-not (Test-Path $cmakeExe)) {
    throw "CMake was not found at '$cmakeExe'. Install CMake first."
}

if (-not (Test-Path $toolchainFile)) {
    throw "vcpkg toolchain file was not found at '$toolchainFile'. Bootstrap the repo-local vcpkg first."
}

if ($Clean -and (Test-Path $buildRoot)) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null

& $cmakeExe `
    -S $sourceRoot `
    -B $buildRoot `
    -G "Visual Studio 17 2022" `
    -A x64 `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchainFile"

if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed."
}

& $cmakeExe --build $buildRoot --config $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Native geometry build failed."
}

$dllPath = Join-Path $buildRoot "$Configuration\kml_geometry_native.dll"
if (-not (Test-Path $dllPath)) {
    throw "Native geometry DLL was not produced at '$dllPath'."
}

Write-Host "Built native geometry library at $dllPath"
