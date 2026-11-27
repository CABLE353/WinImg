# WinImg

A lightweight, portable Windows disk cloning, VHDX imaging, and boot/recovery utility built with native Win32 APIs and .NET 4.8. Zero external dependencies.

## Features
- **Disk-to-Disk Cloning:** Native 4 MB buffered raw sector streaming with live speed (MB/s), ETA, and safe cancellation.
- **Automated Storage Upgrade Support:** Automatically assigns a fresh GPT/MBR Disk ID, repairs the UEFI bootloader (`bcdboot`), moves the Recovery partition to the end of the drive, and expands the OS partition to account for its expanded storage.
- **VHDX Imaging:** Back up any physical disk to a mountable `.vhdx` image and restore `.vhdx` images back to bare metal.
- **Standalone Rescue Tool Management:** One-click UEFI boot repair and Recovery partition relocation (`reagentc` for live system disks, `robocopy` for secondary drives): *Not supported in WinRE or WinPE*.
- **Visual Disk Map:** Color-coded partition layout inspector with built-in safety locks to prevent overwriting the active System Disk. *I am not responsible for ANY accidents or otherwise!*

## Requirements
- Windows 10 or Windows 11 (64-bit)
- Administrator privileges
- .NET Framework 4.8 (pre-installed on Windows)

## Quick Start
1. **Build:** Run `build.bat` to compile `WinImg.exe` using Windows' built-in C# compiler (`csc.exe`).
2. **Run Portable:** Launch `WinImg.exe` directly from any folder or USB drive.
3. **Optional Install:** Run `Install-WinImg.bat` as Administrator to install to `System32` (`Win+R` → `winimg`), add File Explorer right-click menus, and create a Start Menu shortcut. Run `Uninstall-WinImg.bat` as Administrator to remove.

## Safe Testing
Run `Setup-TestLab.bat` as Administrator to attach a 2 GB mock Windows GPT source disk and a 4 GB blank target disk for safe testing. Run `Cleanup-TestLab.bat` when finished.

## License
MIT License
