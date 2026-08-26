# FastExplorer Installer Build Script
param(
    [string]$Arch = "x64",        # "x64" or "arm64"
    [string]$Version = "1.0.0"    # e.g. "1.0.1", "v1.1.0"
)

$ErrorActionPreference = "Stop"

# 'v' または 'V' が先頭についている場合はトリム (例: "v1.0.1" -> "1.0.1")
$CleanVersion = $Version.TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($CleanVersion)) {
    $CleanVersion = "1.0.0"
}

$ProjectDir = $PSScriptRoot
Set-Location $ProjectDir

Write-Host "1. Publishing Release build for win-$Arch (Version: $CleanVersion)..." -ForegroundColor Cyan

# Publish specified architecture with version metadata
dotnet publish -c Release -r "win-$Arch" /p:Platform=$Arch --self-contained true /p:PublishReadyToRun=true /p:Version=$CleanVersion /p:AssemblyVersion=$CleanVersion /p:FileVersion=$CleanVersion /p:InformationalVersion=$CleanVersion

# Copy icon.ico to publish directory
Copy-Item "icon.ico" "bin\Release\net10.0-windows10.0.19041.0\win-$Arch\publish\" -Force

# Build C++ FastExplorerWatcher.exe and copy to publish directory
Write-Host "`n1.5. Building C++ FastExplorerWatcher..." -ForegroundColor Cyan
& "$ProjectDir\build_watcher.ps1"
Copy-Item "$ProjectDir\Watcher\bin\FastExplorerWatcher.exe" "bin\Release\net10.0-windows10.0.19041.0\win-$Arch\publish\" -Force

# Clean up unnecessary language folders from publish directory (Keep only Assets, ja-JP, en-us)
$publishDir = "$ProjectDir\bin\Release\net10.0-windows10.0.19041.0\win-$Arch\publish"
$allowedDirs = @("Assets", "ja-JP", "ja", "en-us", "en-US", "en")
if (Test-Path $publishDir) {
    Get-ChildItem -Directory -Path $publishDir | ForEach-Object {
        if ($allowedDirs -notcontains $_.Name) {
            Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Locate Inno Setup Compiler dynamically
$iscc = $null

# 1. Check PATH
$pathCmd = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue).Source
if ($pathCmd -and (Test-Path $pathCmd)) {
    $iscc = $pathCmd
}

# 2. Check standard install directories via environment variables
if (-not $iscc) {
    $searchRoots = @(
        "${env:LocalAppData}\Programs",
        ${env:ProgramFiles},
        ${env:ProgramFiles(x86)},
        ${env:ProgramW6432}
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    $candidates = @()
    foreach ($root in $searchRoots) {
        $candidates += "$root\Inno Setup 7\ISCC.exe"
        $candidates += "$root\Inno Setup 6\ISCC.exe"
    }

    foreach ($path in $candidates) {
        if (Test-Path $path) {
            $iscc = $path
            break
        }
    }
}

# 3. Check Registry
if (-not $iscc) {
    $regKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )
    foreach ($rk in $regKeys) {
        if (Test-Path $rk) {
            $installLoc = (Get-ItemProperty -Path $rk -Name "InstallLocation" -ErrorAction SilentlyContinue).InstallLocation
            if ($installLoc -and (Test-Path "$installLoc\ISCC.exe")) {
                $iscc = "$installLoc\ISCC.exe"
                break
            }
        }
    }
}

if (-not $iscc -or -not (Test-Path $iscc)) {
    Write-Error "ISCC.exe (Inno Setup Compiler) was not found."
    exit 1
}

Write-Host "`n2. Compiling Installer with Inno Setup (Version: $CleanVersion)..." -ForegroundColor Cyan
$outputBaseFilename = "FastExplorer_Setup_v$CleanVersion"
& $iscc "/DAppArch=$Arch" "/DMyAppVersion=$CleanVersion" "/DOutputBaseFilename=$outputBaseFilename" "$ProjectDir\installer.iss"

# Also create FastExplorer_Setup.exe copy for convenient local testing
if (Test-Path "$ProjectDir\dist\$outputBaseFilename.exe") {
    Copy-Item "$ProjectDir\dist\$outputBaseFilename.exe" "$ProjectDir\dist\FastExplorer_Setup.exe" -Force
}

Write-Host "`nSUCCESS! Installer created at:" -ForegroundColor Green
Write-Host "  - $ProjectDir\dist\$outputBaseFilename.exe" -ForegroundColor Green
Write-Host "  - $ProjectDir\dist\FastExplorer_Setup.exe" -ForegroundColor Green

