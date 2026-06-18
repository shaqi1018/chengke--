@echo off
echo ============================================
echo   Remove libusbK drivers (fix MSC driver flip)
echo ============================================
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] No admin rights!
    echo Right-click this file and choose "Run as administrator"
    echo.
    pause
    exit /b 1
)

echo [OK] Admin rights confirmed
echo.
echo === Deleting all libusbK sensor_wcid INFs ===
for %%I in (oem55.inf oem56.inf oem58.inf oem59.inf oem60.inf) do (
    echo --- delete %%I ---
    pnputil /delete-driver %%I /uninstall /force
    echo.
)

echo === Rescan hardware ===
pnputil /scan-devices
echo.

echo === Remaining sensor_wcid drivers ===
pnputil /enum-drivers | findstr /i "sensor_wcid libusbK libwdi"
echo.
echo ============================================
echo   Done! Check above for libusbK/libwdi.
echo   If none remain = success.
echo ============================================
echo.
pause
