@echo off
setlocal
set "TOOLS=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin"
set "BUILDER=%TOOLS%\MSBuild.exe"
set "LOG=%~dp0build.log"

if not exist "%BUILDER%" (
    echo [FAIL] Build tool not found:
    echo        %BUILDER%
    pause
    exit /b 1
)

echo Building with the official VSSDK toolchain...
echo Log: %LOG%
echo.
"%BUILDER%" "%~dp0SsmsSqlPlugin.Std.csproj" /t:Rebuild /restore /p:Configuration=Release /v:d /nologo > "%LOG%" 2>&1
set "RC=%errorlevel%"
echo EXITCODE=%RC%
echo.
echo Searching for .vsix:
dir /s /b "%~dp0*.vsix"
echo.
echo Log written to %LOG%
pause
