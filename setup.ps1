# Ground War Tanks Emulator - Setup Script
# Configure game client for emulator

param(
    [string]$GamePath
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "Ground War Tanks Emulator - Setup"

Write-Host "========================================" -ForegroundColor Green
Write-Host "  Ground War Tanks Emulator - Setup" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "This script will configure the game client for the emulator."
Write-Host ""

# Request game path if not provided
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    do {
        $GamePath = Read-Host "Enter full path to Ground War Tanks folder (e.g., C:\Games\gwtnew)"
        
        if (-not (Test-Path $GamePath)) {
            Write-Host ""
            Write-Host "ERROR: Folder not found: $GamePath" -ForegroundColor Red
            Write-Host "Please try again."
            Write-Host ""
        }
        elseif (-not (Test-Path "$GamePath\Tank.exe")) {
            Write-Host ""
            Write-Host "ERROR: Tank.exe not found in $GamePath" -ForegroundColor Red
            Write-Host "Make sure you specified the correct path to the game."
            Write-Host ""
        }
        else {
            $validPath = $true
        }
    } while (-not $validPath)
}
else {
    # Validate provided path
    if (-not (Test-Path $GamePath)) {
        Write-Host "ERROR: Folder not found: $GamePath" -ForegroundColor Red
        exit 1
    }
    if (-not (Test-Path "$GamePath\Tank.exe")) {
        Write-Host "ERROR: Tank.exe not found in $GamePath" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "Path found: $GamePath" -ForegroundColor Green
Write-Host ""

# [1/4] Disable anti-cheat
Write-Host "[1/4] Disabling MRAC anti-cheat..." -ForegroundColor Cyan

if (Test-Path "$GamePath\mrac.cfg") {
    if (Test-Path "$GamePath\mrac.cfg.disabled") {
        Remove-Item "$GamePath\mrac.cfg.disabled" -Force
    }
    if (Test-Path "$GamePath\mrac.cfg.bak") {
        Remove-Item "$GamePath\mrac.cfg.bak" -Force
    }
    Rename-Item "$GamePath\mrac.cfg" "mrac.cfg.disabled"
    Write-Host "  mrac.cfg -> mrac.cfg.disabled" -ForegroundColor Yellow
}

if (Test-Path "$GamePath\mrac.dll") {
    if (Test-Path "$GamePath\mrac.dll.disabled") {
        Remove-Item "$GamePath\mrac.dll.disabled" -Force
    }
    if (Test-Path "$GamePath\mrac.dll.bak") {
        Remove-Item "$GamePath\mrac.dll.bak" -Force
    }
    Rename-Item "$GamePath\mrac.dll" "mrac.dll.disabled"
    Write-Host "  mrac.dll -> mrac.dll.disabled" -ForegroundColor Yellow
}

Write-Host "Anti-cheat disabled!" -ForegroundColor Green
Write-Host ""

# [2/4] Configure ClientConfig.tk
Write-Host "[2/4] Configuring ClientConfig.tk..." -ForegroundColor Cyan

$configContent = @"
;;-----------------------------------------------------
;;Client configuration
;;Platform:gamebox/mailru
;;-----------------------------------------------------
[Platform]
Platform=mailru

[GameServer]
GameIP=127.0.0.1
Port=9801

[AssetServer]
AssetIP=127.0.0.1:18081

[StatisticalServer]
;;StatisticalIP=http://prod.tanks.mail.ru/pub/client_log.php

[AuthorizeServer]
AuthorizeIP=http://127.0.0.1:18080

[RechargeServer]
RechargeIP=http://tanks.mail.ru/user/bank

[DebugNetwork]
Debug=1
DebugNetwork_Debug=1
DebugNetwork_Socket=0
DebugNetwork_ErrorHandling=0
"@

$configContent | Out-File -FilePath "$GamePath\ClientConfig.tk" -Encoding UTF8
Write-Host "ClientConfig.tk configured for local emulator!" -ForegroundColor Green
Write-Host ""

# Tell the emulator's AssetServer where to find this machine's real AssetBundles
# folder, so tank model/skin files load from disk instead of the dead
# images.tanks.mail.ru CDN (that's what was making every tank render solid black).
$assetBundlesPath = Join-Path $GamePath "AssetBundles"
if (Test-Path $assetBundlesPath) {
    $assetBundlesPath | Out-File -FilePath "$PSScriptRoot\asset_root.txt" -Encoding UTF8 -NoNewline
    Write-Host "asset_root.txt configured -> $assetBundlesPath" -ForegroundColor Green
} else {
    Write-Host "WARNING: $assetBundlesPath not found - tank skins will stay broken until" -ForegroundColor Yellow
    Write-Host "         asset_root.txt (next to this script) points at your real AssetBundles folder." -ForegroundColor Yellow
}
Write-Host ""

# [3/4] Create start.bat
Write-Host "[3/4] Creating start.bat for game launch..." -ForegroundColor Cyan

$startBatContent = @"
@echo off
cd /d "%~dp0"
if exist Tank_Data\output_log.txt del Tank_Data\output_log.txt
Tank.exe "--sz_pers_id=12345 --sz_token=abc123 mpop=cookie123 soc_auth=token456 partner_id=1"
"@

$startBatContent | Out-File -FilePath "$GamePath\start.bat" -Encoding ASCII
Write-Host "start.bat created!" -ForegroundColor Green
Write-Host ""

# [4/4] Check files
Write-Host "[4/4] Checking files..." -ForegroundColor Cyan

if (Test-Path "$GamePath\Tank.exe") {
    Write-Host "  Tank.exe - OK" -ForegroundColor Green
}
if (Test-Path "$GamePath\Tank_Data") {
    Write-Host "  Tank_Data - OK" -ForegroundColor Green
}
if (Test-Path "$GamePath\AssetBundles") {
    Write-Host "  AssetBundles - OK" -ForegroundColor Green
}
Write-Host ""

# Completion
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Setup completed successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "What was done:" -ForegroundColor Cyan
Write-Host "  + MRAC anti-cheat disabled" -ForegroundColor Green
Write-Host "  + ClientConfig.tk configured to 127.0.0.1" -ForegroundColor Green
Write-Host "  + start.bat created for game launch" -ForegroundColor Green
Write-Host ""
Write-Host "Now you can:" -ForegroundColor Cyan
Write-Host "  1. Start emulator: start_emulator.bat"
Write-Host "  2. Start game: $GamePath\start.bat"
Write-Host ""
Write-Host "IMPORTANT: Start emulator first, then the game!" -ForegroundColor Yellow
Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
