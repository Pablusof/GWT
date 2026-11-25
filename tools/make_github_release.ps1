param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ServerHost = "",
    [string]$LauncherVersion = "1.2.0",
    [string]$GameDir = "",
    [string]$OutRoot = "",
    [string]$PrevManifest = "",
    [int]$MaxPackMb = 1200,
    [ValidateSet("fastest", "optimal", "none")]
    [string]$Compression = "fastest"
)

$ErrorActionPreference = "Stop"
$Repo = $Repo.Trim().Trim('/')
$Tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
$CleanVer = $Version.TrimStart('v')

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$EmuRoot = Split-Path -Parent $ScriptDir
$WorkspaceRoot = Split-Path -Parent $EmuRoot

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = Join-Path $WorkspaceRoot "Ground War Tanks"
}
if ([string]::IsNullOrWhiteSpace($OutRoot)) {
    $OutRoot = Join-Path $WorkspaceRoot "releases"
}

$BuildManifestExe = Join-Path $ScriptDir "BuildManifest\bin\Release\net8.0\BuildManifest.exe"
if (-not (Test-Path $BuildManifestExe)) {
    Write-Host "Собираю BuildManifest..."
    dotnet build (Join-Path $ScriptDir "BuildManifest\BuildManifest.csproj") -c Release | Out-Host
}

$LauncherExe = Join-Path $WorkspaceRoot "GWT_Launcher.exe"
if (-not (Test-Path $LauncherExe)) {
    throw "Не найден $LauncherExe. Сначала соберите GwtLauncher."
}

$RelDir = Join-Path $OutRoot $Tag
New-Item -ItemType Directory -Force -Path $RelDir | Out-Null

$ManifestPath = Join-Path $RelDir "manifest.json"
$BaseUrl = "https://github.com/$Repo/releases/download/$Tag/"
$LauncherUrl = "https://github.com/$Repo/releases/latest/download/GWT_Launcher.exe"
$LatestManifestUrl = "https://github.com/$Repo/releases/latest/download/manifest.json"

$bmArgs = @(
    "--game-dir", $GameDir,
    "--out", $ManifestPath,
    "--pack-dir", $RelDir,
    "--base-url", $BaseUrl,
    "--game-version", $CleanVer,
    "--launcher-version", $LauncherVersion,
    "--launcher-url", $LauncherUrl,
    "--max-pack-mb", $MaxPackMb.ToString(),
    "--compression", $Compression
)
if (-not [string]::IsNullOrWhiteSpace($ServerHost)) {
    $bmArgs += @("--server-host", $ServerHost)
}
if (-not [string]::IsNullOrWhiteSpace($PrevManifest)) {
    $bmArgs += @("--prev-manifest", $PrevManifest)
}

& $BuildManifestExe @bmArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -Force $LauncherExe (Join-Path $RelDir "GWT_Launcher.exe")

$cfgObj = [ordered]@{
    manifestUrl    = $LatestManifestUrl
    serverHost     = $(if ([string]::IsNullOrWhiteSpace($ServerHost)) { "127.0.0.1" } else { $ServerHost })
    gamePort       = 9801
    authPort       = 18080
    assetPort      = 18081
    useLocalServer = [string]::IsNullOrWhiteSpace($ServerHost)
}
$CfgPath = Join-Path $RelDir "launcher_config.json"
$cfgObj | ConvertTo-Json -Depth 3 | Set-Content -Path $CfgPath -Encoding UTF8

# Готовый архив для игроков: GWT_Launcher.exe + launcher_config.json
$PortableZip = Join-Path $RelDir "GWT_Launcher_Player.zip"
if (Test-Path $PortableZip) { Remove-Item -Force $PortableZip }
Compress-Archive -Path (Join-Path $RelDir "GWT_Launcher.exe"), $CfgPath -DestinationPath $PortableZip

Write-Host ""
Write-Host "=== Релиз $Tag готов в папке: $RelDir ==="
Get-ChildItem $RelDir | ForEach-Object {
    Write-Host ("  - {0} ({1:N1} МБ)" -f $_.Name, ($_.Length / 1MB))
}
Write-Host ""
Write-Host "Загрузи ВСЕ эти файлы в GitHub Release с тегом $Tag."
Write-Host "Игрокам давай ссылку на скачивание GWT_Launcher_Player.zip из релиза!"
