@echo off
title WinImg - Create Virtual Test Lab
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% neq 0 (echo [!] Please run as Administrator. & pause & exit /b 1)

set "LABDIR=%~dp0TestLab"
if not exist "%LABDIR%" mkdir "%LABDIR%"

set "SRC_VHD=%LABDIR%\Test_Source_2GB.vhdx"
set "TGT_VHD=%LABDIR%\Test_Target_4GB.vhdx"

echo [1/4] Creating 2 GB Mock Windows Source VHDX (EFI + OS + Recovery)...
(
echo create vdisk file="%SRC_VHD%" maximum=2048 type=expandable
echo attach vdisk
echo convert gpt
echo create partition efi size=100
echo format quick fs=fat32 label="SYSTEM"
echo create partition primary size=1500
echo format quick fs=ntfs label="MockWindows"
echo assign letter=V
echo create partition primary
echo format quick fs=ntfs label="Recovery"
echo assign letter=W
echo set id="de94bba4-06d1-4d40-a16a-bfd50179d6ac"
echo gpt attributes=0x8000000000000001
) > "%temp%\lab_src.txt"
diskpart /s "%temp%\lab_src.txt" >nul

echo [2/4] Populating mock Windows UEFI boot files (for real bcdboot testing)...
if exist "V:\" (
    mkdir "V:\Windows\System32" 2>nul
    echo Mock Windows File > "V:\Windows\explorer.exe"
    echo Test payload data > "V:\README_TEST.txt"
    if exist "%SystemRoot%\Boot" (
        robocopy "%SystemRoot%\Boot" "V:\Windows\Boot" /E /NFL /NDL /NJH /NJS /NP >nul
    )
    if exist "%SystemRoot%\System32\winload.efi" copy /y "%SystemRoot%\System32\winload.efi" "V:\Windows\System32\" >nul
    if exist "%SystemRoot%\System32\winresume.efi" copy /y "%SystemRoot%\System32\winresume.efi" "V:\Windows\System32\" >nul
    (echo select volume V & echo remove letter=V) > "%temp%\lab_unm.txt"
    diskpart /s "%temp%\lab_unm.txt" >nul
    del "%temp%\lab_unm.txt" 2>nul
)

if exist "W:\" (
    mkdir "W:\Recovery\WindowsRE" 2>nul
    echo Mock WinRE Payload > "W:\Recovery\WindowsRE\Winre.wim"
    (echo select volume W & echo remove letter=W) > "%temp%\lab_unm2.txt"
    diskpart /s "%temp%\lab_unm2.txt" >nul
    del "%temp%\lab_unm2.txt" 2>nul
)

echo [3/4] Creating 4 GB Blank Target VHDX...
(
echo create vdisk file="%TGT_VHD%" maximum=4096 type=expandable
echo attach vdisk
echo convert gpt
) > "%temp%\lab_tgt.txt"
diskpart /s "%temp%\lab_tgt.txt" >nul

del "%temp%\lab_src.txt" "%temp%\lab_tgt.txt" 2>nul

echo [4/4] Done! Both virtual disks are now attached and visible in WinImg.
echo       - Source: 2.00 GB (EFI + MockWindows with real Boot\EFI + Recovery)
echo       - Target: 4.00 GB (Blank GPT)
echo.
echo Press F5 inside WinImg to see them. Run Cleanup-TestLab.bat when finished.
pause
