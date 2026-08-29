# FastExplorer Installer Build Script
param(
    [string]$Arch = "x64",          # "x64" or "arm64"
    [string]$Version = "1.0.8",      # e.g. "1.0.8", "v1.1.0"
    [switch]$Release,               # GitHub Releases に自動アップロード
    [string]$Notes = "",            # リリースノート (説明文)
    [string]$Title = "",            # リリースタイトル (省略時は "FastExplorer vX.X.X")
    [switch]$Draft,                 # 下書き (Draft) として作成
    [switch]$Prerelease             # プレリリースとして作成
)

$ErrorActionPreference = "Stop"

# 'v' または 'V' が先頭についている場合はトリム (例: "v1.0.1" -> "1.0.1")
$CleanVersion = $Version.TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($CleanVersion)) {
    $CleanVersion = "1.0.0"
}

$ProjectDir = $PSScriptRoot
Set-Location $ProjectDir

# Clean dist folder (delete previous installer artifacts)
$distDir = "$ProjectDir\dist"
if (Test-Path $distDir) {
    Write-Host "0. Cleaning previous installer artifacts in dist/..." -ForegroundColor DarkGray
    Get-ChildItem -Path $distDir -Filter "*.exe" -File | Remove-Item -Force -ErrorAction SilentlyContinue
} else {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

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

# 3. GitHub Releases 自動アップロード (オプション: -Release 指定時)
if ($Release) {
    Write-Host "`n3. Uploading Release to GitHub..." -ForegroundColor Cyan

    $ghCmd = (Get-Command "gh.exe" -ErrorAction SilentlyContinue).Source
    if (-not $ghCmd) {
        $ghPaths = @(
            "${env:ProgramFiles}\GitHub CLI\gh.exe",
            "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe",
            "${env:LocalAppData}\Programs\GitHub CLI\gh.exe"
        )
        foreach ($p in $ghPaths) {
            if (Test-Path $p) {
                $ghCmd = $p
                break
            }
        }
    }

    if (-not $ghCmd) {
        Write-Warning "GitHub CLI (gh) was not found."
        Write-Host "Please install GitHub CLI with: winget install --id GitHub.cli" -ForegroundColor Yellow
        Write-Host "Then run: gh auth login" -ForegroundColor Yellow
        exit 1
    }

    $tag = "v$CleanVersion"
    $relTitle = if (-not [string]::IsNullOrWhiteSpace($Title)) { $Title } else { "FastExplorer $tag" }
    $relNotes = if (-not [string]::IsNullOrWhiteSpace($Notes)) { $Notes } else { "Release $tag" }
    $installerPath = "$ProjectDir\dist\$outputBaseFilename.exe"

    $ghArgs = @(
        "release", "create", $tag,
        $installerPath,
        "--title", $relTitle,
        "--notes", $relNotes
    )

    if ($Draft) { $ghArgs += "--draft" }
    if ($Prerelease) { $ghArgs += "--prerelease" }

    Write-Host "Executing: gh $($ghArgs -join ' ')" -ForegroundColor DarkGray
    & $ghCmd @ghArgs

    if ($LASTEXITCODE -ne 0) {
        # リリースが既に存在する場合はアセットの上書きアップロードを試行
        Write-Host "Release $tag already exists or create failed. Attempting upload with --clobber..." -ForegroundColor Yellow
        & $ghCmd release upload $tag $installerPath --clobber
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "`nSUCCESS: Created and published release $tag to GitHub!" -ForegroundColor Green
    } else {
        Write-Error "Failed to create or upload GitHub release. Make sure you are authenticated with gh auth login."
    }
}
