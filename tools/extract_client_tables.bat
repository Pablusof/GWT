@echo off
chcp 65001 >nul
title Выгрузка таблиц клиента (Table_New_bonusbox и др.)
setlocal

REM Этот .bat держит окно открытым, что бы ни случилось — ошибку будет видно.
REM Можно перетащить папку "Ground War Tanks" прямо на этот файл: путь уйдёт
REM в скрипт первым аргументом.

cd /d "%~dp0"

REM Ищем Python: сначала launcher "py", потом "python".
set "PYEXE="
where py >nul 2>nul && set "PYEXE=py"
if not defined PYEXE (
    where python >nul 2>nul && set "PYEXE=python"
)

if not defined PYEXE (
    echo.
    echo [ОШИБКА] Python не найден в PATH.
    echo Установите Python 3 с https://www.python.org/downloads/
    echo и при установке отметьте галочку "Add Python to PATH".
    echo.
    pause
    exit /b 1
)

echo Использую Python: %PYEXE%
echo.

REM Проверяем UnityPy и при отсутствии предлагаем поставить.
%PYEXE% -c "import UnityPy" 1>nul 2>nul
if errorlevel 1 (
    echo Библиотека UnityPy не установлена.
    echo Устанавливаю UnityPy...
    %PYEXE% -m pip install UnityPy
    echo.
)

REM TypeTreeGeneratorAPI — ОТДЕЛЬНЫЙ пакет, без него чтение таблиц падает с ошибкой
REM "TypeTreeGeneratorAPI isn't installed!". pip install UnityPy его НЕ тянет.
REM Колёса идут как cp36-abi3 — ставятся на любой Python 3.6+ (в т.ч. 3.14) без сборки.
%PYEXE% -c "import TypeTreeGeneratorAPI" 1>nul 2>nul
if errorlevel 1 (
    echo Устанавливаю TypeTreeGeneratorAPI (нужен для чтения таблиц)...
    %PYEXE% -m pip install TypeTreeGeneratorAPI
    echo.
)

REM %1 — необязательный путь к папке игры (если перетащили её на .bat).
%PYEXE% "%~dp0extract_client_tables.py" %1

echo.
pause
endlocal
