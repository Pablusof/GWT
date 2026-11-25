@echo off
chcp 65001 >nul
title Патч витрины главного приза (T95/Chieftain -> FV4030/4)
setlocal

cd /d "%~dp0"

set "PYEXE="
where py >nul 2>nul && set "PYEXE=py"
if not defined PYEXE (
    where python >nul 2>nul && set "PYEXE=python"
)

if not defined PYEXE (
    echo.
    echo [ОШИБКА] Python не найден в PATH.
    echo Установите Python 3 с https://www.python.org/downloads/
    echo и отметьте "Add Python to PATH".
    echo.
    pause
    exit /b 1
)

%PYEXE% -c "import UnityPy" 1>nul 2>nul
if errorlevel 1 (
    echo Устанавливаю UnityPy...
    %PYEXE% -m pip install UnityPy
    echo.
)

REM TypeTreeGeneratorAPI — отдельный пакет, pip install UnityPy его не тянет.
%PYEXE% -c "import TypeTreeGeneratorAPI" 1>nul 2>nul
if errorlevel 1 (
    echo Устанавливаю TypeTreeGeneratorAPI...
    %PYEXE% -m pip install TypeTreeGeneratorAPI
    echo.
)

echo ВНИМАНИЕ: игра должна быть ЗАКРЫТА. Будет создан бэкап level0.bak.
echo.
%PYEXE% "%~dp0patch_power_reward.py" %1

echo.
pause
endlocal
