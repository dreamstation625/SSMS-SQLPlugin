@echo off
setlocal
net session >nul 2>&1
if not "%errorLevel%"=="0" (
    echo [FAIL] Administrator privileges required.
    echo        Right-click this file and choose "Run as administrator".
    pause
    exit /b 1
)

set "IDE=C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE"
set "TARGET=%IDE%\Extensions\SSMS-SQLPlugin"
set "SRC=%~dp0dist\deploy"
set "USEREXT=%LOCALAPPDATA%\Microsoft\SSMS\22.0_a832b12e\Extensions\ocnybrrt.303"

if not exist "%SRC%\" (
    echo [FAIL] deploy folder missing. Run: python build.py first.
    pause
    exit /b 1
)

echo Closing SSMS...
taskkill /F /IM SSMS.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo Installing to machine-wide extension folder...
if not exist "%TARGET%" mkdir "%TARGET%"
copy /Y "%SRC%\*" "%TARGET%\" >nul
if errorlevel 1 (
    echo [FAIL] copy failed
    pause
    exit /b 1
)

if exist "%USEREXT%" (
    echo Removing user-scope copy to avoid duplicate package registration...
    rd /s /q "%USEREXT%"
)

echo Running SSMS /setup, this can take a minute...
"%IDE%\SSMS.exe" /setup
echo SETUP_EXITCODE=%errorlevel%

echo.
echo Files in machine extension folder:
dir /b "%TARGET%"
echo.
pause
