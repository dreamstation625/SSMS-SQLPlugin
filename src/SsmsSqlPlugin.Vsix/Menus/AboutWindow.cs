using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.History;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Localization;

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
            Text = Loc.T("About SSMS-SQLPlugin");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            // 英文文案换行后比中文高，窗口留足高度，避免底部按钮被裁切
            ClientSize = new Size(760, 500);
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
                Text = Loc.T("Close"),
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
                Text = Loc.T("SQL formatting, completion and history for SSMS"),
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

            AddFeature(grid, 0, Loc.T("SQL Formatting"),
                Loc.T("Ctrl+K, Ctrl+D for the whole document; Ctrl+K, Ctrl+F for the selection. Comments and blank lines are preserved."));
            AddFeature(grid, 1, Loc.T("Completion"),
                Loc.T("Completes tables, columns and types for the current database; press Tab to commit."));
            AddFeature(grid, 2, Loc.T("SQL History"),
                Loc.T("Records executions and edits. Search, preview, copy and open; multi-select delete and a 30-day recycle bin."));
            AddFeature(grid, 3, Loc.T("File Snapshots"),
                Loc.T("Records the version and path when SQL files are opened or saved."));
            AddFeature(grid, 4, Loc.T("Settings"),
                Loc.T("Indent, SELECT list, subquery expansion, auto-uppercase and auto-save."));
            AddFeature(grid, 5, Loc.T("Entry Points"),
                Loc.T("Top menu, context menu, refresh completion cache and open log."));

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

            AddPathRow(grid, 0, Loc.T("Repository"));
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

            AddPathRow(grid, 1, Loc.T("Config file"), FormatOptionsStore.ConfigPath);
            AddPathRow(grid, 2, Loc.T("Log file"), Log.LogFilePath);

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
