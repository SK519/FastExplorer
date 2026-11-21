# Build script for fast_archive.dll (Rust Native Archive Engine)
param(
    [string]$Arch = "x64", # "x64" or "arm64"
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$CrateDir = "$ProjectDir\Native\fast_archive"
$Target = if ($Arch -ieq "arm64") { "aarch64-pc-windows-msvc" } else { "x86_64-pc-windows-msvc" }
$Profile = if ($DebugBuild) { "debug" } else { "release" }
$OutputDir = "$CrateDir\bin\win-$Arch"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host "Building fast_archive.dll (Rust) for $Target ($Profile)..." -ForegroundColor Cyan

Push-Location $CrateDir
try {
    $cargoArgs = @("build", "--target", $Target)
    if (-not $DebugBuild) {
        $cargoArgs += "--release"
    }
    & cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$BuiltDll = "$CrateDir\target\$Target\$Profile\fast_archive.dll"
if (-not (Test-Path $BuiltDll)) {
    Write-Error "Expected DLL not found at $BuiltDll"
    exit 1
}

Copy-Item $BuiltDll "$OutputDir\fast_archive.dll" -Force
$size = (Get-Item "$OutputDir\fast_archive.dll").Length
Write-Host "Build SUCCESS: $OutputDir\fast_archive.dll (Size: $([math]::Round($size / 1MB, 2)) MB / $size bytes)" -ForegroundColor Green
