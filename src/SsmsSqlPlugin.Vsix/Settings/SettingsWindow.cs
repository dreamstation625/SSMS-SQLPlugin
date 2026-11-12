using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.History;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Settings
{
    /// <summary>
    /// 统一设置窗口：格式化风格、自动记录、历史数据目录。
    /// 菜单不再放带勾选项，避免 CommandBars 的勾选留白。
    /// </summary>
    internal sealed class SettingsWindow : Form
    {
        private static readonly int[] IntervalValues = { 1, 5, 15, 30 };

        private readonly RadioButton _indent2;
        private readonly RadioButton _indent4;
        private readonly ToggleSwitch _uppercase;
        private readonly ToggleSwitch _vertical;
        private readonly ToggleSwitch _subquery;
        private readonly ToggleSwitch _autoCase;
        private readonly ComboBox _autoMode;
        private readonly ComboBox _autoInterval;
        private readonly Label _autoHint;

        private SettingsWindow()
        {
            Text = "SSMS-SQLPlugin 设置";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 720);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = HistoryTheme.Background;
            ForeColor = HistoryTheme.TextPrimary;
            Font = HistoryTheme.UiFont;
            Padding = new Padding(20);
            DoubleBuffered = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = HistoryTheme.Background,
                Padding = Padding.Empty,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 266));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 202));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ── 标题 ──
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

            var title = new Label
            {
                Text = "设置",
                AutoSize = true,
                Font = HistoryTheme.TitleFont,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            var subtitle = new Label
            {
                Text = "格式化风格、自动记录与历史数据",
                AutoSize = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(subtitle, 0, 1);
            root.Controls.Add(header, 0, 0);

            // ── 格式化 ──
            var formatCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(18, 14, 18, 14),
                Margin = new Padding(0, 0, 0, 12),
            };

            var format = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            format.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            format.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++)
                format.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 30 : 34));

            format.Controls.Add(CardTitle("格式化"), 0, 0);
            format.SetColumnSpan(format.GetControlFromPosition(0, 0), 2);

            format.Controls.Add(RowLabel("缩进"), 0, 1);
            var indentPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            _indent2 = MakeRadio("2 空格");
            _indent4 = MakeRadio("4 空格");
            indentPanel.Controls.Add(_indent2);
            indentPanel.Controls.Add(_indent4);
            format.Controls.Add(indentPanel, 1, 1);

            _uppercase = MakeToggle("关键字大写");
            _vertical = MakeToggle("SELECT 每列一行");
            _subquery = MakeToggle("展开子查询");
            _autoCase = MakeToggle("空格后自动大写关键字");
            format.Controls.Add(_uppercase, 0, 2);
            format.SetColumnSpan(_uppercase, 2);
            format.Controls.Add(_vertical, 0, 3);
            format.SetColumnSpan(_vertical, 2);
            format.Controls.Add(_subquery, 0, 4);
            format.SetColumnSpan(_subquery, 2);
            format.Controls.Add(_autoCase, 0, 5);
            format.SetColumnSpan(_autoCase, 2);

            formatCard.Controls.Add(format);
            root.Controls.Add(formatCard, 0, 1);

            // ── 自动记录 ──
            var autoCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(18, 14, 18, 14),
                Margin = new Padding(0, 0, 0, 12),
            };

            var auto = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            auto.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            auto.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            auto.Controls.Add(CardTitle("自动记录"), 0, 0);
            auto.SetColumnSpan(auto.GetControlFromPosition(0, 0), 2);

            auto.Controls.Add(RowLabel("模式"), 0, 1);
            _autoMode = MakeCombo(170);
            _autoMode.Items.AddRange(new object[] { "关闭", "修改后记录", "定时" });
            _autoMode.SelectedIndexChanged += (s, e) => UpdateAutoIntervalState();
            auto.Controls.Add(_autoMode, 1, 1);

            auto.Controls.Add(RowLabel("间隔"), 0, 2);
            _autoInterval = MakeCombo(170);
            _autoInterval.Items.AddRange(new object[] { "每 1 分钟", "每 5 分钟", "每 15 分钟", "每 30 分钟" });
            auto.Controls.Add(_autoInterval, 1, 2);

            _autoHint = new Label
            {
                Text = "不执行也会记录；内容有变化才落库。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextMuted,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            auto.Controls.Add(_autoHint, 0, 3);
            auto.SetColumnSpan(_autoHint, 2);

            autoCard.Controls.Add(auto);
            root.Controls.Add(autoCard, 0, 2);

            // ── 历史数据 ──
            var historyCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(18, 14, 18, 14),
                Margin = new Padding(0, 0, 0, 12),
            };

            var history = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };
            history.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            history.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            history.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            history.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            var historyTitle = CardTitle("历史数据");
            history.Controls.Add(historyTitle, 0, 0);
            history.SetColumnSpan(historyTitle, 2);

            var pathLabel = new Label
            {
                Text = HistoryRuntime.DatabasePath,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 0, 12, 0),
            };
            history.Controls.Add(pathLabel, 0, 1);

            var openDir = new ModernButton
            {
                Text = "打开目录",
                Style = ModernButtonStyle.Secondary,
                Width = 90,
                Height = 30,
                Margin = Padding.Empty,
            };
            openDir.Click += (s, e) => OpenDataDirectory();
            history.Controls.Add(openDir, 1, 1);

            historyCard.Controls.Add(history);
            root.Controls.Add(historyCard, 0, 3);

            // ── 底部按钮 ──
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = HistoryTheme.Background,
                Padding = new Padding(0, 8, 0, 0),
                Margin = Padding.Empty,
            };

            var save = new ModernButton
            {
                Text = "保存",
                Style = ModernButtonStyle.Primary,
                Width = 88,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            save.Click += (s, e) => SaveSettings();

            var cancel = new ModernButton
            {
                Text = "取消",
                Style = ModernButtonStyle.Ghost,
                Width = 80,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            root.Controls.Add(footer, 0, 4);

            Controls.Add(root);
            LoadSettings();
        }

        public static void ShowFrom(EnvDTE.DTE dte)
        {
            var form = new SettingsWindow();

            IntPtr owner = IntPtr.Zero;
            try { owner = dte.MainWindow.HWnd; } catch { }

            if (owner != IntPtr.Zero) form.ShowDialog(new HandleOwner(owner));
            else form.ShowDialog();
        }

        private static Label CardTitle(string text)
            => new Label
            {
                Text = text,
                AutoSize = true,
                Font = HistoryTheme.UiFontMedium,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };

        private static Label RowLabel(string text)
            => new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = HistoryTheme.UiFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };

        private static RadioButton MakeRadio(string text)
            => new RadioButton
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = HistoryTheme.UiFont,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 4, 18, 0),
            };

        private static ToggleSwitch MakeToggle(string text)
        {
            var toggle = new ToggleSwitch
            {
                Text = text,
                Height = 24,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(0, 5, 0, 0),
            };

            // 只让「开关 + 文字」区域响应点击，右侧空白不切换。
            toggle.Width = 46 + TextRenderer.MeasureText(text, HistoryTheme.UiFont).Width + 14;
            return toggle;
        }

        private static ComboBox MakeCombo(int width)
            => new ComboBox
            {
                Width = width,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = HistoryTheme.UiFont,
                BackColor = HistoryTheme.Card,
                ForeColor = HistoryTheme.TextPrimary,
                Margin = new Padding(0, 4, 0, 0),
            };

        private void LoadSettings()
        {
            var options = FormatOptionsStore.Current;
            _indent2.Checked = options.IndentSize == 2;
            _indent4.Checked = options.IndentSize != 2;
            _uppercase.Checked = options.UppercaseKeywords;
            _vertical.Checked = options.VerticalSelectList;
            _subquery.Checked = options.ExpandSubqueries;
            _autoCase.Checked = options.AutoUppercaseKeywordOnSpace;

            var auto = HistoryAutoSaveStore.Current;
            _autoMode.SelectedIndex = !auto.Enabled
                ? 0
                : auto.Mode == AutoSaveMode.OnEdit ? 1 : 2;

            int intervalIndex = Array.IndexOf(IntervalValues, auto.IntervalMinutes);
            _autoInterval.SelectedIndex = intervalIndex >= 0 ? intervalIndex : 1;

            UpdateAutoIntervalState();
        }

        private void UpdateAutoIntervalState()
        {
            bool timer = _autoMode.SelectedIndex == 2;
            _autoInterval.Enabled = timer;
            _autoHint.Text = timer
                ? "内容有变化才落库，间隔按上面的选择。"
                : _autoMode.SelectedIndex == 1
                    ? "内容有变化时自动记录，最多每分钟一条。"
                    : "自动记录已关闭。";
        }

        private void SaveSettings()
        {
            FormatOptionsStore.Update(options =>
            {
                options.IndentSize = _indent2.Checked ? 2 : 4;
                options.UppercaseKeywords = _uppercase.Checked;
                options.VerticalSelectList = _vertical.Checked;
                options.ExpandSubqueries = _subquery.Checked;
                options.AutoUppercaseKeywordOnSpace = _autoCase.Checked;
            });

            int mode = Math.Max(0, _autoMode.SelectedIndex);
            int interval = IntervalValues[Math.Max(0, _autoInterval.SelectedIndex)];

            HistoryAutoSaveStore.Update(auto =>
            {
                auto.Enabled = mode != 0;
                auto.Mode = mode == 0
                    ? AutoSaveMode.Off
                    : mode == 1 ? AutoSaveMode.OnEdit : AutoSaveMode.Interval;
                auto.IntervalMinutes = interval;
            });

            Log.Info("设置已保存: 缩进=" + (_indent2.Checked ? 2 : 4) +
                     " 大写=" + _uppercase.Checked +
                     " 每列一行=" + _vertical.Checked +
                     " 展开子查询=" + _subquery.Checked +
                     " 空格自动大写=" + _autoCase.Checked +
                     " 自动记录=" + HistoryAutoSaveStore.Describe(HistoryAutoSaveStore.Current));

            DialogResult = DialogResult.OK;
            Close();
        }

        private void OpenDataDirectory()
        {
            try
            {
                string dir = Path.GetDirectoryName(HistoryRuntime.DatabasePath);
                if (string.IsNullOrEmpty(dir)) return;

                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败：" + ex.Message, "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private sealed class HandleOwner : IWin32Window
        {
            public HandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
