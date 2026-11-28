@echo off
title Building WinImg v1.0...
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] Could not find .NET 4.8 C# compiler at %CSC%
    pause
    exit /b 1
)

echo [WORKING] Compiling WinImg.exe (v1.0.0.0) with custom icon and metadata...
"%CSC%" /nologo /target:winexe /optimize+ /platform:x64 /out:WinImg.exe /win32manifest:app.manifest /win32icon:WinImg.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll WinImg.cs

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Compilation failed.
    pause
    exit /b 1
)

echo [SUCCESS] Built WinImg.exe v1.0 successfully!
echo.
set /p run="Launch WinImg.exe now? (y/n): "
if /i "%run%"=="y" start "" "WinImg.exe"
