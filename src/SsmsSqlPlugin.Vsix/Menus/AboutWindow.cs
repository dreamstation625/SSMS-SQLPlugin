using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.History;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Menus
{
    /// <summary>
    /// 「关于」窗口：版本、主要功能、仓库地址和本地路径。
    /// </summary>
    internal sealed class AboutWindow : Form
    {
        private const string RepoUrl = "https://github.com/dreamstation625/SSMS-SQLPlugin";

        private AboutWindow()
        {
            Text = "关于 SSMS-SQLPlugin";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 460);
            BackColor = HistoryTheme.Background;
            ForeColor = HistoryTheme.TextPrimary;
            Font = HistoryTheme.UiFont;
            Padding = new Padding(20);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = HistoryTheme.Background,
                Padding = Padding.Empty,
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildFeatures(), 0, 1);
            root.Controls.Add(BuildFooterInfo(), 0, 2);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = HistoryTheme.Background,
                Margin = new Padding(0, 10, 0, 0),
            };

            var close = new ModernButton
            {
                Text = "关闭",
                Style = ModernButtonStyle.Primary,
                Width = 88,
                Height = 32,
                Margin = Padding.Empty,
            };
            close.Click += (s, e) => Close();
            footer.Controls.Add(close);
            root.Controls.Add(footer, 0, 3);

            Controls.Add(root);
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                BackColor = HistoryTheme.Background,
                Margin = new Padding(0, 0, 0, 12),
            };
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titleRow = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            titleRow.Controls.Add(new Label
            {
                Text = "SSMS-SQLPlugin",
                AutoSize = true,
                Font = HistoryTheme.TitleFont,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Background,
                Margin = new Padding(0, 0, 12, 0),
                Anchor = AnchorStyles.Left,
            }, 0, 0);

            string version = "0.1.0";
            try
            {
                version = typeof(AboutWindow).Assembly.GetName().Version.ToString(3);
            }
            catch { }

            titleRow.Controls.Add(new Label
            {
                Text = "v" + version,
                AutoSize = true,
                Font = HistoryTheme.UiFontMedium,
                ForeColor = HistoryTheme.Accent,
                BackColor = HistoryTheme.AccentSoft,
                Padding = new Padding(8, 4, 8, 4),
                Margin = Padding.Empty,
                Anchor = AnchorStyles.Left,
            }, 1, 0);

            header.Controls.Add(titleRow, 0, 0);
            header.Controls.Add(new Label
            {
                Text = "SSMS 的 SQL 格式化、补全和历史工具",
                AutoSize = true,
                Font = HistoryTheme.UiFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Background,
                Margin = new Padding(0, 2, 0, 0),
            }, 0, 1);

            return header;
        }

        private Control BuildFeatures()
        {
            var card = new RoundedPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(18, 14, 18, 14),
                Margin = new Padding(0, 0, 0, 10),
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 6,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddFeature(grid, 0, "SQL 格式化",
                "Ctrl+K, Ctrl+D 全文；Ctrl+K, Ctrl+F 选区。保留注释和空行。");
            AddFeature(grid, 1, "补全",
                "按当前库补全表、字段、类型；Tab 提交。");
            AddFeature(grid, 2, "SQL 历史",
                "记录执行和编辑。可搜索、预览、复制、打开；支持多选删除和回收站（30 天）。");
            AddFeature(grid, 3, "文件快照",
                "打开或保存 SQL 文件时记录版本和路径。");
            AddFeature(grid, 4, "设置",
                "缩进、SELECT 列表、子查询展开、自动大写、自动记录。");
            AddFeature(grid, 5, "入口",
                "顶部菜单、右键菜单、刷新补全缓存、打开日志。");

            card.Controls.Add(grid);
            return card;
        }

        private static void AddFeature(TableLayoutPanel grid, int row, string title, string description)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = HistoryTheme.UiFontMedium,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 5, 12, 5),
            }, 0, row);

            grid.Controls.Add(new Label
            {
                Text = description,
                AutoSize = true,
                MaximumSize = new Size(540, 0),
                Font = HistoryTheme.UiFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 5, 0, 5),
            }, 1, row);
        }

        private Control BuildFooterInfo()
        {
            var card = new RoundedPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(18, 12, 18, 12),
                Margin = Padding.Empty,
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddPathRow(grid, 0, "仓库");
            var repo = new LinkLabel
            {
                Text = RepoUrl,
                AutoSize = true,
                Font = HistoryTheme.UiFontMedium,
                LinkColor = HistoryTheme.Accent,
                ActiveLinkColor = HistoryTheme.AccentHover,
                VisitedLinkColor = HistoryTheme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 4, 0, 4),
            };
            repo.LinkClicked += (s, e) => OpenRepository();
            grid.Controls.Add(repo, 1, 0);

            AddPathRow(grid, 1, "配置文件", FormatOptionsStore.ConfigPath);
            AddPathRow(grid, 2, "日志文件", Log.LogFilePath);

            card.Controls.Add(grid);
            return card;
        }

        private static void AddPathRow(TableLayoutPanel grid, int row, string label, string value = null)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextMuted,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 5, 8, 5),
            }, 0, row);

            if (value != null)
            {
                grid.Controls.Add(new Label
                {
                    Text = value,
                    AutoSize = true,
                    MaximumSize = new Size(580, 0),
                    Font = HistoryTheme.SmallFont,
                    ForeColor = HistoryTheme.TextSecondary,
                    BackColor = HistoryTheme.Card,
                    Margin = new Padding(0, 5, 0, 5),
                }, 1, row);
            }
        }

        public static void OpenRepository()
        {
            try
            {
                Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("[关于] 打开仓库失败: " + ex.Message);
            }
        }

        public static void ShowFrom(EnvDTE.DTE dte)
        {
            var form = new AboutWindow();

            IntPtr owner = IntPtr.Zero;
            try { owner = dte.MainWindow.HWnd; } catch { }

            if (owner != IntPtr.Zero) form.ShowDialog(new HandleOwner(owner));
            else form.ShowDialog();
        }

        private sealed class HandleOwner : IWin32Window
        {
            public HandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
