@echo off
title Ground War Tanks Emulator Server
color 0B

echo ========================================
echo   Ground War Tanks Emulator Server
echo ========================================
echo.

where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK not found!
    echo.
    echo Please install .NET 8 SDK:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    echo Or run install_dependencies.bat
    echo.
    pause
    exit /b 1
)

echo Checking .NET SDK...
dotnet --version
echo.

echo Starting emulator server...
echo.
echo Servers:
echo   - Auth Server:   http://127.0.0.1:18080/
echo   - Login Server:  127.0.0.1:9801
echo   - Hall Server:   127.0.0.1:9301
echo   - Room Server:   127.0.0.1:9601
echo.
echo Press Ctrl+C to stop the server.
echo.

cd /d "%~dp0"
dotnet run --project GwtEmulator.App

pause
