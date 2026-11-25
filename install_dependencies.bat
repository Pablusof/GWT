@echo off
title Ground War Tanks Emulator - Install Dependencies
color 0E

echo ========================================
echo   Install Dependencies
echo ========================================
echo.

where dotnet >nul 2>nul
if %ERRORLEVEL% EQU 0 (
    echo .NET SDK is already installed!
    echo Version:
    dotnet --version
    echo.
    echo No installation needed.
    echo.
    pause
    exit /b 0
)

echo .NET SDK not found.
echo.
echo The emulator requires .NET 8 SDK to run.
echo.
echo Opening download page...
echo.

start https://dotnet.microsoft.com/download/dotnet/8.0

echo.
echo ========================================
echo   Installation instructions:
echo ========================================
echo.
echo 1. Download ".NET SDK 8.0.x" from the opened page
echo    (choose x64 version for 64-bit Windows)
echo.
echo 2. Run the downloaded installer
echo.
echo 3. Follow the installer instructions
echo.
echo 4. RESTART your computer after installation
echo    (or at least close and reopen command prompt)
echo.
echo 5. Verify installation: dotnet --version
echo.
echo ========================================
echo.
pause
