@echo off
title Ground War Tanks - Quick Start
color 0A

echo ========================================
echo   Ground War Tanks - Quick Start
echo ========================================
echo.
echo This script will start the emulator and the game automatically.
echo.

REM Check .NET SDK
where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK not found!
    echo.
    echo Run install_dependencies.bat to install.
    echo.
    pause
    exit /b 1
)

REM Check if configured
if not exist "%~dp0GwtEmulator.App\gwt_server.db" (
    echo WARNING: Emulator not configured!
    echo.
    echo Please run setup.bat first.
    echo.
    pause
    exit /b 1
)

echo Starting emulator server...
start "GWT Emulator Server" cmd /c "cd /d "%~dp0" && dotnet run --project GwtEmulator.App"

echo.
echo Waiting for servers to start (5 seconds)...
timeout /t 5 /nobreak >nul

echo.
echo Looking for game folder...

REM Search for game path
set GAME_PATH=

REM Check common paths
if exist "C:\Games\Ground War Tanks\Tank.exe" (
    set GAME_PATH=C:\Games\Ground War Tanks
    goto found
)

if exist "D:\Games\Ground War Tanks\Tank.exe" (
    set GAME_PATH=D:\Games\Ground War Tanks
    goto found
)

if exist "E:\Games\Ground War Tanks\Tank.exe" (
    set GAME_PATH=E:\Games\Ground War Tanks
    goto found
)

if exist "%PROGRAMFILES%\Ground War Tanks\Tank.exe" (
    set GAME_PATH=%PROGRAMFILES%\Ground War Tanks
    goto found
)

if exist "%PROGRAMFILES(X86)%\Ground War Tanks\Tank.exe" (
    set GAME_PATH=%PROGRAMFILES(X86)%\Ground War Tanks
    goto found
)

REM Ask user if not found
:ask_path
set /p GAME_PATH="Enter path to Ground War Tanks folder: "

if not exist "%GAME_PATH%\Tank.exe" (
    echo.
    echo ERROR: Tank.exe not found in %GAME_PATH%
    echo.
    goto ask_path
)

:found
echo.
echo Game found: %GAME_PATH%
echo.

REM Check start.bat
if not exist "%GAME_PATH%\start.bat" (
    echo WARNING: start.bat not found!
    echo.
    echo Please run setup.bat first to configure the client.
    echo.
    pause
    exit /b 1
)

echo Starting game...
start "" "%GAME_PATH%\start.bat"

echo.
echo ========================================
echo   Game started!
echo ========================================
echo.
echo DO NOT close the emulator server window!
echo.
echo Enjoy the game!
echo.
pause
