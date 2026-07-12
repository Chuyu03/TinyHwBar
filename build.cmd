@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FRAMEWORK=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "OUT_DIR=%ROOT%outputs"
set "OUT_EXE=%OUT_DIR%\TinyHwBar.exe"

if not exist "%CSC%" (
    echo ERROR: .NET Framework compiler not found: "%CSC%"
    exit /b 1
)

if not exist "%OUT_DIR%" (
    mkdir "%OUT_DIR%"
    if errorlevel 1 (
        echo ERROR: Could not create output directory: "%OUT_DIR%"
        exit /b 1
    )
)

if exist "%OUT_EXE%" (
    del /q "%OUT_EXE%"
    if exist "%OUT_EXE%" (
        echo ERROR: Could not replace "%OUT_EXE%". Close TinyHwBar and try again.
        exit /b 1
    )
)

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
    /out:"%OUT_EXE%" ^
    /reference:"%FRAMEWORK%\System.dll" ^
    /reference:"%FRAMEWORK%\System.Core.dll" ^
    /reference:"%FRAMEWORK%\System.Drawing.dll" ^
    /reference:"%FRAMEWORK%\System.Windows.Forms.dll" ^
    "%ROOT%src\AssemblyInfo.cs" ^
    "%ROOT%src\Program.cs" ^
    "%ROOT%src\NativeMethods.cs" ^
    "%ROOT%src\HardwareSampler.cs" ^
    "%ROOT%src\SettingsStore.cs" ^
    "%ROOT%src\MonitorForm.cs"

if errorlevel 1 (
    echo ERROR: Build failed.
    exit /b 1
)

if not exist "%OUT_EXE%" (
    echo ERROR: Compiler completed without producing "%OUT_EXE%".
    exit /b 1
)

echo Built: "%OUT_EXE%"
exit /b 0
