using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SsmsSqlPlugin.Installer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = args != null && args.Any(a =>
                string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase));

            if (args != null && args.Any(a =>
                    string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                bool confirmed = silent || MessageBox.Show(
                        "卸载 SSMS-SQLPlugin？\r\n\r\n扩展文件会被移除，历史记录和设置会保留。",
                        "卸载 SSMS-SQLPlugin",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;

                if (confirmed)
                {
                    string message;
                    bool ok = InstallerCore.Uninstall(out message);
                    Environment.ExitCode = ok ? 0 : 1;
                    if (!silent)
                    {
                        MessageBox.Show(message, "SSMS-SQLPlugin",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                return;
            }

            if (args != null && args.Any(a =>
                    string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase)))
            {
                string message;
                bool ok = InstallerCore.Install(out message);
                Environment.ExitCode = ok ? 0 : 1;
                if (!silent)
                {
                    MessageBox.Show(message, "SSMS-SQLPlugin",
                        MessageBoxButtons.OK,
                        ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
                return;
            }

            Application.Run(new InstallerForm());
        }
    }

    internal static class InstallerCore
    {
        public const string ProductName = "SSMS-SQLPlugin";
        private const string ExtensionId = "SSMS-SQLPlugin.7b3e2f18-5c94-4a67-b1d8-3f9e6a4c2d10";
        private const string PkgdefName = "SSMS-SQLPlugin.pkgdef";
        private const int VsixInstallerTimeoutMs = 90000;

        public static string DataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SsmsSqlPlugin");
            }
        }

        public static string FindExtensionRoot()
        {
            string ssmsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "SSMS");

            if (!Directory.Exists(ssmsRoot)) return null;

            foreach (string instance in Directory.GetDirectories(ssmsRoot, "22.0_*")
                                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string extensions = Path.Combine(instance, "Extensions");
                if (Directory.Exists(extensions)) return extensions;
            }
            return null;
        }

        public static string FindInstalledDirectory()
        {
            string root = FindExtensionRoot();
            if (string.IsNullOrEmpty(root)) return null;

            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, PkgdefName))) return dir;
                }
            }
            catch { }
            return null;
        }

        public static string FindVsixInstaller()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            foreach (string root in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                try
                {
                    foreach (string dir in Directory.GetDirectories(
                                 root, "Microsoft SQL Server Management Studio*"))
                    {
                        string[] candidates =
                        {
                            Path.Combine(dir, "Release", "Common7", "IDE", "VSIXInstaller.exe"),
                            Path.Combine(dir, "Common7", "IDE", "VSIXInstaller.exe"),
                        };

                        foreach (string candidate in candidates)
                        {
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        public static string GetPayloadVersion()
        {
            string temp = null;
            try
            {
                temp = ExtractPayload();
                string dll = Path.Combine(temp, "SsmsSqlPlugin.dll");
                if (!File.Exists(dll)) return "0.1.1";

                return AssemblyName.GetAssemblyName(dll).Version.ToString(3);
            }
            catch
            {
                return "0.1.1";
            }
            finally
            {
                SafeDeleteDirectory(temp);
            }
        }

        public static bool Install(out string message)
        {
            message = null;

            if (IsSsmsRunning())
            {
                message = "SSMS 正在运行。请先关闭所有 SSMS 窗口，再点「安装 / 更新」。";
                return false;
            }

            string vsixInstaller = FindVsixInstaller();
            if (string.IsNullOrEmpty(vsixInstaller))
            {
                message = "未找到 SSMS 22 的 VSIXInstaller.exe。\r\n" +
                          "请确认已安装 SSMS 22，或重新安装 SSMS 后重试。";
                return false;
            }

            // 已安装过则先卸载，避免 VSIXInstaller 因同版本拒绝更新
            if (FindInstalledDirectory() != null)
            {
                string uninstallMessage;
                if (!RunUninstall(vsixInstaller, out uninstallMessage))
                {
                    message = uninstallMessage;
                    return false;
                }
            }

            string temp = null;
            try
            {
                string vsix = ExtractPayloadVsix(out temp);
                int code = RunVsixInstaller(vsixInstaller, "/quiet \"" + vsix + "\"");
                if (code != 0)
                {
                    message = "安装失败：VSIXInstaller 返回 " + code + "。";
                    return false;
                }

                string installed = WaitForInstall();
                if (code != 0)
                {
                    // 有些安全软件会在 VSIXInstaller 改 SSMS.exe.config 时拦截，
                    // 进程可能超时，但扩展已经落盘；以目录状态为准再确认一次。
                    string partial = FindInstalledDirectory();
                    if (!string.IsNullOrEmpty(partial))
                    {
                        WriteUninstallEntry(partial);
                        CopyInstallerToDataDirectory();
                        message = "安装已完成，但 VSIXInstaller 没有正常退出。\r\n" +
                                  "如果安全软件有弹窗，请按提示处理。\r\n\r\n" +
                                  "扩展目录：\r\n" + partial + "\r\n\r\n请重启 SSMS 22 生效。";
                        return true;
                    }

                    message = code == -2
                        ? "安装超时：VSIXInstaller 可能被杀毒/安全软件拦截。\r\n" +
                          "请处理安全软件弹窗后重试。"
                        : "安装失败：VSIXInstaller 返回 " + code + "。";
                    return false;
                }

                if (string.IsNullOrEmpty(installed))
                {
                    message = "安装未完成：没有找到已注册的扩展目录。";
                    return false;
                }

                WriteUninstallEntry(installed);
                CopyInstallerToDataDirectory();

                message = "安装完成。\r\n\r\n扩展目录：\r\n" + installed +
                          "\r\n\r\n请重启 SSMS 22 生效。";
                return true;
            }
            catch (Exception ex)
            {
                message = "安装失败：" + ex.Message;
                return false;
            }
            finally
            {
                SafeDeleteDirectory(temp);
            }
        }

        public static bool Uninstall(out string message)
        {
            message = null;

            if (IsSsmsRunning())
            {
                message = "SSMS 正在运行。请先关闭所有 SSMS 窗口，再卸载。";
                return false;
            }

            string vsixInstaller = FindVsixInstaller();
            if (string.IsNullOrEmpty(vsixInstaller))
            {
                message = "未找到 SSMS 22 的 VSIXInstaller.exe。";
                return false;
            }

            if (FindInstalledDirectory() != null &&
                !RunUninstall(vsixInstaller, out message))
            {
                return false;
            }

            RemoveUninstallEntry();
            message = "卸载完成。\r\n\r\n历史记录和设置已保留：\r\n" + DataDirectory +
                      "\r\n\r\n如果 SSMS 正在运行过，请重启 SSMS。";
            return true;
        }

        public static bool IsSsmsRunning()
        {
            try { return Process.GetProcessesByName("SSMS").Length > 0; }
            catch { return false; }
        }

        private static bool RunUninstall(string vsixInstaller, out string message)
        {
            int code = RunVsixInstaller(vsixInstaller, "/quiet /uninstall:" + ExtensionId);
            if (code != 0)
            {
                // 进程可能被安全软件拦住，但扩展目录已经移除
                if (FindInstalledDirectory() == null)
                {
                    message = null;
                    return true;
                }

                message = code == -2
                    ? "卸载超时：VSIXInstaller 可能被杀毒/安全软件拦截。\r\n" +
                      "请处理安全软件弹窗后重试。"
                    : "卸载失败：VSIXInstaller 返回 " + code + "。";
                return false;
            }

            if (!WaitForUninstall())
            {
                message = "卸载未完成：扩展目录仍然存在。";
                return false;
            }

            message = null;
            return true;
        }

        private static string WaitForInstall()
        {
            for (int i = 0; i < 60; i++)
            {
                string dir = FindInstalledDirectory();
                if (!string.IsNullOrEmpty(dir)) return dir;
                System.Threading.Thread.Sleep(500);
            }
            return null;
        }

        private static bool WaitForUninstall()
        {
            for (int i = 0; i < 60; i++)
            {
                if (FindInstalledDirectory() == null) return true;
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        private static int RunVsixInstaller(string exe, string arguments)
        {
            try
            {
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null) return -1;
                    if (!process.WaitForExit(VsixInstallerTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return -2;
                    }
                    return process.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }

        private static string ExtractPayload()
        {
            string temp;
            string vsix = ExtractPayloadVsix(out temp);
            try
            {
                ZipFile.ExtractToDirectory(vsix, temp);
                return temp;
            }
            catch
            {
                SafeDeleteDirectory(temp);
                throw;
            }
        }

        private static string ExtractPayloadVsix(out string tempDir)
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "SsmsSqlPluginSetup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            using (Stream stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("Payload.zip"))
            {
                if (stream == null)
                    throw new InvalidOperationException("安装包缺少内嵌的 Payload.zip。");

                string vsix = Path.Combine(tempDir, "SSMS-SQLPlugin.vsix");
                using (FileStream file = File.Create(vsix)) stream.CopyTo(file);
                return vsix;
            }
        }

        private static void WriteUninstallEntry(string installDirectory)
        {
            string uninstaller = Path.Combine(DataDirectory, "Installer", "SSMS-SQLPlugin-Setup.exe");

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SSMS-SQLPlugin"))
            {
                if (key == null) return;

                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", GetPayloadVersion());
                key.SetValue("Publisher", "dreamstation625");
                key.SetValue("InstallLocation", installDirectory ?? string.Empty);
                key.SetValue("DisplayIcon", uninstaller);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SSMS-SQLPlugin",
                    false);
            }
            catch { }
        }

        private static void CopyInstallerToDataDirectory()
        {
            try
            {
                string dir = Path.Combine(DataDirectory, "Installer");
                Directory.CreateDirectory(dir);
                File.Copy(Application.ExecutablePath,
                    Path.Combine(dir, "SSMS-SQLPlugin-Setup.exe"), true);
            }
            catch { }
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly Label _pathValue;
        private readonly Label _versionValue;
        private readonly Label _status;
        private readonly Button _install;
        private readonly Button _uninstall;
        private readonly Button _openData;
        private readonly Button _close;
        private readonly Panel _busyPanel;
        private readonly Label _busyLabel;
        private bool _busy;

        public InstallerForm()
        {
            Text = "SSMS-SQLPlugin 安装器";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(700, 340);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(247, 249, 252);
            Padding = new Padding(22);

            var title = new Label
            {
                Text = "SSMS-SQLPlugin",
                Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 36, 48),
                AutoSize = true,
                Location = new Point(22, 18),
            };

            var subtitle = new Label
            {
                Text = "安装到 SSMS 22 用户扩展目录，卸载时保留历史记录和设置。",
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize = true,
                Location = new Point(24, 54),
            };

            var info = new GroupBox
            {
                Text = "安装信息",
                Location = new Point(22, 88),
                Size = new Size(656, 128),
                ForeColor = Color.FromArgb(55, 65, 81),
            };

            info.Controls.Add(MakeLabel("版本", 14, 26));
            _versionValue = MakeValue("", 94, 26);
            info.Controls.Add(_versionValue);

            info.Controls.Add(MakeLabel("扩展目录", 14, 58));
            _pathValue = MakeValue("", 94, 58);
            info.Controls.Add(_pathValue);

            info.Controls.Add(MakeLabel("用户数据", 14, 90));
            info.Controls.Add(MakeValue(InstallerCore.DataDirectory, 94, 90));

            _status = new Label
            {
                Text = "正在检查 SSMS 22…",
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize = false,
                Location = new Point(24, 228),
                Size = new Size(420, 44),
            };

            _install = new Button
            {
                Text = "安装 / 更新",
                Size = new Size(105, 32),
                Location = new Point(280, 280),
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _install.FlatAppearance.BorderSize = 0;
            _install.Click += (s, e) => DoInstall();

            _uninstall = new Button
            {
                Text = "卸载",
                Size = new Size(82, 32),
                Location = new Point(393, 280),
                FlatStyle = FlatStyle.Flat,
            };
            _uninstall.Click += (s, e) => DoUninstall();

            _openData = new Button
            {
                Text = "打开数据目录",
                Size = new Size(105, 32),
                Location = new Point(483, 280),
                FlatStyle = FlatStyle.Flat,
            };
            _openData.Click += (s, e) => OpenDataDirectory();

            _close = new Button
            {
                Text = "关闭",
                Size = new Size(72, 32),
                Location = new Point(596, 280),
                FlatStyle = FlatStyle.Flat,
            };
            _close.Click += (s, e) => Close();

            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(info);
            Controls.Add(_status);
            Controls.Add(_install);
            Controls.Add(_uninstall);
            Controls.Add(_openData);
            Controls.Add(_close);

            _busyPanel = new Panel
            {
                Size = new Size(380, 96),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
            };
            _busyLabel = new Label
            {
                Text = "",
                Location = new Point(14, 10),
                Size = new Size(352, 44),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 36, 48),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var busyBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Location = new Point(14, 62),
                Size = new Size(352, 16),
            };
            _busyPanel.Controls.Add(_busyLabel);
            _busyPanel.Controls.Add(busyBar);
            Controls.Add(_busyPanel);

            FormClosing += (s, e) =>
            {
                if (!_busy) return;
                e.Cancel = true;
                MessageBox.Show("正在调用 SSMS VSIXInstaller，请等待操作完成。",
                    "SSMS-SQLPlugin", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            Shown += (s, e) => RefreshState();
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(107, 114, 128),
            };
        }

        private static Label MakeValue(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = false,
                Size = new Size(540, 20),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(31, 36, 48),
            };
        }

        private void RefreshState()
        {
            string vsixInstaller = InstallerCore.FindVsixInstaller();
            string installed = InstallerCore.FindInstalledDirectory();

            _versionValue.Text = "v" + InstallerCore.GetPayloadVersion();
            _pathValue.Text = !string.IsNullOrEmpty(installed)
                ? installed
                : (!string.IsNullOrEmpty(vsixInstaller)
                    ? "未安装（由 SSMS VSIXInstaller 注册）"
                    : "未找到 SSMS 22");

            bool ready = !string.IsNullOrEmpty(vsixInstaller);
            _install.Enabled = ready;
            _uninstall.Enabled = ready && !string.IsNullOrEmpty(installed);

            _status.Text = ready
                ? "安装或卸载前请先关闭所有 SSMS 窗口。"
                : "未找到 SSMS 22 的 VSIXInstaller.exe，请先安装或修复 SSMS 22。";
            _status.ForeColor = ready
                ? Color.FromArgb(107, 114, 128)
                : Color.FromArgb(185, 28, 28);
        }

        private void DoInstall()
        {
            if (_busy) return;

            SetBusy(true, "正在安装 / 更新…\r\n正在调用 VSIXInstaller；如有安全软件弹窗，请先处理。");
            _status.Text = "正在调用 VSIXInstaller…";

            Task.Run(() =>
            {
                string message;
                bool ok = InstallerCore.Install(out message);
                try
                {
                    BeginInvoke((Action)(() => FinishOperation(
                        ok, message, ok ? "安装完成，请重启 SSMS。" : "安装失败")));
                }
                catch { }
            });
        }

        private void DoUninstall()
        {
            if (_busy) return;

            if (MessageBox.Show(
                    "卸载 SSMS-SQLPlugin？\r\n\r\n扩展文件会被移除，历史记录和设置会保留。",
                    "卸载 SSMS-SQLPlugin",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            SetBusy(true, "正在卸载…\r\n正在调用 VSIXInstaller；如有安全软件弹窗，请先处理。");
            _status.Text = "正在调用 VSIXInstaller…";

            Task.Run(() =>
            {
                string message;
                bool ok = InstallerCore.Uninstall(out message);
                try
                {
                    BeginInvoke((Action)(() => FinishOperation(
                        ok, message, ok ? "已卸载。" : "卸载失败")));
                }
                catch { }
            });
        }

        private void FinishOperation(bool ok, string message, string successText)
        {
            SetBusy(false, string.Empty);
            _status.Text = ok ? successText : message;
            _status.ForeColor = ok
                ? Color.FromArgb(22, 163, 74)
                : Color.FromArgb(185, 28, 28);

            MessageBox.Show(message, "SSMS-SQLPlugin",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshState();
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            _busyLabel.Text = message ?? string.Empty;
            _busyPanel.Visible = busy;

            if (busy)
            {
                _install.Enabled = false;
                _uninstall.Enabled = false;
                _openData.Enabled = false;
                _close.Enabled = false;
                _busyPanel.Left = Math.Max(0, (ClientSize.Width - _busyPanel.Width) / 2);
                _busyPanel.Top = Math.Max(0, (ClientSize.Height - _busyPanel.Height) / 2);
                _busyPanel.BringToFront();
                UseWaitCursor = true;
            }
            else
            {
                _openData.Enabled = true;
                _close.Enabled = true;
                UseWaitCursor = false;
            }
        }

        private void OpenDataDirectory()
        {
            try
            {
                Directory.CreateDirectory(InstallerCore.DataDirectory);
                Process.Start("explorer.exe", "\"" + InstallerCore.DataDirectory + "\"");
            }
            catch { }
        }
    }
}
