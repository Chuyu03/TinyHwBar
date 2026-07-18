@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FRAMEWORK=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "POWERSHELL=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
set "COMPILE_ONLY=0"

if "%~1"=="" goto arguments_valid
if /i not "%~1"=="--compile-only" goto invalid_arguments
if not "%~2"=="" goto invalid_arguments
set "COMPILE_ONLY=1"

:arguments_valid
:choose_test_exe
set "TEST_EXE=%TEMP%\TinyHwBar.Tests.%RANDOM%.%RANDOM%.exe"
if exist "%TEST_EXE%" goto choose_test_exe

if not exist "%CSC%" (
    echo ERROR: .NET Framework compiler not found: "%CSC%"
    exit /b 1
)

if not exist "%POWERSHELL%" (
    echo ERROR: Windows PowerShell 5.1 not found: "%POWERSHELL%"
    exit /b 1
)

"%POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tests\InstallerScriptTests.ps1"
if errorlevel 1 (
    echo ERROR: PowerShell script safety tests failed.
    exit /b 1
)

"%CSC%" ^
    /nologo ^
    /langversion:5 ^
    /target:exe ^
    /platform:x64 ^
    /optimize+ ^
    /debug- ^
    /warn:4 ^
    /warnaserror+ ^
    /codepage:65001 ^
    /utf8output ^
    /main:TinyHwBar.Tests.TestProgram ^
    /out:"%TEST_EXE%" ^
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
    "%ROOT%src\MonitorForm.cs" ^
    "%ROOT%tests\V2ServiceTests.cs" ^
    "%ROOT%tests\V2LocalTests.cs"

if errorlevel 1 (
    echo ERROR: Test compilation failed.
    call :cleanup_test_exe
    exit /b 1
)

if "%COMPILE_ONLY%"=="1" (
    echo Compiled local tests successfully. Execution was skipped by request.
    call :cleanup_test_exe
    if errorlevel 1 exit /b 1
    exit /b 0
)

"%TEST_EXE%"
set "TEST_EXIT=%ERRORLEVEL%"
call :cleanup_test_exe
if errorlevel 1 exit /b 1

if not "%TEST_EXIT%"=="0" (
    echo ERROR: The test process did not complete successfully. Exit code: %TEST_EXIT%
    echo ERROR: Review the preceding output and Windows Code Integrity events to distinguish failed assertions from a policy block.
    exit /b %TEST_EXIT%
)

exit /b 0

:invalid_arguments
echo ERROR: Unsupported test arguments.
echo Usage: test.cmd [--compile-only]
exit /b 2

:cleanup_test_exe
if not exist "%TEST_EXE%" exit /b 0
del /q "%TEST_EXE%" >nul 2>&1
if exist "%TEST_EXE%" (
    echo ERROR: Could not remove the temporary test executable: "%TEST_EXE%"
    exit /b 1
)
exit /b 0
