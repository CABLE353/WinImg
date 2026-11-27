using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using System.Reflection;

[assembly: AssemblyTitle("WinImg")]
[assembly: AssemblyDescription("Portable Windows Disk Cloner & Imaging Utility")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCompany("WinImg Project")]
[assembly: AssemblyProduct("WinImg")]
[assembly: AssemblyCopyright("Copyright (c) 2026 WinImg")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]


namespace WinImg
{
    public class PartitionInfo
    {
        public int PartitionNumber { get; set; }
        public ulong Offset { get; set; }
        public ulong Size { get; set; }
        public string DriveLetter { get; set; }
        public string Label { get; set; }
        public string FileSystem { get; set; }
        public string TypeName { get; set; }
        public string GptType { get; set; }
        public bool IsUnallocated { get; set; }
        public bool IsDynamic { get; set; }
    }

    public class DiskInfo
    {
        public int Number { get; set; }
        public string Model { get; set; }
        public string InterfaceType { get; set; }
        public string PartitionStyle { get; set; }
        public ulong Size { get; set; }
        public string Status { get; set; }
        public bool IsSystemDisk { get; set; }
        public bool IsDynamic { get; set; }
        public List<PartitionInfo> Partitions { get; set; }

        public DiskInfo()
        {
            Partitions = new List<PartitionInfo>();
        }

        public override string ToString()
        {
            return string.Format("Disk {0} — {1} ({2:F2} GB) [{3}]{4}",
                Number, Model, Size / 1073741824.0, PartitionStyle, IsSystemDisk ? " [SYSTEM]" : "");
        }
    }

