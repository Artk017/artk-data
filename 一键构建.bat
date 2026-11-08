@echo off
setlocal
cd /d "%~dp0"

rem Usage: double-click, or run "build.bat 1.2.0"
set "VERSION=%~1"

echo ==========================================================
echo   Nova Build   (auto +1 when no version is given)
echo   publish self-contained app, then build installers
echo ==========================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found. Please install .NET 10 SDK.
    echo         https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

rem vpk is installed under %USERPROFILE%\.dotnet\tools, add it to PATH
set "PATH=%USERPROFILE%\.dotnet\tools;%PATH%"
where vpk >nul 2>nul
if errorlevel 1 (
    echo [INFO] Velopack CLI not found, installing...
    dotnet tool install --global vpk
)

where vpk >nul 2>nul
if errorlevel 1 (
    echo [ERROR] vpk is not available. Run: dotnet tool install --global vpk
    echo.
    pause
    exit /b 1
)

rem Inno Setup 6 is required for the Artk Tools client installer (installer.iss)
if not exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" if not exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" if not exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" (
    echo [ERROR] Inno Setup 6 not found. Install it first:
    echo         winget install JRSoftware.InnoSetup
    echo.
    pause
    exit /b 1
)

if "%VERSION%"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" -Version %VERSION%
)
if errorlevel 1 (
    echo.
    echo [FAILED] Build failed. See the output above.
    pause
    exit /b 1
)

echo.
echo [DONE] Build finished
echo         Artk Tools installers: dist\release\
echo         ArtkTools-win-Setup.exe       Inno Setup installer
echo         ArtkTools-win-Portable.zip    portable
echo         Artk Server installer: dist\release-server\
echo         ArtkServer-win-Setup.exe      with background-service option
echo         ArtkServer-win-Portable.zip   portable
echo.
echo         dist keeps only release / release-server / version.txt
echo.
start "" "%~dp0dist\release"
pause
