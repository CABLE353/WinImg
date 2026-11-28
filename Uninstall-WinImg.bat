@echo off
title Uninstall WinImg v1.0
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] Please right-click Uninstall-WinImg.bat and select "Run as Administrator".
    pause
    exit /b 1
)

echo [WORKING] Removing WinImg.exe, Explorer context menus, and shortcuts...
if exist "%SystemRoot%\System32\WinImg.exe" del /f /q "%SystemRoot%\System32\WinImg.exe"
if exist "%ProgramData%\Microsoft\Windows\Start Menu\Programs\Administrative Tools\WinImg.lnk" del /f /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\Administrative Tools\WinImg.lnk"
if exist "%LocalAppData%\Microsoft\Windows\WinX\Group3\04b - WinImg.lnk" del /f /q "%LocalAppData%\Microsoft\Windows\WinX\Group3\04b - WinImg.lnk"
if exist "%LocalAppData%\Microsoft\Windows\WinX\Group3\03b - WinImg.lnk" del /f /q "%LocalAppData%\Microsoft\Windows\WinX\Group3\03b - WinImg.lnk"

reg delete "HKCR\Drive\shell\WinImg" /f >nul 2>&1
reg delete "HKCU\Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\WinImg" /f >nul 2>&1

echo [SUCCESS] WinImg has been uninstalled cleanly.
pause
