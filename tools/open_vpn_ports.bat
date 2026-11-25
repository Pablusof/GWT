@echo off
chcp 65001 > nul
echo Открытие TCP-портов Ground War Tanks в Брандмауэре Windows (9801, 9301, 9601, 18080, 18081)...
netsh advfirewall firewall delete rule name="GWT Emulator Ports" > nul 2>&1
netsh advfirewall firewall add rule name="GWT Emulator Ports" dir=in action=allow protocol=TCP localport=9801,9301,9601,18080,18081
if %errorlevel% neq 0 (
    echo.
    echo ОШИБКА: Запустите этот файл от имени Администратора!
) else (
    echo.
    echo Готово! Порты открыты для друзей по Radmin VPN / локальной сети.
)
pause
