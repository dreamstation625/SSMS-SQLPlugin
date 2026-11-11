@echo off
setlocal
net session >nul 2>&1
if not "%errorLevel%"=="0" (
    echo.
    echo [FAIL] This script needs administrator privileges.
    echo        Right-click install.bat and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

set "TARGET=C://Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMS-SQLPlugin"
set "SRC=%~dp0dist\deploy"
if not exist "%SRC%\" set "SRC=%~dp0deploy"

if not exist "%SRC%\" (
    echo [FAIL] deploy folder not found.
    echo        Expected: %~dp0dist\deploy
    echo        Run first: python build.py
    pause
    exit /b 1
)

if not exist "%TARGET%" mkdir "%TARGET%"
copy /Y "%SRC%\*" "%TARGET%\" >nul
if errorlevel 1 (
    echo [FAIL] copy failed
    pause
    exit /b 1
)

echo [OK] Installed to:
echo    %TARGET%
echo.
echo Files:
dir /b "%TARGET%"
echo.
echo Next:
echo   1. Close ALL SSMS windows
echo   2. Start SSMS
echo   3. Check log: %TEMP%\SsmsSqlPlugin\probe.log
pause
