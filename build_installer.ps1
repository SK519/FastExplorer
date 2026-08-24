# FastExplorer Installer Build Script
param(
    [string]$Arch = "x64" # "x64" or "arm64"
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
Set-Location $ProjectDir

Write-Host "1. Publishing Release build for win-$Arch..." -ForegroundColor Cyan

# Publish specified architecture
dotnet publish -c Release -r "win-$Arch" /p:Platform=$Arch --self-contained true /p:PublishReadyToRun=true

# Copy icon.ico to publish directory
Copy-Item "icon.ico" "bin\Release\net10.0-windows10.0.19041.0\win-$Arch\publish\" -Force

# Locate Inno Setup Compiler (Inno Setup 7 prioritized)
$isccPaths = @(
    "C:\Users\swei4\AppData\Local\Programs\Inno Setup 7\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Users\swei4\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$iscc = $null
foreach ($path in $isccPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    $iscc = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue).Source
}

if (-not $iscc -or -not (Test-Path $iscc)) {
    Write-Error "ISCC.exe (Inno Setup Compiler) was not found."
    exit 1
}

Write-Host "`n2. Compiling Installer with Inno Setup..." -ForegroundColor Cyan
& $iscc "/DAppArch=$Arch" "$ProjectDir\installer.iss"

Write-Host "`nSUCCESS! Single installer created at: $ProjectDir\dist\FastExplorer_Setup.exe" -ForegroundColor Green
