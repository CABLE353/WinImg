@echo off
title WinImg - Cleanup Virtual Test Lab
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% neq 0 (echo [!] Please run as Administrator. & pause & exit /b 1)

set "LABDIR=%~dp0TestLab"
set "SRC_VHD=%LABDIR%\Test_Source_2GB.vhdx"
set "TGT_VHD=%LABDIR%\Test_Target_4GB.vhdx"

echo [WORKING] Detaching virtual test disks...
if exist "%SRC_VHD%" (
    (echo select vdisk file="%SRC_VHD%" & echo detach vdisk) > "%temp%\lab_det.txt"
    diskpart /s "%temp%\lab_det.txt" >nul 2>&1
)
if exist "%TGT_VHD%" (
    (echo select vdisk file="%TGT_VHD%" & echo detach vdisk) > "%temp%\lab_det.txt"
    diskpart /s "%temp%\lab_det.txt" >nul 2>&1
)
del "%temp%\lab_det.txt" 2>nul

if exist "%LABDIR%" rd /s /q "%LABDIR%"
echo [SUCCESS] Test Lab detached and deleted.
pause
