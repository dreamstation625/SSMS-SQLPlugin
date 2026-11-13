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
            ClientSize = new Size(700, 380);
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
                Text = "安装到已检测到的 SSMS 20 / 21 / 22 扩展目录，卸载时保留历史记录和设置。SSMS 20 使用文件部署。",
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize = true,
                Location = new Point(24, 54),
            };

            var info = new GroupBox
            {
                Text = "安装信息",
                Location = new Point(22, 88),
                Size = new Size(656, 156),
                ForeColor = Color.FromArgb(55, 65, 81),
            };

            info.Controls.Add(MakeLabel("版本", 14, 26));
            _versionValue = MakeValue("", 94, 26);
            info.Controls.Add(_versionValue);

            info.Controls.Add(MakeLabel("安装状态", 14, 58));
            _pathValue = MakeValue("", 94, 58);
            _pathValue.Size = new Size(540, 42);
            _pathValue.AutoEllipsis = false;
            info.Controls.Add(_pathValue);

            info.Controls.Add(MakeLabel("用户数据", 14, 112));
            info.Controls.Add(MakeValue(InstallerCore.DataDirectory, 94, 112));

            _status = new Label
            {
                Text = "正在检查 SSMS 20 / 21 / 22…",
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize = false,
                Location = new Point(24, 256),
                Size = new Size(420, 44),
            };

            _install = new Button
            {
                Text = "安装 / 更新",
                Size = new Size(105, 32),
                Location = new Point(280, 308),
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
                Location = new Point(393, 308),
                FlatStyle = FlatStyle.Flat,
            };
            _uninstall.Click += (s, e) => DoUninstall();

            _openData = new Button
            {
                Text = "打开数据目录",
                Size = new Size(105, 32),
                Location = new Point(483, 308),
                FlatStyle = FlatStyle.Flat,
            };
            _openData.Click += (s, e) => OpenDataDirectory();

            _close = new Button
            {
                Text = "关闭",
                Size = new Size(72, 32),
                Location = new Point(596, 308),
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
                MessageBox.Show("正在安装扩展，请等待操作完成。",
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
            var products = InstallerCore.FindProducts();
            var installed = InstallerCore.FindInstalledExtensions();

            _versionValue.Text = "v" + InstallerCore.GetPayloadVersion();

            if (products.Count == 0)
            {
                _pathValue.Text = "未找到 SSMS 20 / 21 / 22";
                _install.Enabled = false;
                _uninstall.Enabled = false;
                _status.Text = "未找到已安装的 SSMS 20、21 或 22，请先安装或修复 SSMS。";
                _status.ForeColor = Color.FromArgb(185, 28, 28);
                return;
            }

            var lines = new System.Collections.Generic.List<string>();
            foreach (SsmsProduct product in products)
            {
                InstalledExtension extension = installed.FirstOrDefault(
                    x => x.Product.Version == product.Version);
                lines.Add(product.DisplayName + "：" +
                          (extension != null
                              ? "已安装（" + Path.GetFileName(extension.Directory) + "）"
                              : "未安装"));
            }
            _pathValue.Text = string.Join("\r\n", lines);

            _install.Enabled = true;
            _uninstall.Enabled = installed.Count > 0;
            _status.Text = installed.Count > 0
                ? "已安装到 " + installed.Count + " 个 SSMS 版本；安装或卸载前请先关闭所有 SSMS 窗口。"
                : "检测到 " + products.Count + " 个 SSMS 版本；安装前请先关闭所有 SSMS 窗口。";
            _status.ForeColor = Color.FromArgb(107, 114, 128);
        }

        private void DoInstall()
        {
            if (_busy) return;

            SetBusy(true, "正在安装 / 更新…\r\n正在部署扩展；如有安全软件弹窗，请先处理。");
            _status.Text = "正在部署扩展…";

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

            SetBusy(true, "正在卸载…\r\n正在移除扩展；如有安全软件弹窗，请先处理。");
            _status.Text = "正在卸载扩展…";

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
