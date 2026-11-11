@echo off
setlocal
net session >nul 2>&1
if not "%errorLevel%"=="0" (
    echo.
    echo [FAIL] This script needs administrator privileges.
    echo        Right-click uninstall.bat and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

set "TARGET=C://Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMS-SQLPlugin"

if exist "%TARGET%" (
    rmdir /S /Q "%TARGET%"
    echo [OK] Removed
    echo    %TARGET%
) else (
    echo [INFO] Not installed
)
pause
