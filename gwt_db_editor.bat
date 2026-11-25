@echo off
title GWT DB Editor
set "EDITOR=D:\GamesMailRu\emuserverGWT\tools\db_editor.py"

echo Starting GWT account DB editor...
echo.

py -3 "%EDITOR%" 2>nul
if not errorlevel 1 goto :eof

python "%EDITOR%" 2>nul
if not errorlevel 1 goto :eof

python3 "%EDITOR%" 2>nul
if not errorlevel 1 goto :eof

echo.
echo [ERROR] Python 3 was not found in PATH.
echo Install Python 3 from https://www.python.org/downloads/
echo and enable "Add python.exe to PATH" during setup.
echo.
pause
