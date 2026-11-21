# Build script for FastExplorerWatcher (Rust Native - windows-rs)
param(
    [string]$Arch = "x64", # "x64" or "arm64"
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$CrateDir = "$ProjectDir\Native\fast_watcher"
$Target = if ($Arch -ieq "arm64") { "aarch64-pc-windows-msvc" } else { "x86_64-pc-windows-msvc" }
$Profile = if ($DebugBuild) { "debug" } else { "release" }
$OutputDir = "$ProjectDir\Watcher\bin"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host "Building FastExplorerWatcher.exe (Rust) for $Target ($Profile)..." -ForegroundColor Cyan

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

$BuiltExe = "$CrateDir\target\$Target\$Profile\FastExplorerWatcher.exe"
if (-not (Test-Path $BuiltExe)) {
    Write-Error "Expected executable not found at $BuiltExe"
    exit 1
}

Copy-Item $BuiltExe "$OutputDir\FastExplorerWatcher.exe" -Force
$size = (Get-Item "$OutputDir\FastExplorerWatcher.exe").Length
Write-Host "Build SUCCESS: $OutputDir\FastExplorerWatcher.exe (Size: $([math]::Round($size / 1KB, 1)) KB / $size bytes)" -ForegroundColor Green
