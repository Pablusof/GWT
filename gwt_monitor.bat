@echo off
chcp 65001 >nul
title GWT Monitor

set "SERVER_DIR=D:\GamesMailRu\emuserverGWT\GwtEmulator.App"
set "CLIENT_DIR=D:\GamesMailRu\Ground War Tanks"
set "CLIENT_EXE=Tank.exe"
set "CLIENT_ARGS=--sz_pers_id=12345 --sz_token=abc123 mpop=cookie123 soc_auth=token456 partner_id=1"
set "RESET_DB=%GWT_RESET_DB%"
if not defined RESET_DB set "RESET_DB=0"

echo [GWT Monitor] Starting...
echo.

call :stop_all
call :clean_logs

echo [GWT Monitor] Starting server...
start "GWT_Server" /D "%SERVER_DIR%" dotnet run
echo [GWT Monitor] Server started, waiting for initialization...
timeout /t 5 /nobreak >nul

echo [GWT Monitor] Starting client...
start "GWT_Client" /D "%CLIENT_DIR%" "%CLIENT_DIR%\%CLIENT_EXE%" %CLIENT_ARGS%
echo [GWT Monitor] Client started
echo.
echo [GWT Monitor] Monitoring... (close this window to stop everything)
echo.

set "CLIENT_WAS_RUNNING=0"

:monitor_loop
timeout /t 2 /nobreak >nul

tasklist /FI "WINDOWTITLE eq GWT_Server*" /NH 2>nul | find /I "dotnet.exe" >nul
if errorlevel 1 (
    echo [GWT Monitor] Server stopped - exiting...
    goto :cleanup
)

tasklist /NH 2>nul | find /I "%CLIENT_EXE%" >nul
if errorlevel 1 (
    if "%CLIENT_WAS_RUNNING%"=="1" (
        echo [GWT Monitor] Client closed - stopping server...
        goto :cleanup
    )
) else (
    if "%CLIENT_WAS_RUNNING%"=="0" (
        echo [GWT Monitor] Client is running
        set "CLIENT_WAS_RUNNING=1"
    )
)

goto :monitor_loop

:cleanup
echo.
echo [GWT Monitor] Stopping all processes...
call :stop_all
echo [GWT Monitor] Done
timeout /t 2 /nobreak >nul
exit /b

:stop_all
echo [GWT Monitor] Killing processes...
for /f "tokens=2" %%a in ('tasklist /FI "WINDOWTITLE eq GWT_Server*" /NH 2^>nul ^| find /I "dotnet.exe"') do (
    taskkill /F /PID %%a >nul 2>&1
    echo   Killed dotnet.exe PID %%a
)
for /f "tokens=2" %%a in ('tasklist /FI "WINDOWTITLE eq GWT_Client*" /NH 2^>nul ^| find /I "%CLIENT_EXE%"') do (
    taskkill /F /PID %%a >nul 2>&1
    echo   Killed %CLIENT_EXE% PID %%a
)
taskkill /F /IM "%CLIENT_EXE%" >nul 2>&1
timeout /t 2 /nobreak >nul
goto :eof

:clean_logs
echo [GWT Monitor] Cleaning logs...
if "%RESET_DB%"=="1" (
    echo [GWT Monitor] Resetting database state...
    del /q "D:\GamesMailRu\emuserverGWT\GwtEmulator.App\gwt_server.db" 2>nul
    del /q "D:\GamesMailRu\emuserverGWT\GwtEmulator.App\gwt_server.db-shm" 2>nul
    del /q "D:\GamesMailRu\emuserverGWT\GwtEmulator.App\gwt_server.db-wal" 2>nul
) else (
    echo [GWT Monitor] Preserving database state (set GWT_RESET_DB=1 to force reset)
)
del /q "D:\GamesMailRu\emuserverGWT\server.log" 2>nul
del /q "D:\GamesMailRu\emuserverGWT\GwtEmulator.App\server.log" 2>nul
del /q "%CLIENT_DIR%\Tank_Data\output_log.txt" 2>nul
del /q "%CLIENT_DIR%\Tank_Data\Player.log" 2>nul
for /d %%i in ("%CLIENT_DIR%\Tank_Data\Logs\*") do rd /s /q "%%i" 2>nul
echo [GWT Monitor] Logs cleaned
goto :eof
