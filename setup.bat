@echo off
title Ground War Tanks Emulator - Setup
set /p GAME_PATH="Enter path to Ground War Tanks folder: "
powershell -ExecutionPolicy Bypass -File "%~dp0setup.ps1" -GamePath "%GAME_PATH%"
