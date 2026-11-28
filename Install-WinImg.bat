@echo off
title Install WinImg v1.0
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] Please right-click Install-WinImg.bat and select "Run as Administrator".
    pause
    exit /b 1
)

if not exist "WinImg.exe" (
    echo [!] WinImg.exe not found in this folder. Run build.bat first.
    pause
    exit /b 1
)

echo [1/4] Installing WinImg.exe to %SystemRoot%\System32\ (next to notepad.exe)...
copy /y "WinImg.exe" "%SystemRoot%\System32\WinImg.exe" >nul
if %errorlevel% neq 0 (
    echo [ERROR] Failed to copy WinImg.exe to System32.
    pause
    exit /b 1
)

echo [2/4] Adding Windows Explorer right-click context menu (Drives ^& This PC)...
reg add "HKCR\Drive\shell\WinImg" /ve /d "Open in WinImg" /f >nul
reg add "HKCR\Drive\shell\WinImg" /v "Icon" /d "%SystemRoot%\System32\WinImg.exe,0" /f >nul
reg add "HKCR\Drive\shell\WinImg\command" /ve /d "\"%SystemRoot%\System32\WinImg.exe\"" /f >nul

reg add "HKCU\Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\WinImg" /ve /d "WinImg Disk Cloner" /f >nul
reg add "HKCU\Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\WinImg" /v "Icon" /d "%SystemRoot%\System32\WinImg.exe,0" /f >nul
reg add "HKCU\Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell\WinImg\command" /ve /d "\"%SystemRoot%\System32\WinImg.exe\"" /f >nul

echo [3/4] Adding Start Menu shortcut (Windows Administrative Tools)...
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%ProgramData%\Microsoft\Windows\Start Menu\Programs\Administrative Tools\WinImg.lnk'); $s.TargetPath = '%SystemRoot%\System32\WinImg.exe'; $s.IconLocation = '%SystemRoot%\System32\WinImg.exe,0'; $s.Description = 'WinImg — Portable Windows Disk Cloner & Imaging Utility'; $s.Save()"

echo [4/4] Creating and signing Win+X Menu shortcut (under Disk Management)...
set "WINX_DIR=%LocalAppData%\Microsoft\Windows\WinX\Group3"
if exist "%WINX_DIR%" (
    if exist "%WINX_DIR%\04b - WinImg.lnk" del /f /q "%WINX_DIR%\04b - WinImg.lnk"
    powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%WINX_DIR%\03b - WinImg.lnk'); $s.TargetPath = '%SystemRoot%\System32\WinImg.exe'; $s.IconLocation = '%SystemRoot%\System32\WinImg.exe,0'; $s.Description = 'WinImg Disk Cloner'; $s.Save()"
    start "" /wait "%SystemRoot%\System32\WinImg.exe" /signwinx "%WINX_DIR%\03b - WinImg.lnk" "%SystemRoot%\System32\WinImg.exe"
    if %errorlevel% equ 0 (
        echo       [SUCCESS] Win+X shortcut hashed and signed natively!
    ) else (
        echo       [WARN] Could not sign Win+X shortcut.
    )
) else (
    echo       [WARN] WinX Group3 folder not found for this user profile.
)

echo.
echo [DONE] WinImg v1.0 installed!
set /p rest="Restart Windows Explorer now so Win+X refreshes immediately? (y/n): "
if /i "%rest%"=="y" (
    taskkill /f /im explorer.exe >nul 2>&1
    start explorer.exe
)
pause
