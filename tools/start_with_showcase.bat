@echo off
rem ============================================================================
rem  Запуск Ground War Tanks с ротацией витрины главного приза «Золотого запаса».
rem
rem  Перед стартом игры витрина (танк справа от колеса + «Подробнее») меняется на
rem  следующий танк из списка. Клиент читает level0 при запуске, поэтому каждая
rem  сессия показывает новый главный приз: Chieftain -> Obj279 -> Object 140 ->
rem  FV214 -> 1K-17 -> FV4030/4 -> Object 263 -> XM815 Chrysler -> по кругу.
rem
rem  Как использовать:
rem    1) Скопируйте этот файл и tools\rotate_showcase.py в папку игры
rem       (туда же, где Tank.exe и Tank_Data).
rem    2) Убедитесь, что установлен Python и пакеты:
rem         pip install UnityPy TypeTreeGeneratorAPI
rem    3) Запускайте игру ЭТИМ файлом вместо start.bat.
rem
rem  Хотите случайный танк вместо цикла — добавьте --random в строку вызова ниже.
rem  Не нужна ротация (фиксированная витрина) — закомментируйте строку с python.
rem ============================================================================
@echo off
cd /d "%~dp0"

rem --- ротация витрины (следующий танк из списка) ---
rem  Если СЕРВЕР на этой же машине — добавьте --sync-server и путь к bonusboxes.json,
rem  чтобы сервер выдавал ровно тот танк, что на витрине. Пример:
rem    python rotate_showcase.py --level0 "Tank_Data\level0" --game "." ^
rem       --sync-server "C:\server\GwtEmulator.Server\GameTables\Data\bonusboxes.json"
python rotate_showcase.py --level0 "Tank_Data\level0" --game "."
if errorlevel 1 (
    echo [showcase] не удалось повернуть витрину - запускаю игру как есть
)

rem --- обычный запуск игры ---
if exist Tank_Data\output_log.txt del Tank_Data\output_log.txt
Tank.exe "--sz_pers_id=12345 --sz_token=abc123 mpop=cookie123 soc_auth=token456 partner_id=1"
