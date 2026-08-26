# Build script for FastExplorerWatcher (C++ Native)
$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$SourceFile = "$ProjectDir\Watcher\FastExplorerWatcher.cpp"
$OutputDir = "$ProjectDir\Watcher\bin"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Find vcvars64.bat using vswhere or standard search paths
$vcvars = $null

# 1. vswhere.exe detection (official VS locator)
$vswherePaths = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
)
foreach ($vsw in $vswherePaths) {
    if (Test-Path $vsw) {
        $installPath = & $vsw -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installPath -and (Test-Path "$installPath\VC\Auxiliary\Build\vcvars64.bat")) {
            $vcvars = "$installPath\VC\Auxiliary\Build\vcvars64.bat"
            break
        }
    }
}

# 2. Dynamic ProgramFiles fallback paths
if (-not $vcvars) {
    $pfList = @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}, ${env:ProgramW6432}, "C:\Program Files", "C:\Program Files (x86)") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
    foreach ($pf in $pfList) {
        $candidates = @(
            "$pf\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat",
            "$pf\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
            "$pf\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
            "$pf\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
            "$pf\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat",
            "$pf\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat",
            "$pf\Microsoft Visual Studio\2019\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) {
                $vcvars = $c
                break 2
            }
        }
    }
}

# 3. Recursive fallback search
if (-not $vcvars) {
    foreach ($pf in $pfList) {
        $vsRoot = "$pf\Microsoft Visual Studio"
        if (Test-Path $vsRoot) {
            $vcvars = Get-ChildItem $vsRoot -Recurse -Filter "vcvars64.bat" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
            if ($vcvars) { break }
        }
    }
}

if (-not $vcvars) {
    Write-Error "vcvars64.bat was not found!"
    exit 1
}

Write-Host "Compiling FastExplorerWatcher with MSVC ($vcvars)..." -ForegroundColor Cyan

$cmd = "`"$vcvars`" && cl.exe /O2 /MT /utf-8 /DUNICODE /D_UNICODE /Fe`"$OutputDir\FastExplorerWatcher.exe`" `"$SourceFile`" /link user32.lib shell32.lib advapi32.lib ole32.lib oleaut32.lib shlwapi.lib /SUBSYSTEM:WINDOWS"
cmd.exe /c $cmd

if (Test-Path "$OutputDir\FastExplorerWatcher.exe") {
    $size = (Get-Item "$OutputDir\FastExplorerWatcher.exe").Length
    Write-Host "Build SUCCESS: $OutputDir\FastExplorerWatcher.exe (Size: $size bytes)" -ForegroundColor Green
} else {
    Write-Error "Build failed!"
    exit 1
}