    public static class NativeDiskIO
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        public static string RunCommand(string exe, string args, out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                return (stdout + "\r\n" + stderr).Trim();
            }
        }

        public static string RunDiskpart(params string[] lines)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "winimg_dp_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            try
            {
                File.WriteAllLines(tmp, lines);
                int code;
                return RunCommand("diskpart.exe", "/s \"" + tmp + "\"", out code);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static string GetTrulyFreeDriveLetter(params string[] exclude)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exclude != null)
            {
                foreach (string ex in exclude)
                    if (!string.IsNullOrEmpty(ex)) used.Add(ex.Substring(0, 1));
            }

            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                    used.Add(di.Name.Substring(0, 1));
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Network"))
                {
                    if (key != null)
                    {
                        foreach (string sub in key.GetSubKeyNames())
                            if (sub.Length == 1) used.Add(sub);
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\Microsoft\\Windows\\Storage", "SELECT DriveLetter FROM MSFT_Partition"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        if (obj["DriveLetter"] != null)
                        {
                            char c = Convert.ToChar(obj["DriveLetter"]);
                            if (c != '\0' && char.IsLetter(c)) used.Add(c.ToString());
                        }
                    }
                }
            }
            catch { }

            for (int code = 90; code >= 70; code--) // Z down to F
            {
                string cand = ((char)code).ToString();
                if (!used.Contains(cand) && !Directory.Exists(cand + ":\\"))
                    return cand;
            }
            return null;
        }

        public static string StreamRawDisk(int srcDiskNum, int tgtDiskNum, ulong totalBytes,
            Func<bool> isCancelled, Action<int, string, string> updateUi)
        {
            string srcPath = @"\\.\PhysicalDrive" + srcDiskNum;
            string tgtPath = @"\\.\PhysicalDrive" + tgtDiskNum;

            using (SafeFileHandle hSrc = CreateFile(srcPath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero))
            using (SafeFileHandle hTgt = CreateFile(tgtPath, GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (hSrc.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Source " + srcPath);
                if (hTgt.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Target " + tgtPath);

                const int BUFFER_SIZE = 4 * 1024 * 1024;
                byte[] buffer = new byte[BUFFER_SIZE];
                ulong copiedBytes = 0;

                Stopwatch sw = Stopwatch.StartNew();
                long lastUiTick = 0;

                while (copiedBytes < totalBytes)
                {
                    if (isCancelled())
                        return "Operation cancelled by user.";

                    uint toRead = (uint)Math.Min((ulong)BUFFER_SIZE, totalBytes - copiedBytes);
                    uint bytesRead = 0;
                    if (!ReadFile(hSrc, buffer, toRead, out bytesRead, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed on Source disk.");

                    if (bytesRead == 0) break;

                    uint bytesWritten = 0;
                    if (!WriteFile(hTgt, buffer, bytesRead, out bytesWritten, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile failed on Target disk.");

                    copiedBytes += bytesWritten;

                    if (sw.ElapsedMilliseconds - lastUiTick >= 200 || copiedBytes >= totalBytes)
                    {
                        lastUiTick = sw.ElapsedMilliseconds;
                        double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        double mbPerSec = (copiedBytes / 1048576.0) / sec;
                        double pct = (double)copiedBytes / (double)totalBytes;
                        double remSec = mbPerSec > 0.01 ? ((totalBytes - copiedBytes) / 1048576.0) / mbPerSec : 0;
                        TimeSpan eta = TimeSpan.FromSeconds(remSec);

                        string stats = string.Format("{0:F1}%  |  {1:F2} / {2:F2} GB  |  {3:F1} MB/s  |  ETA {4:mm\\:ss}",
                            pct * 100.0, copiedBytes / 1073741824.0, totalBytes / 1073741824.0, mbPerSec, eta);
                        updateUi((int)(pct * 1000.0), null, stats);
                    }
                }
            }
            return null;
        }
    }

    public static class StorageOperations
    {
        public static DiskInfo GetFreshDisk(int diskNumber)
        {
            return MainForm.LoadAllDisksStatic().FirstOrDefault(d => d.Number == diskNumber);
        }

        public static int FindNewAttachedDisk(HashSet<int> beforeDiskNumbers)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                List<DiskInfo> now = MainForm.LoadAllDisksStatic();
                foreach (DiskInfo d in now)
                {
                    if (!beforeDiskNumbers.Contains(d.Number))
                        return d.Number;
                }
                Thread.Sleep(500);
            }
            return -1;
        }

        public static void DetachVhdx(string vhdxPath)
        {
            if (string.IsNullOrEmpty(vhdxPath)) return;
            NativeDiskIO.RunDiskpart(
                "select vdisk file=\"" + vhdxPath + "\"",
                "detach vdisk"
            );
        }

        public static void RepairUefiBoot(int diskNumber, Action<string> log)
        {
            DiskInfo disk = GetFreshDisk(diskNumber);
            if (disk == null)
            {
                log("[ERROR] Could not inspect Disk " + diskNumber + ".");
                return;
            }
            if (disk.IsSystemDisk)
            {
                log("[WARN] Skipping UEFI BCD repair on active System Disk " + diskNumber + ".");
                return;
            }
            if (disk.PartitionStyle.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) < 0)
            {
                log("[INFO] Disk " + diskNumber + " is not GPT (" + disk.PartitionStyle + "). Skipping UEFI bcdboot.");
                return;
            }

            PartitionInfo efi = disk.Partitions.FirstOrDefault(p => !p.IsUnallocated &&
                p.GptType == "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}");
            PartitionInfo main = disk.Partitions.Where(p => !p.IsUnallocated).OrderByDescending(p => p.Size).FirstOrDefault();

            if (efi == null || main == null)
            {
                log("[WARN] Could not locate both EFI and main OS partitions on Disk " + diskNumber + ". Skipping bcdboot.");
                return;
            }

            string efiLtr = NativeDiskIO.GetTrulyFreeDriveLetter();
            bool assignedWin = string.IsNullOrEmpty(main.DriveLetter);
            string winLtr = assignedWin ? NativeDiskIO.GetTrulyFreeDriveLetter(efiLtr) : main.DriveLetter;

            if (string.IsNullOrEmpty(efiLtr) || string.IsNullOrEmpty(winLtr))
            {
                log("[ERROR] Not enough free drive letters to mount EFI/OS partitions.");
                return;
            }

            log(string.Format("[BOOT] Mounting EFI ({0}:) and OS ({1}:) on Disk {2}...", efiLtr, winLtr, diskNumber));
            List<string> mountCmds = new List<string>
            {
                "select disk " + diskNumber,
                "select partition " + efi.PartitionNumber,
                "assign letter=" + efiLtr
            };
            if (assignedWin)
            {
                mountCmds.Add("select partition " + main.PartitionNumber);
                mountCmds.Add("assign letter=" + winLtr);
            }
            NativeDiskIO.RunDiskpart(mountCmds.ToArray());
            Thread.Sleep(1500);

            try
            {
                if (!Directory.Exists(winLtr + ":\\Windows"))
                {
                    log("[INFO] No \\Windows directory found on " + winLtr + ":. Skipping bcdboot.");
                    return;
                }

                int code;
                NativeDiskIO.RunCommand("bcdboot.exe",
                    string.Format("{0}:\\Windows /s {1}: /f UEFI", winLtr, efiLtr), out code);

                if (code != 0)
                {
                    log("[BOOT] Initial bcdboot returned code " + code + ". Formatting EFI (" + efiLtr + ":) as FAT32 and retrying...");
                    int fmtCode;
                    NativeDiskIO.RunCommand("cmd.exe", string.Format("/c format {0}: /fs:fat32 /q /y", efiLtr), out fmtCode);
                    Thread.Sleep(1000);
                    NativeDiskIO.RunCommand("bcdboot.exe",
                        string.Format("{0}:\\Windows /s {1}: /f UEFI", winLtr, efiLtr), out code);
                }

                if (code == 0)
                    log("[SUCCESS] UEFI Bootloader (BCD) rebuilt cleanly on Disk " + diskNumber + ".");
                else
                    log("[WARN] bcdboot returned code " + code + ". Ensure \\Windows\\Boot\\EFI and winload.efi exist on the OS partition.");
            }
            finally
            {
                List<string> cleanCmds = new List<string>
                {
                    "select disk " + diskNumber,
                    "select partition " + efi.PartitionNumber,
                    "remove letter=" + efiLtr
                };
                if (assignedWin)
                {
                    cleanCmds.Add("select partition " + main.PartitionNumber);
                    cleanCmds.Add("remove letter=" + winLtr);
                }
                NativeDiskIO.RunDiskpart(cleanCmds.ToArray());
            }
        }

        public static void RelocateRecoveryAndExtend(int diskNumber, Action<string> log)
        {
            DiskInfo disk = GetFreshDisk(diskNumber);
            if (disk == null)
            {
                log("[ERROR] Could not inspect Disk " + diskNumber + ".");
                return;
            }
            if (disk.IsDynamic)
            {
                log("[ERROR] Disk " + diskNumber + " is a Dynamic Disk (LDM). Convert to Basic first.");
                return;
            }

            List<PartitionInfo> realParts = disk.Partitions.Where(p => !p.IsUnallocated).OrderBy(p => p.Offset).ToList();
            PartitionInfo main = realParts.OrderByDescending(p => p.Size).FirstOrDefault();
            if (main == null)
            {
                log("[ERROR] No main OS partition found on Disk " + diskNumber + ".");
                return;
            }

            List<PartitionInfo> trailing = realParts.Where(p => p.Offset > main.Offset).ToList();

            if (trailing.Count == 0)
            {
                log("[EXPAND] No trailing partition after OS. Extending Partition " + main.PartitionNumber + " to maximum size...");
                NativeDiskIO.RunDiskpart(
                    "select disk " + diskNumber,
                    "select partition " + main.PartitionNumber,
                    "extend"
                );
                log("[SUCCESS] Main OS partition extended.");
                return;
            }

            if (trailing.Count > 1)
            {
                log("[WARN] Multiple partitions exist after the main OS partition. Skipping auto-move to prevent data loss.");
                return;
            }

            PartitionInfo rec = trailing[0];
            ulong unallocAfterRec = disk.Size > (rec.Offset + rec.Size) ? (disk.Size - (rec.Offset + rec.Size)) : 0UL;
            if (unallocAfterRec < 100UL * 1048576UL)
            {
                log("[INFO] Less than 100 MB of unallocated space after Recovery. Nothing to expand.");
                return;
            }

            bool isGpt = disk.PartitionStyle.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0;
            ulong recSizeBytes = rec.Size;
            ulong recReserveBytes = (disk.Size > 20UL * 1073741824UL) ? Math.Max(recSizeBytes, 1100UL * 1048576UL) : recSizeBytes;
            int recReserveMB = Math.Max(100, (int)Math.Ceiling(recReserveBytes / 1048576.0));

            if (disk.IsSystemDisk)
            {
                log("[EXPAND] Live System Disk detected. Staging WinRE onto C: via reagentc /disable...");
                int reCode;
                NativeDiskIO.RunCommand("reagentc.exe", "/disable", out reCode);

                log("[EXPAND] Removing old Recovery partition (Partition " + rec.PartitionNumber + ")...");
                NativeDiskIO.RunDiskpart(
                    "select disk " + diskNumber,
                    "select partition " + rec.PartitionNumber,
                    "delete partition override"
                );
                Thread.Sleep(1500);

                log(string.Format("[EXPAND] Extending C: and reserving {0} MB at the end for Recovery...", recReserveMB));
                NativeDiskIO.RunDiskpart(
                    "select disk " + diskNumber,
                    "select partition " + main.PartitionNumber,
                    "extend",
                    "shrink desired=" + recReserveMB
                );
                Thread.Sleep(1500);

                log("[EXPAND] Recreating Recovery partition at the end of Disk " + diskNumber + "...");
                if (isGpt)
                {
                    NativeDiskIO.RunDiskpart(
                        "select disk " + diskNumber,
                        "create partition primary id=de94bba4-06d1-4d40-a16a-bfd50179d6ac",
                        "gpt attributes=0x8000000000000001",
                        "format quick fs=ntfs label=\"Recovery\""
                    );
                }
                else
                {
                    NativeDiskIO.RunDiskpart(
                        "select disk " + diskNumber,
                        "create partition primary id=27",
                        "format quick fs=ntfs label=\"Recovery\""
                    );
                }

                log("[EXPAND] Re-enabling WinRE...");
                NativeDiskIO.RunCommand("reagentc.exe", "/enable", out reCode);
                log("[SUCCESS] Live System Disk expanded and Recovery relocated!");
                return;
            }

            string ltr = NativeDiskIO.GetTrulyFreeDriveLetter();
            if (string.IsNullOrEmpty(ltr))
            {
                log("[ERROR] No completely free drive letter available (Z-F in use or mapped).");
                return;
            }

            string backupDir = Path.Combine(Path.GetTempPath(), "WinImg_RecBackup_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(backupDir);

            log(string.Format("[EXPAND] Backing up Recovery partition ({0} MB) via {1}:...", (int)(rec.Size / 1048576UL), ltr));
            NativeDiskIO.RunDiskpart(
                "select disk " + diskNumber,
                "select partition " + rec.PartitionNumber,
                "assign letter=" + ltr
            );
            Thread.Sleep(1500);

            if (!Directory.Exists(ltr + ":\\"))
            {
                log("[ERROR] Failed to mount Recovery partition to " + ltr + ":. Aborting safely.");
                try { Directory.Delete(backupDir, true); } catch { }
                return;
            }

            int rc;
            string rcOut = NativeDiskIO.RunCommand("robocopy.exe",
                string.Format("{0}:\\ \"{1}\" /E /COPY:DAT /B /R:1 /W:1 /XD \"System Volume Information\" \"$RECYCLE.BIN\" /NFL /NDL /NJH /NJS /NP", ltr, backupDir),
                out rc);

            NativeDiskIO.RunDiskpart(
                "select disk " + diskNumber,
                "select partition " + rec.PartitionNumber,
                "remove letter=" + ltr
            );

            if (rc >= 8)
            {
                log("[ERROR] Robocopy backup failed (code " + rc + "). Original Recovery partition untouched.");
                if (!string.IsNullOrEmpty(rcOut)) log(rcOut);
                try { Directory.Delete(backupDir, true); } catch { }
                return;
            }

            log("[EXPAND] Removing old Recovery partition and extending OS partition...");
            NativeDiskIO.RunDiskpart(
                "select disk " + diskNumber,
                "select partition " + rec.PartitionNumber,
                "delete partition override"
            );
            Thread.Sleep(1500);

            NativeDiskIO.RunDiskpart(
                "select disk " + diskNumber,
                "select partition " + main.PartitionNumber,
                "extend",
                "shrink desired=" + recReserveMB
            );
            Thread.Sleep(1500);

            ltr = NativeDiskIO.GetTrulyFreeDriveLetter();
            if (string.IsNullOrEmpty(ltr))
            {
                log("[ERROR] No free drive letter available to recreate Recovery. Backup preserved at: " + backupDir);
                return;
            }

            log(string.Format("[EXPAND] Recreating Recovery partition at the end of Disk {0} via {1}:...", diskNumber, ltr));
            NativeDiskIO.RunDiskpart(
                "select disk " + diskNumber,
                "create partition primary",
                "format quick fs=ntfs label=\"Recovery\"",
                "assign letter=" + ltr
            );
            Thread.Sleep(1500);

            string rcRestoreOut = NativeDiskIO.RunCommand("robocopy.exe",
                string.Format("\"{0}\" {1}:\\ /E /COPY:DAT /B /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", backupDir, ltr),
                out rc);

            DiskInfo updated = GetFreshDisk(diskNumber);
            PartitionInfo newRec = updated != null
                ? updated.Partitions.Where(p => !p.IsUnallocated).OrderBy(p => p.Offset).LastOrDefault()
                : null;

            if (newRec != null)
            {
                List<string> attrCmds = new List<string>
                {
                    "select disk " + diskNumber,
                    "select partition " + newRec.PartitionNumber,
                    "remove letter=" + ltr
                };
                if (isGpt)
                {
                    attrCmds.Add("set id=\"de94bba4-06d1-4d40-a16a-bfd50179d6ac\"");
                    attrCmds.Add("gpt attributes=0x8000000000000001");
                }
                else
                {
                    attrCmds.Add("set id=27");
                }
                NativeDiskIO.RunDiskpart(attrCmds.ToArray());
            }

            if (rc < 8)
            {
                try { Directory.Delete(backupDir, true); } catch { }
                log("[SUCCESS] Recovery partition relocated to the end of Disk " + diskNumber + " and OS partition expanded!");
            }
            else
            {
                log("[WARN] Robocopy restore returned code " + rc + ". Backup preserved at: " + backupDir);
                if (!string.IsNullOrEmpty(rcRestoreOut)) log(rcRestoreOut);
            }
        }
    }

    public class TaskLogDialog : Form
    {
        private TextBox _txtLog;
        private Button _btnClose;
        private ProgressBar _bar;
        private volatile bool _running = true;

        public TaskLogDialog(string title, Action<Action<string>> work)
        {
            this.Text = title;
            this.Size = new Size(580, 340);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = SystemFonts.MessageBoxFont;

            _bar = new ProgressBar { Left = 14, Top = 14, Width = 536, Height = 20, Style = ProgressBarStyle.Marquee };
            _txtLog = new TextBox
            {
                Left = 14,
                Top = 44,
                Width = 536,
                Height = 205,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 8.5f)
            };
            _btnClose = new Button { Text = "Close", Left = 455, Top = 260, Width = 95, Height = 28, Enabled = false };
            _btnClose.Click += delegate { this.Close(); };

            this.Controls.AddRange(new Control[] { _bar, _txtLog, _btnClose });
            this.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_running) e.Cancel = true;
            };

            this.Shown += delegate
            {
                Thread t = new Thread(delegate()
                {
                    try { work(AppendLog); }
                    catch (Exception ex) { AppendLog("[ERROR] " + ex.Message); }
                    finally
                    {
                        _running = false;
                        if (!this.IsDisposed)
                        {
                            this.BeginInvoke(new Action(delegate
                            {
                                _bar.Style = ProgressBarStyle.Blocks;
                                _bar.Value = 100;
                                _btnClose.Enabled = true;
                            }));
                        }
                    }
                });
                t.IsBackground = true;
                t.Start();
            };
        }

        private void AppendLog(string line)
        {
            if (this.IsDisposed) return;
            this.BeginInvoke(new Action(delegate { _txtLog.AppendText(line + "\r\n"); }));
        }
    }

    public class CloneDialog : Form
    {
        private ComboBox _cbSource;
        private ComboBox _cbTarget;
        private CheckBox _chkKeepUnallocated;
        private CheckBox _chkRandomizeGuid;
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private Label _lblStats;
        private Button _btnStart;
        private Button _btnCancel;

        private List<DiskInfo> _disks;
        private volatile bool _isCloning = false;
        private volatile bool _cancelRequested = false;

        public bool CloneCompleted { get; private set; }

        public CloneDialog(List<DiskInfo> disks, DiskInfo preselectedSource = null)
        {
            _disks = disks;
            this.Text = "WinImg — Disk-to-Disk Cloner";
            this.Size = new Size(570, 340);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = SystemFonts.MessageBoxFont;

            Label lblSrc = new Label { Text = "Source Disk (Read):", Left = 16, Top = 16, Width = 160 };
            _cbSource = new ComboBox { Left = 16, Top = 36, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };

            Label lblTgt = new Label { Text = "Target Disk (OVERWRITTEN):", Left = 16, Top = 70, Width = 220, ForeColor = Color.DarkRed };
            _cbTarget = new ComboBox { Left = 16, Top = 90, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };

            foreach (DiskInfo d in _disks)
            {
                _cbSource.Items.Add(d);
                if (!d.IsSystemDisk)
                    _cbTarget.Items.Add(d);
            }
            if (preselectedSource != null && _cbSource.Items.Contains(preselectedSource)) _cbSource.SelectedItem = preselectedSource;

            else if (_cbSource.Items.Count > 0) _cbSource.SelectedIndex = 0;
            if (_cbTarget.Items.Count > 0) _cbTarget.SelectedIndex = _cbTarget.Items.Count - 1;

            _chkKeepUnallocated = new CheckBox
            {
                Text = "Keep unallocated space equal to the difference in size (skip auto-expand && Recovery move)",
                Left = 16,
                Top = 126,
                Width = 525,
                Checked = false
            };

            _chkRandomizeGuid = new CheckBox
            {
                Text = "Assign fresh GPT/MBR Disk ID && rebuild UEFI Bootloader (bcdboot) after clone",
                Left = 16,
                Top = 150,
                Width = 525,
                Checked = true
            };

            _progressBar = new ProgressBar { Left = 16, Top = 186, Width = 520, Height = 24, Minimum = 0, Maximum = 1000 };
            _lblStatus = new Label { Text = "Default mode: Clones disk, repairs UEFI boot, moves Recovery to end, and extends OS.", Left = 16, Top = 218, Width = 525 };
            _lblStats = new Label { Text = "", Left = 16, Top = 238, Width = 525, ForeColor = Color.FromArgb(60, 60, 60) };

            _btnStart = new Button { Text = "Start Clone", Left = 340, Top = 266, Width = 95, Height = 28 };
            _btnCancel = new Button { Text = "Close", Left = 441, Top = 266, Width = 95, Height = 28 };

            _btnStart.Click += BtnStart_Click;
            _btnCancel.Click += BtnCancel_Click;

            this.Controls.AddRange(new Control[]
            {
                lblSrc, _cbSource, lblTgt, _cbTarget,
                _chkKeepUnallocated, _chkRandomizeGuid,
                _progressBar, _lblStatus, _lblStats, _btnStart, _btnCancel
            });

            this.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_isCloning)
                {
                    e.Cancel = true;
                    MessageBox.Show("A clone is currently in progress. Click Cancel to abort safely.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (_isCloning) { _cancelRequested = true; _lblStatus.Text = "Cancelling..."; _btnCancel.Enabled = false; }
            else { this.Close(); }
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            DiskInfo src = _cbSource.SelectedItem as DiskInfo;
            DiskInfo tgt = _cbTarget.SelectedItem as DiskInfo;

            if (src == null || tgt == null) { MessageBox.Show("Please select both a Source and a Target disk.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (src.Number == tgt.Number) { MessageBox.Show("Source and Target cannot be the same disk.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (tgt.IsSystemDisk) { MessageBox.Show("Safety Block: You cannot overwrite your active Windows System Disk.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (tgt.Size < src.Size)
            {
                MessageBox.Show(string.Format("Target disk ({0:F2} GB) is smaller than Source disk ({1:F2} GB).", tgt.Size / 1073741824.0, src.Size / 1073741824.0),
                    "Capacity Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string modeDesc = _chkKeepUnallocated.Checked ? "Verbatim Clone (extra space kept unallocated)" : "Default Clone (auto-move Recovery to end & extend OS)";
            DialogResult confirm = MessageBox.Show(
                string.Format("ALL DATA on Target Disk {0} ({1}, {2:F2} GB) will be permanently overwritten!\n\nMode: {5}\nSource: Disk {3} ({4:F2} GB) -> Target: Disk {0}",
                tgt.Number, tgt.Model, tgt.Size / 1073741824.0, src.Number, src.Size / 1073741824.0, modeDesc),
                "Confirm Disk Clone", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            _isCloning = true; _cancelRequested = false;
            _cbSource.Enabled = false; _cbTarget.Enabled = false;
            _chkKeepUnallocated.Enabled = false; _chkRandomizeGuid.Enabled = false;
            _btnStart.Enabled = false; _btnCancel.Text = "Cancel";

            bool keepUnalloc = _chkKeepUnallocated.Checked;
            bool randomizeGuid = _chkRandomizeGuid.Checked;
            Thread worker = new Thread(delegate() { RunCloneWorker(src, tgt, keepUnalloc, randomizeGuid); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateUI(int permille, string status, string stats)
        {
            if (this.IsDisposed) return;
            this.BeginInvoke(new Action(delegate
            {
                _progressBar.Value = Math.Max(0, Math.Min(1000, permille));
                if (status != null) _lblStatus.Text = status;
                if (stats != null) _lblStats.Text = stats;
            }));
        }

        private void RunCloneWorker(DiskInfo src, DiskInfo tgt, bool keepUnalloc, bool randomizeGuid)
        {
            string errorMsg = null;
            try
            {
                UpdateUI(0, string.Format("Preparing Target Disk {0} (offlining & clearing readonly)...", tgt.Number), "");
                NativeDiskIO.RunDiskpart("select disk " + tgt.Number, "offline disk", "attributes disk clear readonly");
                Thread.Sleep(1000);

                UpdateUI(0, string.Format("Cloning Disk {0} -> Disk {1}...", src.Number, tgt.Number), "Starting transfer...");
                errorMsg = NativeDiskIO.StreamRawDisk(src.Number, tgt.Number, src.Size, delegate { return _cancelRequested; }, UpdateUI);

                if (errorMsg == null)
                {
                    UpdateUI(1000, "Finalizing Target Disk (bringing online & updating Disk ID)...", null);
                    List<string> postCmds = new List<string> { "select disk " + tgt.Number, "online disk", "attributes disk clear readonly" };
                    if (randomizeGuid)
                    {
                        if (src.PartitionStyle.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0)
                            postCmds.Add("uniqueid disk id=" + Guid.NewGuid().ToString());
                        else
                            postCmds.Add("uniqueid disk id=" + new Random().Next(100000, int.MaxValue).ToString("X8"));
                    }
                    NativeDiskIO.RunDiskpart(postCmds.ToArray());
                    Thread.Sleep(2000);

                    if (randomizeGuid)
                    {
                        UpdateUI(1000, "Rebuilding UEFI Bootloader (bcdboot) on Target Disk...", null);
                        StorageOperations.RepairUefiBoot(tgt.Number, delegate(string msg) { UpdateUI(1000, msg, null); });
                    }
                    if (!keepUnalloc)
                    {
                        UpdateUI(1000, "Relocating Recovery partition to end & extending OS partition...", null);
                        StorageOperations.RelocateRecoveryAndExtend(tgt.Number, delegate(string msg) { UpdateUI(1000, msg, null); });
                    }
                    CloneCompleted = true;
                }
            }
            catch (Exception ex) { errorMsg = ex.Message; }

            this.BeginInvoke(new Action(delegate
            {
                _isCloning = false; _btnCancel.Enabled = true; _btnCancel.Text = "Close";
                if (errorMsg != null) { _lblStatus.Text = "Stopped: " + errorMsg; MessageBox.Show(errorMsg, "WinImg Cloner", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                else { _lblStatus.Text = "Clone completed successfully!"; MessageBox.Show("Disk clone finished successfully!", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }));
        }
    }

    public class BackupVhdxDialog : Form
    {
        private ComboBox _cbSource;
        private TextBox _txtPath;
        private Button _btnBrowse;
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private Label _lblStats;
        private Button _btnStart;
        private Button _btnCancel;

        private volatile bool _isRunning = false;
        private volatile bool _cancelRequested = false;

        public BackupVhdxDialog(List<DiskInfo> disks, DiskInfo preselectedSource = null)
        {
            this.Text = "WinImg — Disk-to-VHDX System Image Backup";
            this.Size = new Size(570, 290);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = SystemFonts.MessageBoxFont;

            Label lblSrc = new Label { Text = "Source Disk to Image:", Left = 16, Top = 16, Width = 180 };
            _cbSource = new ComboBox { Left = 16, Top = 36, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (DiskInfo d in disks) _cbSource.Items.Add(d);
            if (preselectedSource != null && _cbSource.Items.Contains(preselectedSource)) _cbSource.SelectedItem = preselectedSource;

            else if (_cbSource.Items.Count > 0) _cbSource.SelectedIndex = 0;

            Label lblPath = new Label { Text = "Save VHDX Image As:", Left = 16, Top = 72, Width = 180 };
            _txtPath = new TextBox { Left = 16, Top = 92, Width = 435 };
            _btnBrowse = new Button { Text = "Browse...", Left = 458, Top = 90, Width = 78, Height = 24 };
            _btnBrowse.Click += delegate
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "Virtual Hard Disk (*.vhdx)|*.vhdx";
                    sfd.DefaultExt = "vhdx";
                    sfd.FileName = "WinImg_Backup.vhdx";
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                        _txtPath.Text = sfd.FileName;
                }
            };

            _progressBar = new ProgressBar { Left = 16, Top = 136, Width = 520, Height = 24, Minimum = 0, Maximum = 1000 };
            _lblStatus = new Label { Text = "Creates a native, mountable Windows .vhdx image of the selected disk.", Left = 16, Top = 168, Width = 520 };
            _lblStats = new Label { Text = "", Left = 16, Top = 188, Width = 520, ForeColor = Color.FromArgb(60, 60, 60) };

            _btnStart = new Button { Text = "Create Backup", Left = 330, Top = 216, Width = 105, Height = 28 };
            _btnCancel = new Button { Text = "Close", Left = 441, Top = 216, Width = 95, Height = 28 };

            _btnStart.Click += BtnStart_Click;
            _btnCancel.Click += delegate
            {
                if (_isRunning) { _cancelRequested = true; _lblStatus.Text = "Cancelling..."; _btnCancel.Enabled = false; }
                else this.Close();
            };

            this.Controls.AddRange(new Control[] { lblSrc, _cbSource, lblPath, _txtPath, _btnBrowse, _progressBar, _lblStatus, _lblStats, _btnStart, _btnCancel });
            this.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_isRunning) { e.Cancel = true; MessageBox.Show("Backup in progress. Click Cancel first.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
        }

        private void UpdateUI(int permille, string status, string stats)
        {
            if (this.IsDisposed) return;
            this.BeginInvoke(new Action(delegate
            {
                _progressBar.Value = Math.Max(0, Math.Min(1000, permille));
                if (status != null) _lblStatus.Text = status;
                if (stats != null) _lblStats.Text = stats;
            }));
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            DiskInfo src = _cbSource.SelectedItem as DiskInfo;
            string vhdxPath = _txtPath.Text.Trim();

            if (src == null) { MessageBox.Show("Select a Source disk.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (string.IsNullOrEmpty(vhdxPath)) { MessageBox.Show("Choose a destination .vhdx file path.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!vhdxPath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) vhdxPath += ".vhdx";

            if (File.Exists(vhdxPath))
            {
                if (MessageBox.Show("File already exists. Overwrite it?\n" + vhdxPath, "Confirm Overwrite", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                try { StorageOperations.DetachVhdx(vhdxPath); File.Delete(vhdxPath); } catch { }
            }

            _isRunning = true; _cancelRequested = false;
            _cbSource.Enabled = false; _txtPath.Enabled = false; _btnBrowse.Enabled = false;
            _btnStart.Enabled = false; _btnCancel.Text = "Cancel";

            Thread worker = new Thread(delegate() { RunBackupWorker(src, vhdxPath); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void RunBackupWorker(DiskInfo src, string vhdxPath)
        {
            string errorMsg = null;
            bool attached = false;
            try
            {
                HashSet<int> before = new HashSet<int>(MainForm.LoadAllDisksStatic().Select(d => d.Number));
                long sizeMB = (long)Math.Ceiling(src.Size / 1048576.0) + 64;

                UpdateUI(0, string.Format("Creating expandable VHDX ({0:N0} MB)...", sizeMB), "");
                NativeDiskIO.RunDiskpart(
                    string.Format("create vdisk file=\"{0}\" maximum={1} type=expandable", vhdxPath, sizeMB),
                    "attach vdisk"
                );
                attached = true;

                int vhdDiskNum = StorageOperations.FindNewAttachedDisk(before);
                if (vhdDiskNum < 0)
                    throw new Exception("Created VHDX, but Windows did not assign a disk number.");

                NativeDiskIO.RunDiskpart("select disk " + vhdDiskNum, "offline disk", "attributes disk clear readonly");
                Thread.Sleep(1000);

                UpdateUI(0, string.Format("Streaming Disk {0} -> {1}...", src.Number, Path.GetFileName(vhdxPath)), "Starting transfer...");
                errorMsg = NativeDiskIO.StreamRawDisk(src.Number, vhdDiskNum, src.Size, delegate { return _cancelRequested; }, UpdateUI);
            }
            catch (Exception ex) { errorMsg = ex.Message; }
            finally
            {
                if (attached)
                {
                    UpdateUI(1000, "Detaching VHDX image...", null);
                    StorageOperations.DetachVhdx(vhdxPath);
                }
            }

            this.BeginInvoke(new Action(delegate
            {
                _isRunning = false; _btnCancel.Enabled = true; _btnCancel.Text = "Close";
                if (errorMsg != null) { _lblStatus.Text = "Stopped: " + errorMsg; MessageBox.Show(errorMsg, "WinImg Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                else { _lblStatus.Text = "VHDX Backup completed successfully!"; MessageBox.Show("Disk-to-VHDX backup finished!\n\nSaved to:\n" + vhdxPath, "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }));
        }
    }

    public class RestoreVhdxDialog : Form
    {
        private TextBox _txtPath;
        private Button _btnBrowse;
        private ComboBox _cbTarget;
        private CheckBox _chkKeepUnallocated;
        private CheckBox _chkRandomizeGuid;
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private Label _lblStats;
        private Button _btnStart;
        private Button _btnCancel;

        private volatile bool _isRunning = false;
        private volatile bool _cancelRequested = false;

        public bool RestoreCompleted { get; private set; }

        public RestoreVhdxDialog(List<DiskInfo> disks, DiskInfo preselectedTarget = null)
        {
            this.Text = "WinImg — VHDX-to-Disk Bare-Metal Restore";
            this.Size = new Size(570, 340);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = SystemFonts.MessageBoxFont;

            Label lblSrc = new Label { Text = "Source VHDX Image:", Left = 16, Top = 16, Width = 180 };
            _txtPath = new TextBox { Left = 16, Top = 36, Width = 435 };
            _btnBrowse = new Button { Text = "Browse...", Left = 458, Top = 34, Width = 78, Height = 24 };
            _btnBrowse.Click += delegate
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Filter = "Virtual Hard Disk (*.vhdx;*.vhd)|*.vhdx;*.vhd";
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                        _txtPath.Text = ofd.FileName;
                }
            };

            Label lblTgt = new Label { Text = "Target Disk (OVERWRITTEN):", Left = 16, Top = 70, Width = 220, ForeColor = Color.DarkRed };
            _cbTarget = new ComboBox { Left = 16, Top = 90, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (DiskInfo d in disks)
            {
                if (!d.IsSystemDisk) _cbTarget.Items.Add(d);
            }
            if (_cbTarget.Items.Count > 0) _cbTarget.SelectedIndex = _cbTarget.Items.Count - 1;

            _chkKeepUnallocated = new CheckBox
            {
                Text = "Keep unallocated space equal to the difference in size (skip auto-expand && Recovery move)",
                Left = 16,
                Top = 126,
                Width = 525,
                Checked = false
            };

            _chkRandomizeGuid = new CheckBox
            {
                Text = "Assign fresh GPT/MBR Disk ID && rebuild UEFI Bootloader (bcdboot) after restore",
                Left = 16,
                Top = 150,
                Width = 525,
                Checked = true
            };

            _progressBar = new ProgressBar { Left = 16, Top = 186, Width = 520, Height = 24, Minimum = 0, Maximum = 1000 };
            _lblStatus = new Label { Text = "Restores a .vhdx image directly onto a target disk.", Left = 16, Top = 218, Width = 525 };
            _lblStats = new Label { Text = "", Left = 16, Top = 238, Width = 525, ForeColor = Color.FromArgb(60, 60, 60) };

            _btnStart = new Button { Text = "Start Restore", Left = 330, Top = 266, Width = 105, Height = 28 };
            _btnCancel = new Button { Text = "Close", Left = 441, Top = 266, Width = 95, Height = 28 };

            _btnStart.Click += BtnStart_Click;
            _btnCancel.Click += delegate
            {
                if (_isRunning) { _cancelRequested = true; _lblStatus.Text = "Cancelling..."; _btnCancel.Enabled = false; }
                else this.Close();
            };

            this.Controls.AddRange(new Control[]
            {
                lblSrc, _txtPath, _btnBrowse, lblTgt, _cbTarget,
                _chkKeepUnallocated, _chkRandomizeGuid,
                _progressBar, _lblStatus, _lblStats, _btnStart, _btnCancel
            });

            this.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_isRunning) { e.Cancel = true; MessageBox.Show("Restore in progress. Click Cancel first.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
        }

        private void UpdateUI(int permille, string status, string stats)
        {
            if (this.IsDisposed) return;
            this.BeginInvoke(new Action(delegate
            {
                _progressBar.Value = Math.Max(0, Math.Min(1000, permille));
                if (status != null) _lblStatus.Text = status;
                if (stats != null) _lblStats.Text = stats;
            }));
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            string vhdxPath = _txtPath.Text.Trim();
            DiskInfo tgt = _cbTarget.SelectedItem as DiskInfo;

            if (string.IsNullOrEmpty(vhdxPath) || !File.Exists(vhdxPath))
            {
                MessageBox.Show("Select a valid .vhdx image file.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (tgt == null || tgt.IsSystemDisk)
            {
                MessageBox.Show("Select a valid non-System Target disk.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (MessageBox.Show(
                string.Format("ALL DATA on Target Disk {0} ({1}, {2:F2} GB) will be permanently overwritten!\n\nRestore image:\n{3}",
                tgt.Number, tgt.Model, tgt.Size / 1073741824.0, vhdxPath),
                "Confirm VHDX Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            _isRunning = true; _cancelRequested = false;
            _txtPath.Enabled = false; _btnBrowse.Enabled = false; _cbTarget.Enabled = false;
            _chkKeepUnallocated.Enabled = false; _chkRandomizeGuid.Enabled = false;
            _btnStart.Enabled = false; _btnCancel.Text = "Cancel";

            bool keepUnalloc = _chkKeepUnallocated.Checked;
            bool randomizeGuid = _chkRandomizeGuid.Checked;
            Thread worker = new Thread(delegate() { RunRestoreWorker(vhdxPath, tgt, keepUnalloc, randomizeGuid); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void RunRestoreWorker(string vhdxPath, DiskInfo tgt, bool keepUnalloc, bool randomizeGuid)
        {
            string errorMsg = null;
            bool attached = false;
            try
            {
                HashSet<int> before = new HashSet<int>(MainForm.LoadAllDisksStatic().Select(d => d.Number));
                UpdateUI(0, "Attaching source VHDX image (read-only)...", "");
                NativeDiskIO.RunDiskpart(
                    "select vdisk file=\"" + vhdxPath + "\"",
                    "attach vdisk readonly"
                );
                attached = true;

                int srcDiskNum = StorageOperations.FindNewAttachedDisk(before);
                if (srcDiskNum < 0)
                    throw new Exception("Could not attach VHDX (it may already be attached in Windows).");

                DiskInfo srcInfo = StorageOperations.GetFreshDisk(srcDiskNum);
                if (srcInfo == null || srcInfo.Size == 0)
                    throw new Exception("Could not read virtual disk size from VHDX.");

                if (tgt.Size < srcInfo.Size)
                    throw new Exception(string.Format("Target disk ({0:F2} GB) is smaller than VHDX image ({1:F2} GB).",
                        tgt.Size / 1073741824.0, srcInfo.Size / 1073741824.0));

                UpdateUI(0, string.Format("Preparing Target Disk {0}...", tgt.Number), "");
                NativeDiskIO.RunDiskpart("select disk " + tgt.Number, "offline disk", "attributes disk clear readonly");
                Thread.Sleep(1000);

                UpdateUI(0, string.Format("Restoring {0} -> Disk {1}...", Path.GetFileName(vhdxPath), tgt.Number), "Starting transfer...");
                errorMsg = NativeDiskIO.StreamRawDisk(srcDiskNum, tgt.Number, srcInfo.Size, delegate { return _cancelRequested; }, UpdateUI);

                StorageOperations.DetachVhdx(vhdxPath);
                attached = false;

                if (errorMsg == null)
                {
                    UpdateUI(1000, "Finalizing Target Disk (bringing online & updating Disk ID)...", null);
                    List<string> postCmds = new List<string> { "select disk " + tgt.Number, "online disk", "attributes disk clear readonly" };
                    if (randomizeGuid)
                    {
                        if (srcInfo.PartitionStyle.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0)
                            postCmds.Add("uniqueid disk id=" + Guid.NewGuid().ToString());
                        else
                            postCmds.Add("uniqueid disk id=" + new Random().Next(100000, int.MaxValue).ToString("X8"));
                    }
                    NativeDiskIO.RunDiskpart(postCmds.ToArray());
                    Thread.Sleep(2000);

                    if (randomizeGuid)
                    {
                        UpdateUI(1000, "Rebuilding UEFI Bootloader (bcdboot) on Target Disk...", null);
                        StorageOperations.RepairUefiBoot(tgt.Number, delegate(string msg) { UpdateUI(1000, msg, null); });
                    }
                    if (!keepUnalloc)
                    {
                        UpdateUI(1000, "Relocating Recovery partition to end & extending OS partition...", null);
                        StorageOperations.RelocateRecoveryAndExtend(tgt.Number, delegate(string msg) { UpdateUI(1000, msg, null); });
                    }
                    RestoreCompleted = true;
                }
            }
            catch (Exception ex) { errorMsg = ex.Message; }
            finally
            {
                if (attached) StorageOperations.DetachVhdx(vhdxPath);
            }

            this.BeginInvoke(new Action(delegate
            {
                _isRunning = false; _btnCancel.Enabled = true; _btnCancel.Text = "Close";
                if (errorMsg != null) { _lblStatus.Text = "Stopped: " + errorMsg; MessageBox.Show(errorMsg, "WinImg Restore", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                else { _lblStatus.Text = "Restore completed successfully!"; MessageBox.Show("VHDX restore finished successfully!", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }));
        }
    }

    public class PartitionMapPanel : Panel
    {
        private DiskInfo _disk;

        public PartitionMapPanel()
        {
            this.DoubleBuffered = true;
            this.BackColor = Color.White;
            this.BorderStyle = BorderStyle.FixedSingle;
        }

        public void SetDisk(DiskInfo disk) { _disk = disk; this.Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(Color.White);

            if (_disk == null)
            {
                TextRenderer.DrawText(g, "Select a disk above to inspect its partition layout.",
                    this.Font, this.ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            string header = string.Format("Disk {0}  —  {1}  ({2:F2} GB)  [{3}]  {4}",
                _disk.Number, _disk.Model, _disk.Size / 1073741824.0, _disk.PartitionStyle,
                _disk.IsSystemDisk ? "★ SYSTEM DISK" : "");
            using (Font hdrFont = new Font(this.Font.FontFamily, 9.5f, FontStyle.Bold))
                g.DrawString(header, hdrFont, Brushes.Black, 10, 8);

            if (_disk.Size == 0 || _disk.Partitions.Count == 0)
            {
                TextRenderer.DrawText(g, "No readable partitions (Disk may be Blank, Offline, Uninitialized, or Dynamic LDM).",
                    this.Font, new Rectangle(10, 35, this.Width - 20, this.Height - 45), Color.DarkRed,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int barX = 10, barY = 34;
            int barW = Math.Max(100, this.Width - 20);
            int barH = Math.Max(60, this.Height - 46);

            int count = _disk.Partitions.Count;
            int minW = Math.Min(85, barW / Math.Max(1, count));
            double[] widths = new double[count];
            double totalRaw = 0;

            for (int i = 0; i < count; i++)
            {
                double ratio = (double)_disk.Partitions[i].Size / (double)_disk.Size;
                widths[i] = Math.Max(minW, ratio * barW);
                totalRaw += widths[i];
            }

            double scale = barW / totalRaw;
            float curX = barX;

            using (Font titleFont = new Font(this.Font.FontFamily, 8.25f, FontStyle.Bold))
            using (Font subFont = new Font(this.Font.FontFamily, 7.75f, FontStyle.Regular))
            using (Pen borderPen = new Pen(Color.FromArgb(140, 140, 140)))
            {
                for (int i = 0; i < count; i++)
                {
                    PartitionInfo p = _disk.Partitions[i];
                    float w = (i == count - 1) ? (barX + barW - curX) : (float)(widths[i] * scale);
                    RectangleF rect = new RectangleF(curX, barY, w, barH);

                    Color stripColor = Color.FromArgb(0, 0, 128);
                    if (p.IsUnallocated) stripColor = Color.Black;
                    else if (p.IsDynamic) stripColor = Color.FromArgb(140, 140, 0);
                    else if (p.TypeName != null && (p.TypeName.IndexOf("EFI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             p.TypeName.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0))
                        stripColor = Color.FromArgb(0, 120, 120);
                    else if (p.TypeName != null && p.TypeName.IndexOf("Recovery", StringComparison.OrdinalIgnoreCase) >= 0)
                        stripColor = Color.FromArgb(100, 50, 150);

                    using (Brush bg = new SolidBrush(p.IsUnallocated ? Color.FromArgb(245, 245, 245) : Color.White))
                        g.FillRectangle(bg, rect);
                    using (Brush strip = new SolidBrush(stripColor))
                        g.FillRectangle(strip, curX, barY, w, 10);
                    g.DrawRectangle(borderPen, curX, barY, w, barH);

                    string line1 = p.IsUnallocated
                        ? "Unallocated"
                        : (!string.IsNullOrEmpty(p.DriveLetter)
                            ? string.Format("{0} ({1}:)", string.IsNullOrEmpty(p.Label) ? "Volume" : p.Label, p.DriveLetter)
                            : (string.IsNullOrEmpty(p.TypeName) ? "Partition " + p.PartitionNumber : p.TypeName));

                    string sizeStr = FormatSize(p.Size);
                    string line2 = p.IsUnallocated ? sizeStr : string.Format("{0} {1}", sizeStr, p.FileSystem ?? "").Trim();
                    string line3 = p.IsUnallocated ? "" : (p.TypeName ?? "");

                    Rectangle textRect = new Rectangle((int)curX + 4, barY + 14, Math.Max(1, (int)w - 8), barH - 18);
                    TextRenderer.DrawText(g, line1, titleFont, new Rectangle(textRect.X, textRect.Y, textRect.Width, 16),
                        Color.Black, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, line2, subFont, new Rectangle(textRect.X, textRect.Y + 16, textRect.Width, 15),
                        Color.FromArgb(50, 50, 50), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (!string.IsNullOrEmpty(line3) && barH > 55)
                        TextRenderer.DrawText(g, line3, subFont, new Rectangle(textRect.X, textRect.Y + 32, textRect.Width, 15),
                            Color.FromArgb(90, 90, 90), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    curX += w;
                }
            }
        }

        private string FormatSize(ulong bytes)
        {
            if (bytes >= 1073741824UL) return string.Format("{0:F2} GB", bytes / 1073741824.0);
            return string.Format("{0:F0} MB", bytes / 1048576.0);
        }
    }


    public static class WinXSigner
    {
        [DllImport("shlwapi.dll", PreserveSig = false)]
        private static extern void HashData(byte[] pbData, uint cbData, out uint pbHash, uint cbHash);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint ulVal;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        public static bool SignShortcut(string lnkPath, string targetPath, string arguments)
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string pfDir  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                string norm = targetPath;
                if (norm.StartsWith(sysDir, StringComparison.OrdinalIgnoreCase))
                    norm = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" + norm.Substring(sysDir.Length);
                else if (norm.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                    norm = "{F38BF404-1D43-42F2-9305-67DE0B28FC23}" + norm.Substring(winDir.Length);
                else if (norm.StartsWith(pfDir, StringComparison.OrdinalIgnoreCase))
                    norm = "{905E63B6-C1BF-494E-B29C-65B732D3D21A}" + norm.Substring(pfDir.Length);

                string blob = (norm + (arguments ?? "") + "Do not prehash links. This should only be done by the user.").ToLowerInvariant();
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(blob);
                uint hash;
                HashData(bytes, (uint)bytes.Length, out hash, 4);

                Type shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
                object linkObj = Activator.CreateInstance(shellLinkType);
                IPersistFile pf = (IPersistFile)linkObj;
                pf.Load(lnkPath, 2); // STGM_READWRITE

                IPropertyStore store = (IPropertyStore)linkObj;
                PROPERTYKEY pkey = new PROPERTYKEY
                {
                    fmtid = new Guid("FB8D2D7B-90D1-4E34-BF60-6EAC09922BBF"),
                    pid = 2
                };
                PROPVARIANT pv = new PROPVARIANT { vt = 19, ulVal = hash }; // VT_UI4 = 19
                store.SetValue(ref pkey, ref pv);
                store.Commit();
                pf.Save(lnkPath, true);
                Marshal.ReleaseComObject(linkObj);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }


    public class MainForm : Form
    {
        private ListView _diskList;

        private ContextMenuStrip _diskContextMenu;
        private ListView _partList;
        private PartitionMapPanel _mapPanel;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private List<DiskInfo> _disks = new List<DiskInfo>();

        public MainForm()
        {
            this.Text = "WinImg v1.0 — Portable Windows Disk Cloner & Imaging Utility";

            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            this.Size = new Size(980, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = SystemFonts.MessageBoxFont;

            BuildMenuAndToolbar();
            BuildMainLayout();
            BuildStatusBar();

            this.Load += delegate { RefreshDisks(); };
        }

        private void BuildMenuAndToolbar()
        {
            MenuStrip menu = new MenuStrip();

            ToolStripMenuItem fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add("&Refresh Disks (F5)", null, delegate { RefreshDisks(); });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("E&xit", null, delegate { this.Close(); });

            ToolStripMenuItem actionsMenu = new ToolStripMenuItem("&Actions");
            actionsMenu.DropDownItems.Add("&Clone Disk-to-Disk...", null, OpenCloneDialog);
            actionsMenu.DropDownItems.Add("&Backup Disk to VHDX...", null, OpenBackupDialog);
            actionsMenu.DropDownItems.Add("&Restore VHDX to Disk...", null, OpenRestoreDialog);
            actionsMenu.DropDownItems.Add(new ToolStripSeparator());
            actionsMenu.DropDownItems.Add("Relocate &Recovery && Extend OS (Selected Disk)...", null, RunRelocateSelected);
            actionsMenu.DropDownItems.Add("Repair &UEFI Bootloader (Selected Disk)...", null, RunBootRepairSelected);

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("&Help");
            helpMenu.DropDownItems.Add("&About WinImg", null, delegate
            {
                MessageBox.Show(
                    "WinImg v1.0.0.0\n\n" +
                    "Portable native Windows disk cloning, VHDX imaging, and boot/recovery utility.\n" +
                    "Zero external dependencies.",
                    "About WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });

            menu.Items.Add(fileMenu);
            menu.Items.Add(actionsMenu);
            menu.Items.Add(helpMenu);
            this.MainMenuStrip = menu;

            ToolStrip toolbar = new ToolStrip();
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Items.Add(new ToolStripButton("Refresh (F5)", null, delegate { RefreshDisks(); }));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(new ToolStripButton("Clone Disk...", null, OpenCloneDialog));
            toolbar.Items.Add(new ToolStripButton("Backup to VHDX...", null, OpenBackupDialog));
            toolbar.Items.Add(new ToolStripButton("Restore VHDX...", null, OpenRestoreDialog));
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(new ToolStripButton("Relocate Recovery", null, RunRelocateSelected));
            toolbar.Items.Add(new ToolStripButton("Repair UEFI Boot", null, RunBootRepairSelected));

            this.Controls.Add(toolbar);
            this.Controls.Add(menu);
        }

        private DiskInfo GetSelectedDisk()
        {
            if (_diskList.SelectedItems.Count == 0) return null;
            return _diskList.SelectedItems[0].Tag as DiskInfo;
        }

        private void OpenCloneDialog(object sender, EventArgs e)
        {
            if (_disks.Count < 2)
            {
                MessageBox.Show("At least 2 physical or virtual disks are required to clone.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (CloneDialog dlg = new CloneDialog(_disks))
            {
                dlg.ShowDialog(this);
                if (dlg.CloneCompleted) RefreshDisks();
            }
        }

        private void OpenBackupDialog(object sender, EventArgs e)
        {
            if (_disks.Count == 0) return;
            using (BackupVhdxDialog dlg = new BackupVhdxDialog(_disks))
            {
                dlg.ShowDialog(this);
            }
            RefreshDisks();
        }

        private void OpenRestoreDialog(object sender, EventArgs e)
        {
            if (_disks.Count == 0) return;
            using (RestoreVhdxDialog dlg = new RestoreVhdxDialog(_disks))
            {
                dlg.ShowDialog(this);
                if (dlg.RestoreCompleted) RefreshDisks();
            }
        }

        private void RunRelocateSelected(object sender, EventArgs e)
        {
            DiskInfo d = GetSelectedDisk();
            if (d == null) { MessageBox.Show("Select a disk in the top list first.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            if (MessageBox.Show(
                string.Format("Relocate the Recovery partition to the end of Disk {0} ({1}) and extend the main OS partition?{2}",
                d.Number, d.Model, d.IsSystemDisk ? "\n\nNote: This is your active System Disk. WinImg will use native WinRE (reagentc)." : ""),
                "Confirm Recovery Relocation & OS Expand", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            using (TaskLogDialog dlg = new TaskLogDialog("WinImg — Relocate Recovery & Extend OS (Disk " + d.Number + ")",
                delegate(Action<string> log) { StorageOperations.RelocateRecoveryAndExtend(d.Number, log); }))
            {
                dlg.ShowDialog(this);
            }
            RefreshDisks();
        }

        private void RunBootRepairSelected(object sender, EventArgs e)
        {
            DiskInfo d = GetSelectedDisk();
            if (d == null) { MessageBox.Show("Select a cloned GPT disk in the top list first.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (d.IsSystemDisk) { MessageBox.Show("Cannot overwrite BCD on your currently running System Disk.", "WinImg", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            if (MessageBox.Show(string.Format("Rebuild the UEFI Bootloader (bcdboot) on Disk {0} ({1})?", d.Number, d.Model),
                "Confirm UEFI Boot Repair", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            using (TaskLogDialog dlg = new TaskLogDialog("WinImg — Repair UEFI Bootloader (Disk " + d.Number + ")",
                delegate(Action<string> log) { StorageOperations.RepairUefiBoot(d.Number, log); }))
            {
                dlg.ShowDialog(this);
            }
            RefreshDisks();
        }

        private void BuildMainLayout()
        {
            SplitContainer outerSplit = new SplitContainer();
            outerSplit.Dock = DockStyle.Fill;
            outerSplit.Orientation = Orientation.Horizontal;
            outerSplit.SplitterDistance = 240;

            _diskList = new ListView();
            _diskList.Dock = DockStyle.Fill;
            _diskList.View = View.Details;
            _diskList.FullRowSelect = true;
            _diskList.GridLines = true;
            _diskList.HideSelection = false;
            _diskList.Columns.Add("Disk #", 65);
            _diskList.Columns.Add("Model", 270);
            _diskList.Columns.Add("Bus / Type", 95);
            _diskList.Columns.Add("Style", 115);
            _diskList.Columns.Add("Size (GB)", 100, HorizontalAlignment.Right);
            _diskList.Columns.Add("Status", 220);
            _diskList.SelectedIndexChanged += DiskList_SelectedIndexChanged;
            BuildDiskContextMenu();
            _diskList.ContextMenuStrip = _diskContextMenu;
            _diskList.MouseDown += delegate(object s, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Right)
                {
                    ListViewHitTestInfo hit = _diskList.HitTest(me.Location);
                    if (hit.Item != null) hit.Item.Selected = true;
                }
            };
            outerSplit.Panel1.Controls.Add(_diskList);

            SplitContainer bottomSplit = new SplitContainer();
            bottomSplit.Dock = DockStyle.Fill;
            bottomSplit.Orientation = Orientation.Horizontal;
            bottomSplit.SplitterDistance = 120;

            _mapPanel = new PartitionMapPanel();
            _mapPanel.Dock = DockStyle.Fill;
            bottomSplit.Panel1.Controls.Add(_mapPanel);

            _partList = new ListView();
            _partList.Dock = DockStyle.Fill;
            _partList.View = View.Details;
            _partList.FullRowSelect = true;
            _partList.GridLines = true;
            _partList.Columns.Add("Part #", 55);
            _partList.Columns.Add("Letter", 55);
            _partList.Columns.Add("Label", 140);
            _partList.Columns.Add("File System", 85);
            _partList.Columns.Add("Type", 180);
            _partList.Columns.Add("Size", 100, HorizontalAlignment.Right);
            _partList.Columns.Add("Offset (MB)", 110, HorizontalAlignment.Right);
            bottomSplit.Panel2.Controls.Add(_partList);

            outerSplit.Panel2.Controls.Add(bottomSplit);
            this.Controls.Add(outerSplit);
            outerSplit.BringToFront();
        }

        private void BuildDiskContextMenu()
        {
            _diskContextMenu = new ContextMenuStrip();
            ToolStripMenuItem miClone = new ToolStripMenuItem("Clone This Disk...", null, delegate
            {
                DiskInfo d = GetSelectedDisk();
                if (d == null || _disks.Count < 2) { OpenCloneDialog(null, EventArgs.Empty); return; }
                using (CloneDialog dlg = new CloneDialog(_disks, d))
                {
                    dlg.ShowDialog(this);
                    if (dlg.CloneCompleted) RefreshDisks();
                }
            });
            ToolStripMenuItem miBackup = new ToolStripMenuItem("Backup This Disk to VHDX...", null, delegate
            {
                DiskInfo d = GetSelectedDisk();
                if (d == null) return;
                using (BackupVhdxDialog dlg = new BackupVhdxDialog(_disks, d)) { dlg.ShowDialog(this); }
                RefreshDisks();
            });
            ToolStripMenuItem miRestore = new ToolStripMenuItem("Restore VHDX to This Disk...", null, delegate
            {
                DiskInfo d = GetSelectedDisk();
                if (d == null || d.IsSystemDisk) return;
                using (RestoreVhdxDialog dlg = new RestoreVhdxDialog(_disks, d))
                {
                    dlg.ShowDialog(this);
                    if (dlg.RestoreCompleted) RefreshDisks();
                }
            });
            ToolStripMenuItem miRelocate = new ToolStripMenuItem("Relocate Recovery && Extend OS", null, RunRelocateSelected);
            ToolStripMenuItem miBoot = new ToolStripMenuItem("Repair UEFI Bootloader (bcdboot)", null, RunBootRepairSelected);
            ToolStripMenuItem miRefresh = new ToolStripMenuItem("Refresh Disks (F5)", null, delegate { RefreshDisks(); });

            _diskContextMenu.Items.AddRange(new ToolStripItem[]
            {
                miClone, miBackup, miRestore,
                new ToolStripSeparator(),
                miRelocate, miBoot,
                new ToolStripSeparator(),
                miRefresh
            });

            _diskContextMenu.Opening += delegate(object s, CancelEventArgs ce)
            {
                DiskInfo d = GetSelectedDisk();
                bool hasDisk = (d != null);
                miClone.Enabled = hasDisk && _disks.Count >= 2;
                miBackup.Enabled = hasDisk;
                miRestore.Enabled = hasDisk && !d.IsSystemDisk;
                miRelocate.Enabled = hasDisk;
                miBoot.Enabled = hasDisk && !d.IsSystemDisk;
            };
        }

        private void BuildStatusBar()
        {
            _statusStrip = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("Ready");
            _statusStrip.Items.Add(_statusLabel);
            this.Controls.Add(_statusStrip);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { RefreshDisks(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void RefreshDisks()
        {
            _statusLabel.Text = "Scanning physical & virtual disks...";
            Application.DoEvents();

            int prevSelected = GetSelectedDisk() != null ? GetSelectedDisk().Number : -1;
            _disks = LoadAllDisksStatic();
            _diskList.BeginUpdate();
            _diskList.Items.Clear();

            ListViewItem toSelect = null;
            foreach (DiskInfo d in _disks)
            {
                ListViewItem item = new ListViewItem("Disk " + d.Number);
                item.SubItems.Add(d.Model);
                item.SubItems.Add(d.InterfaceType);
                item.SubItems.Add(d.PartitionStyle);
                item.SubItems.Add(string.Format("{0:F2}", d.Size / 1073741824.0));
                item.SubItems.Add(d.Status);
                item.Tag = d;

                if (d.IsSystemDisk) item.BackColor = Color.FromArgb(235, 245, 255);
                else if (d.InterfaceType == "Virtual") item.BackColor = Color.FromArgb(238, 252, 238);
                else if (d.IsDynamic) item.BackColor = Color.FromArgb(255, 250, 225);

                _diskList.Items.Add(item);
                if (d.Number == prevSelected) toSelect = item;
            }
            _diskList.EndUpdate();

            if (toSelect != null) toSelect.Selected = true;
            else if (_diskList.Items.Count > 0) _diskList.Items[0].Selected = true;
            else { _mapPanel.SetDisk(null); _partList.Items.Clear(); }

            _statusLabel.Text = string.Format("Detected {0} disk(s).  |  WinImg v1.0 Ready", _disks.Count);
        }

        private void DiskList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_diskList.SelectedItems.Count == 0) return;
            DiskInfo d = _diskList.SelectedItems[0].Tag as DiskInfo;
            if (d == null) return;

            _mapPanel.SetDisk(d);
            _partList.BeginUpdate();
            _partList.Items.Clear();

            foreach (PartitionInfo p in d.Partitions)
            {
                ListViewItem item = new ListViewItem(p.IsUnallocated ? "-" : p.PartitionNumber.ToString());
                item.SubItems.Add(string.IsNullOrEmpty(p.DriveLetter) ? "" : p.DriveLetter + ":");
                item.SubItems.Add(p.IsUnallocated ? "(Unallocated)" : (p.Label ?? ""));
                item.SubItems.Add(p.FileSystem ?? "");
                item.SubItems.Add(p.TypeName ?? "");
                item.SubItems.Add(p.Size >= 1073741824UL
                    ? string.Format("{0:F2} GB", p.Size / 1073741824.0)
                    : string.Format("{0:F0} MB", p.Size / 1048576.0));
                item.SubItems.Add(string.Format("{0:N0}", p.Offset / 1048576UL));

                if (p.IsUnallocated) item.ForeColor = Color.Gray;
                _partList.Items.Add(item);
            }
            _partList.EndUpdate();
        }

        private static string MapBusType(int bus, string fallback)
        {
            switch (bus)
            {
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 14: return "Virtual";
                case 15: return "Virtual";
                case 17: return "NVMe";
                default: return string.IsNullOrEmpty(fallback) ? "SCSI" : fallback;
            }
        }

        public static List<DiskInfo> LoadAllDisksStatic()
        {
            Dictionary<int, DiskInfo> map = new Dictionary<int, DiskInfo>();

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\cimv2", "SELECT Index, Model, InterfaceType, Size, Status FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        int idx = Convert.ToInt32(obj["Index"]);
                        ulong size = obj["Size"] != null ? Convert.ToUInt64(obj["Size"]) : 0UL;
                        map[idx] = new DiskInfo
                        {
                            Number = idx,
                            Model = obj["Model"] != null ? obj["Model"].ToString().Trim() : "Unknown Drive",
                            InterfaceType = obj["InterfaceType"] != null ? obj["InterfaceType"].ToString() : "",
                            Size = size,
                            PartitionStyle = "DYNAMIC / OFFLINE",
                            Status = obj["Status"] != null ? obj["Status"].ToString() : "Unknown"
                        };
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\Microsoft\\Windows\\Storage", "SELECT Number, PartitionStyle, IsOffline, IsSystem, BusType FROM MSFT_Disk"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        int num = Convert.ToInt32(obj["Number"]);
                        if (!map.ContainsKey(num)) continue;
                        DiskInfo d = map[num];

                        int style = obj["PartitionStyle"] != null ? Convert.ToInt32(obj["PartitionStyle"]) : 0;
                        d.PartitionStyle = style == 2 ? "GPT" : (style == 1 ? "MBR" : "RAW");

                        bool isOffline = obj["IsOffline"] != null && Convert.ToBoolean(obj["IsOffline"]);
                        bool isSys = obj["IsSystem"] != null && Convert.ToBoolean(obj["IsSystem"]);
                        d.IsSystemDisk = isSys;
                        d.Status = (isOffline ? "Offline" : "Online") + (isSys ? " (System Disk)" : "");

                        if (obj["BusType"] != null)
                            d.InterfaceType = MapBusType(Convert.ToInt32(obj["BusType"]), d.InterfaceType);
                    }
                }
            }
            catch { }

            Dictionary<string, string[]> volMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\cimv2", "SELECT DeviceID, VolumeName, FileSystem FROM Win32_LogicalDisk"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        string dev = obj["DeviceID"] != null ? obj["DeviceID"].ToString().TrimEnd(':') : "";
                        if (!string.IsNullOrEmpty(dev))
                        {
                            volMap[dev] = new string[]
                            {
                                obj["VolumeName"] != null ? obj["VolumeName"].ToString() : "",
                                obj["FileSystem"] != null ? obj["FileSystem"].ToString() : ""
                            };
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\Microsoft\\Windows\\Storage",
                    "SELECT DiskNumber, PartitionNumber, Offset, Size, DriveLetter, GptType, MbrType FROM MSFT_Partition"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        int diskNum = Convert.ToInt32(obj["DiskNumber"]);
                        if (!map.ContainsKey(diskNum)) continue;

                        string ltr = "";
                        if (obj["DriveLetter"] != null)
                        {
                            char c = Convert.ToChar(obj["DriveLetter"]);
                            if (c != '\0' && char.IsLetter(c)) ltr = c.ToString();
                        }

                        string gptType = obj["GptType"] != null ? obj["GptType"].ToString().ToLowerInvariant() : "";
                        string typeName = ClassifyPartition(gptType, obj["MbrType"]);
                        bool isDyn = gptType == "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}" || gptType == "{af9b60a0-1431-4f62-bc68-3311714a69ad}";
                        if (isDyn) map[diskNum].IsDynamic = true;

                        PartitionInfo p = new PartitionInfo
                        {
                            PartitionNumber = Convert.ToInt32(obj["PartitionNumber"]),
                            Offset = Convert.ToUInt64(obj["Offset"]),
                            Size = Convert.ToUInt64(obj["Size"]),
                            DriveLetter = ltr,
                            GptType = gptType,
                            TypeName = typeName,
                            IsDynamic = isDyn
                        };

                        if (!string.IsNullOrEmpty(ltr) && volMap.ContainsKey(ltr))
                        {
                            p.Label = volMap[ltr][0];
                            p.FileSystem = volMap[ltr][1];
                            if (ltr.Equals("C", StringComparison.OrdinalIgnoreCase))
                            {
                                map[diskNum].IsSystemDisk = true;
                                if (map[diskNum].Status.IndexOf("System", StringComparison.OrdinalIgnoreCase) < 0)
                                    map[diskNum].Status += " (System Disk)";
                            }
                        }

                        map[diskNum].Partitions.Add(p);
                    }
                }
            }
            catch { }

            foreach (DiskInfo d in map.Values)
            {
                if (d.IsDynamic && d.PartitionStyle.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) < 0)
                    d.PartitionStyle += " (Dynamic)";

                d.Partitions = d.Partitions.OrderBy(p => p.Offset).ToList();
                List<PartitionInfo> withGaps = new List<PartitionInfo>();
                ulong cursor = 0;

                foreach (PartitionInfo p in d.Partitions)
                {
                    if (p.Offset > cursor && (p.Offset - cursor) > 16777216UL)
                    {
                        withGaps.Add(new PartitionInfo { Offset = cursor, Size = p.Offset - cursor, TypeName = "Unallocated", IsUnallocated = true });
                    }
                    withGaps.Add(p);
                    ulong end = p.Offset + p.Size;
                    if (end > cursor) cursor = end;
                }

                if (d.Size > cursor && (d.Size - cursor) > 16777216UL && d.Partitions.Count > 0)
                {
                    withGaps.Add(new PartitionInfo { Offset = cursor, Size = d.Size - cursor, TypeName = "Unallocated", IsUnallocated = true });
                }

                d.Partitions = withGaps;
            }

            return map.Values.OrderBy(d => d.Number).ToList();
        }

        private static string ClassifyPartition(string gptType, object mbrTypeObj)
        {
            if (!string.IsNullOrEmpty(gptType))
            {
                if (gptType == "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}") return "EFI System";
                if (gptType == "{e3c9e316-0b5c-4db8-817d-f92df00215ae}") return "Microsoft Reserved";
                if (gptType == "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}") return "Basic Data";
                if (gptType == "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}") return "Recovery";
                if (gptType == "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}") return "LDM Metadata";
                if (gptType == "{af9b60a0-1431-4f62-bc68-3311714a69ad}") return "LDM Data";
                return "GPT Partition";
            }
            if (mbrTypeObj != null)
            {
                int mbr = Convert.ToInt32(mbrTypeObj);
                if (mbr == 7) return "IFS / NTFS";
                if (mbr == 39) return "Recovery";
                if (mbr == 11 || mbr == 12) return "FAT32";
            }
            return "Primary";
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length >= 3 && args[0].Equals("/signwinx", StringComparison.OrdinalIgnoreCase))
            {
                bool ok = WinXSigner.SignShortcut(args[1], args[2], args.Length >= 4 ? args[3] : "");
                return ok ? 0 : 1;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
