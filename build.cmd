@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FRAMEWORK=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "OUT_DIR=%ROOT%outputs"
set "OUT_EXE=%OUT_DIR%\TinyHwBar.exe"
set "APP_ICON=%ROOT%assets\TinyHwBar.ico"
set "TEMP_DIR="
set "TEMP_EXE="

if not exist "%CSC%" (
    echo ERROR: .NET Framework compiler not found: "%CSC%"
    exit /b 1
)

if not exist "%APP_ICON%" (
    echo ERROR: Application icon not found: "%APP_ICON%"
    exit /b 1
)

if not exist "%OUT_DIR%" (
    mkdir "%OUT_DIR%"
    if errorlevel 1 (
        echo ERROR: Could not create output directory: "%OUT_DIR%"
        exit /b 1
    )
)

:choose_temp_dir
set "TEMP_DIR=%OUT_DIR%\TinyHwBar.build.%RANDOM%.%RANDOM%.tmp"
if exist "%TEMP_DIR%" goto choose_temp_dir
mkdir "%TEMP_DIR%"
if errorlevel 1 (
    echo ERROR: Could not create a temporary build directory.
    exit /b 1
)
set "TEMP_EXE=%TEMP_DIR%\TinyHwBar.exe"

"%CSC%" ^
    /nologo ^
    /langversion:5 ^
    /target:winexe ^
    /platform:x64 ^
    /optimize+ ^
    /debug- ^
    /warn:4 ^
    /warnaserror+ ^
    /codepage:65001 ^
    /utf8output ^
    /win32manifest:"%ROOT%app.manifest" ^
    /win32icon:"%APP_ICON%" ^
    /out:"%TEMP_EXE%" ^
    /reference:"%FRAMEWORK%\System.dll" ^
    /reference:"%FRAMEWORK%\System.Core.dll" ^
    /reference:"%FRAMEWORK%\System.Drawing.dll" ^
    /reference:"%FRAMEWORK%\System.Windows.Forms.dll" ^
    "%ROOT%src\AssemblyInfo.cs" ^
    "%ROOT%src\Program.cs" ^
    "%ROOT%src\NativeMethods.cs" ^
    "%ROOT%src\NetworkSampler.cs" ^
    "%ROOT%src\GatewayLatencySampler.cs" ^
    "%ROOT%src\IntelGpuSampler.cs" ^
    "%ROOT%src\StartupManager.cs" ^
    "%ROOT%src\UpdateService.cs" ^
    "%ROOT%src\UpdatePackageService.cs" ^
    "%ROOT%src\LoopbackApiServer.cs" ^
    "%ROOT%src\TelemetryService.cs" ^
    "%ROOT%src\HardwareSampler.cs" ^
    "%ROOT%src\SettingsStore.cs" ^
    "%ROOT%src\HistoryStore.cs" ^
    "%ROOT%src\MetricHistory.cs" ^
    "%ROOT%src\HistoryChartControl.cs" ^
    "%ROOT%src\DashboardForm.cs" ^
    "%ROOT%src\MonitorForm.cs"

if errorlevel 1 (
    call :cleanup_temp_exe
    echo ERROR: Build failed.
    exit /b 1
)

if not exist "%TEMP_EXE%" (
    call :cleanup_temp_exe
    echo ERROR: Compiler completed without producing the temporary executable.
    exit /b 1
)

move /y "%TEMP_EXE%" "%OUT_EXE%" >nul
if errorlevel 1 goto replace_failed
if exist "%TEMP_EXE%" goto replace_failed
if not exist "%OUT_EXE%" goto replace_failed
rmdir "%TEMP_DIR%" >nul 2>&1
if exist "%TEMP_DIR%" (
    echo ERROR: The build succeeded, but its empty temporary directory could not be removed:
    echo ERROR: "%TEMP_DIR%"
    exit /b 1
)

echo Built: "%OUT_EXE%"
exit /b 0

:replace_failed
call :cleanup_temp_exe
echo ERROR: Could not replace "%OUT_EXE%". The existing executable was preserved.
echo ERROR: If TinyHwBar is running, exit it from the tray and try again.
exit /b 1

:cleanup_temp_exe
if defined TEMP_EXE if exist "%TEMP_EXE%" del /f /q "%TEMP_EXE%" >nul 2>&1
if defined TEMP_DIR if exist "%TEMP_DIR%" rmdir "%TEMP_DIR%" >nul 2>&1
exit /b 0
