using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using EnvDTE;
using SsmsSqlPlugin.Completion;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Storage;

namespace SsmsSqlPlugin.History
{
    /// <summary>
    /// SQL 执行历史窗口：搜索 / 服务器 / 库 / 时间筛选，双击在查询窗口重开。
    ///
    /// 列表用 VirtualMode —— 结果上限 2000 条，但 10 万条搜索由 SQLite 完成，
    /// WinForms 只按可视行取数据。
    /// </summary>
    internal sealed class HistoryWindow : Form
    {
        private static HistoryWindow _instance;

        private readonly DTE _dte;
        private readonly TextBox _search;
        private readonly ComboBox _server;
        private readonly ComboBox _database;
        private readonly ComboBox _range;
        private readonly ComboBox _kind;
        private readonly ModernButton _queryButton;
        private readonly ModernCheckBox _recycleBin;
        private readonly HistoryListView _list;
        private readonly Panel _emptyPanel;
        private readonly SplitContainer _split;
        private readonly Label _previewInfo;
        private readonly Label _previewMeta;
        private readonly Label _previewPath;
        private readonly ToolTip _previewToolTip;
        private readonly RichTextBox _previewText;
        private readonly ColumnHeader _summaryColumn;
        private readonly ColumnHeader _deletedColumn;
        private readonly ModernButton _deleteButton;
        private readonly Label _status;
        private readonly Label _selectionLabel;
        private readonly Label _toast;
        private readonly Timer _toastTimer;
        private readonly Timer _debounce;

        private List<ExecutionRecord> _rows = new List<ExecutionRecord>();
        private bool _loading;
        private bool _pendingReload;
        private bool _filtersLoaded;

        private HistoryWindow(DTE dte)
        {
            _dte = dte;

            Text = "SQL 历史";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1380, 740);
            MinimumSize = new Size(1080, 580);
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = HistoryTheme.Background;
            ForeColor = HistoryTheme.TextPrimary;
            Font = HistoryTheme.UiFont;
            Padding = new Padding(16);

            _debounce = new Timer { Interval = 350 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); ReloadAsync(); };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = HistoryTheme.Background,
                Padding = Padding.Empty,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            // ── 标题 ──
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var headerLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };

            var accent = new RoundedPanel
            {
                Width = 4,
                Height = 32,
                Radius = 2,
                FillColor = HistoryTheme.Accent,
                BorderColor = HistoryTheme.Accent,
                Margin = new Padding(0, 8, 10, 0),
            };

            var titleStack = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };

            var title = new Label
            {
                Text = "SQL 历史",
                AutoSize = true,
                Font = HistoryTheme.TitleFont,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };

            var subtitle = new Label
            {
                Text = "搜索、预览、重开与自动记录",
                AutoSize = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Background,
                Margin = new Padding(0, 2, 0, 0),
            };

            titleStack.Controls.Add(title, 0, 0);
            titleStack.Controls.Add(subtitle, 0, 1);
            headerLeft.Controls.Add(accent);
            headerLeft.Controls.Add(titleStack);
            header.Controls.Add(headerLeft, 0, 0);
            root.Controls.Add(header, 0, 0);

            // ── 筛选工具条 ──
            var toolbarCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(12, 13, 12, 13),
                Margin = new Padding(0, 0, 0, 12),
            };

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                BackColor = HistoryTheme.Card,
                Padding = Padding.Empty,
            };

            var searchCard = new RoundedPanel
            {
                Width = 300,
                Height = 32,
                Radius = 8,
                FillColor = Color.FromArgb(248, 250, 252),
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(10, 7, 10, 0),
                Margin = new Padding(0, 0, 16, 0),
            };

            _search = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = HistoryTheme.UiFont,
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = HistoryTheme.TextPrimary,
            };
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ReloadAsync();
            };
            searchCard.Controls.Add(_search);
            top.Controls.Add(searchCard);

            top.Controls.Add(MakeLabel("服务器"));
            _server = MakeCombo(140);
            _server.SelectedIndexChanged += async (s, e) => await ReloadDatabasesAsync();
            top.Controls.Add(_server);

            top.Controls.Add(MakeLabel("库"));
            _database = MakeCombo(130);
            top.Controls.Add(_database);

            top.Controls.Add(MakeLabel("时间"));
            _range = MakeCombo(105);
            _range.Items.AddRange(new object[] { "全部时间", "最近 1 天", "最近 7 天", "最近 30 天", "最近 90 天" });
            _range.SelectedIndex = 0;
            top.Controls.Add(_range);

            top.Controls.Add(MakeLabel("类型"));
            _kind = MakeCombo(100);
            _kind.Items.AddRange(new object[] { "全部类型", "执行", "自动记录" });
            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (s, e) => ReloadAsync();
            top.Controls.Add(_kind);

            _queryButton = new ModernButton
            {
                Text = "查询",
                Style = ModernButtonStyle.Primary,
                Width = 80,
                Height = 32,
                Margin = new Padding(4, 0, 12, 0),
            };
            _queryButton.Click += (s, e) => ReloadAsync();
            top.Controls.Add(_queryButton);

            _recycleBin = new ModernCheckBox
            {
                Text = "回收站",
                Width = 102,
                Height = 32,
                Margin = Padding.Empty,
            };
            _recycleBin.CheckedChanged += (s, e) => ApplyRecycleMode();
            top.Controls.Add(_recycleBin);

            toolbarCard.Controls.Add(top);
            root.Controls.Add(toolbarCard, 0, 1);

            // ── 列表 + 右侧预览 ──
            var contentCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 10,
                FillColor = HistoryTheme.Card,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(1),
                Margin = new Padding(0, 0, 0, 10),
            };

            _list = new HistoryListView
            {
                Dock = DockStyle.Fill,
                VirtualMode = true,
            };
            _list.Columns.Add("时间", 150, HorizontalAlignment.Left);
            _list.Columns.Add("服务器", 118, HorizontalAlignment.Left);
            _list.Columns.Add("库", 85, HorizontalAlignment.Left);
            _list.Columns.Add("文件名", 128, HorizontalAlignment.Left);
            _list.Columns.Add("耗时", 72, HorizontalAlignment.Right);
            _list.Columns.Add("行数", 56, HorizontalAlignment.Right);
            _list.Columns.Add("类型", 52, HorizontalAlignment.Left);
            _summaryColumn = _list.Columns.Add("摘要", 183, HorizontalAlignment.Left);
            _deletedColumn = _list.Columns.Add("删除时间", 0, HorizontalAlignment.Left);

            _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
            _list.ItemActivate += (s, e) => OpenSelected();
            _list.SelectedIndexChanged += (s, e) => { UpdatePreview(); UpdateSelectionStatus(); };
            _list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A)
                {
                    e.SuppressKeyPress = true;
                    SelectAllRows();
                }
                else if (e.Control && e.KeyCode == Keys.C)
                {
                    e.SuppressKeyPress = true;
                    CopySelected();
                }
                else if (e.KeyCode == Keys.Delete)
                {
                    e.SuppressKeyPress = true;
                    DeleteOrRestoreSelected();
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    OpenSelected();
                }
            };

            var preview = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(16, 14, 16, 14),
                BackColor = HistoryTheme.Card,
            };
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var infoPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                BackColor = HistoryTheme.Card,
            };
            infoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            infoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            infoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

            _previewInfo = new Label
            {
                Dock = DockStyle.Fill,
                Text = "SQL 预览",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = HistoryTheme.UiFontMedium,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };

            _previewMeta = new Label
            {
                Dock = DockStyle.Fill,
                Text = "选中一条记录查看完整 SQL 与连接信息",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };

            _previewPath = new Label
            {
                Dock = DockStyle.Fill,
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextMuted,
                BackColor = HistoryTheme.Card,
                Margin = Padding.Empty,
            };

            _previewToolTip = new ToolTip();
            infoPanel.Controls.Add(_previewInfo, 0, 0);
            infoPanel.Controls.Add(_previewMeta, 0, 1);
            infoPanel.Controls.Add(_previewPath, 0, 2);

            var editorCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 8,
                FillColor = HistoryTheme.CardSoft,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(10),
                Margin = new Padding(0, 6, 0, 0),
            };

            _previewText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = HistoryTheme.CardSoft,
                ForeColor = HistoryTheme.TextPrimary,
                Font = HistoryTheme.PreviewMonoFont,
            };

            editorCard.Controls.Add(_previewText);
            preview.Controls.Add(infoPanel, 0, 0);
            preview.Controls.Add(editorCard, 0, 1);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                SplitterWidth = 12,
                BackColor = HistoryTheme.Card,
            };
            _split.Panel1.BackColor = HistoryTheme.Card;
            _split.Panel2.BackColor = HistoryTheme.Card;
            _split.Panel1.Controls.Add(_list);

            _emptyPanel = new Panel
            {
                BackColor = HistoryTheme.Card,
                Visible = false,
            };
            _emptyPanel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "没有匹配的记录\r\n调整搜索或筛选后再试",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = HistoryTheme.UiFont,
                ForeColor = HistoryTheme.TextMuted,
                BackColor = HistoryTheme.Card,
            });
            _split.Panel1.Controls.Add(_emptyPanel);
            _split.Panel1.Resize += (s, e) => PositionEmptyPanel();

            _split.Panel2.Controls.Add(preview);
            _split.Paint += (s, e) =>
            {
                var rect = _split.SplitterRectangle;
                using (var pen = new Pen(HistoryTheme.Border))
                {
                    int x = rect.Left + rect.Width / 2;
                    e.Graphics.DrawLine(pen, x, rect.Top + 10, x, rect.Bottom - 10);
                }
            };

            contentCard.Controls.Add(_split);
            root.Controls.Add(contentCard, 0, 2);

            // ── 底栏 ──
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _status = new Label
            {
                Text = "历史存储准备中…",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };
            footer.Controls.Add(_status, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Dock = DockStyle.Fill,
                BackColor = HistoryTheme.Background,
                Padding = new Padding(0, 4, 0, 0),
                Margin = Padding.Empty,
            };

            var close = new ModernButton
            {
                Text = "关闭",
                Style = ModernButtonStyle.Ghost,
                Width = 68,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            close.Click += (s, e) => Close();

            var open = new ModernButton
            {
                Text = "打开 SQL",
                Style = ModernButtonStyle.Primary,
                Width = 96,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            open.Click += (s, e) => OpenSelected();

            var copy = new ModernButton
            {
                Text = "复制 SQL",
                Style = ModernButtonStyle.Secondary,
                Width = 94,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            copy.Click += (s, e) => CopySelected();

            _deleteButton = new ModernButton
            {
                Text = "删除",
                Style = ModernButtonStyle.Danger,
                Width = 76,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            _deleteButton.Click += (s, e) => DeleteOrRestoreSelected();

            var refresh = new ModernButton
            {
                Text = "刷新",
                Style = ModernButtonStyle.Ghost,
                Width = 68,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            refresh.Click += async (s, e) =>
            {
                _filtersLoaded = false;
                await ReloadFiltersAsync();
                ReloadAsync();
            };

            var openDir = new ModernButton
            {
                Text = "打开目录",
                Style = ModernButtonStyle.Ghost,
                Width = 96,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            openDir.Click += (s, e) => OpenDataDirectory();

            var selectAll = new ModernButton
            {
                Text = "全选",
                Style = ModernButtonStyle.Ghost,
                Width = 68,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            selectAll.Click += (s, e) => SelectAllRows();

            _selectionLabel = new Label
            {
                AutoSize = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.Accent,
                BackColor = HistoryTheme.Background,
                Margin = new Padding(8, 8, 0, 0),
                Text = "",
            };

            _previewToolTip.SetToolTip(open, "在新查询窗口打开选中 SQL（双击或回车）");
            _previewToolTip.SetToolTip(copy, "复制选中 SQL（Ctrl+C）");
            _previewToolTip.SetToolTip(_deleteButton,
                "删除选中记录（Ctrl/Shift 多选，Ctrl+A 全选，Delete 键删除）");
            _previewToolTip.SetToolTip(selectAll, "选中当前列表全部记录（Ctrl+A）");

            // RightToLeft 流：先加的在最右
            buttons.Controls.Add(close);
            buttons.Controls.Add(open);
            buttons.Controls.Add(copy);
            buttons.Controls.Add(_deleteButton);
            buttons.Controls.Add(refresh);
            buttons.Controls.Add(openDir);
            buttons.Controls.Add(selectAll);
            buttons.Controls.Add(_selectionLabel);
            footer.Controls.Add(buttons, 1, 0);

            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);

            _toast = new Label
            {
                AutoSize = true,
                Visible = false,
                Font = HistoryTheme.UiFontMedium,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(22, 163, 74),
                Padding = new Padding(16, 10, 16, 10),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            Controls.Add(_toast);
            _toast.BringToFront();

            _toastTimer = new Timer { Interval = 2400 };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                _toast.Visible = false;
            };
        }

        private static Label MakeLabel(string text)
            => new Label
            {
                Text = text,
                AutoSize = true,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Card,
                Margin = new Padding(6, 10, 6, 0),
            };

        private static ComboBox MakeCombo(int width)
            => new ComboBox
            {
                Width = width,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = HistoryTheme.UiFont,
                BackColor = HistoryTheme.Card,
                ForeColor = HistoryTheme.TextPrimary,
                Margin = new Padding(0, 4, 14, 0),
            };

        public static void ShowFrom(DTE dte)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                if (_instance.WindowState == FormWindowState.Minimized)
                    _instance.WindowState = FormWindowState.Normal;

                _instance.Activate();
                return;
            }

            var form = new HistoryWindow(dte);
            _instance = form;
            form.FormClosed += (s, e) => { if (ReferenceEquals(_instance, form)) _instance = null; };

            IntPtr owner = IntPtr.Zero;
            try { owner = dte.MainWindow.HWnd; } catch { }

            if (owner != IntPtr.Zero) form.Show(new HandleOwner(owner));
            else form.Show();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            try
            {
                _split.Panel1MinSize = 620;
                _split.Panel2MinSize = 340;
                _split.SplitterDistance = Math.Max(620, ClientSize.Width - 510);
            }
            catch { }

            CueBanner.Set(_search, "搜索 SQL 文本…");

            await ReloadFiltersAsync();
            ReloadAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Dispose();
            _toastTimer.Stop();
            _toastTimer.Dispose();
            _previewToolTip.Dispose();
            base.OnFormClosed(e);
        }

        // ── 数据加载 ──

        private async System.Threading.Tasks.Task ReloadFiltersAsync()
        {
            if (_filtersLoaded) return;

            var db = HistoryRuntime.Database;
            if (db == null || !db.IsAvailable)
            {
                _status.Text = "历史存储尚未就绪：" + (db?.Error ?? "正在打开数据库");
                return;
            }

            try
            {
                var servers = await db.GetDistinctServersAsync();

                string selectedServer = SelectedText(_server);
                _server.BeginUpdate();
                _server.Items.Clear();
                _server.Items.Add("(全部)");
                foreach (string value in servers) _server.Items.Add(value);
                _server.SelectedIndex = FindItem(_server, selectedServer);
                _server.EndUpdate();

                await ReloadDatabasesAsync();
                _filtersLoaded = true;
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 读取筛选项失败: " + ex.Message);
                _status.Text = "读取筛选项失败: " + ex.Message;
            }
        }

        private async System.Threading.Tasks.Task ReloadDatabasesAsync()
        {
            var db = HistoryRuntime.Database;
            if (db == null || !db.IsAvailable) return;

            try
            {
                string server = SelectedText(_server);
                var databases = await db.GetDistinctDatabasesAsync(server);

                string selected = SelectedText(_database);
                _database.BeginUpdate();
                _database.Items.Clear();
                _database.Items.Add("(全部)");
                foreach (string value in databases) _database.Items.Add(value);
                _database.SelectedIndex = FindItem(_database, selected);
                _database.EndUpdate();
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 读取库列表失败: " + ex.Message);
            }
        }

        private void ReloadAsync()
        {
            if (_loading)
            {
                _pendingReload = true;
                return;
            }

            var db = HistoryRuntime.Database;
            if (db == null || !db.IsAvailable)
            {
                _status.Text = "历史存储不可用：" + (db?.Error ?? "正在打开数据库");
                return;
            }

            var query = BuildQuery();
            var sw = Stopwatch.StartNew();

            _loading = true;
            _queryButton.Enabled = false;
            _status.Text = "查询中…";

            db.SearchExecutionsAsync(query).ContinueWith(t =>
            {
                if (IsDisposed) return;

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        sw.Stop();
                        _loading = false;
                        _queryButton.Enabled = true;

                        if (t.IsFaulted)
                        {
                            var error = t.Exception?.GetBaseException();
                            Log.Warn("[历史] 查询失败: " + error?.Message);
                            _status.Text = "查询失败：" + error?.Message;
                        }
                        else
                        {
                            _rows = t.Result ?? new List<ExecutionRecord>();
                            _list.VirtualListSize = _rows.Count;
                            _list.Invalidate();

                            _emptyPanel.Visible = _rows.Count == 0;
                            if (_emptyPanel.Visible)
                            {
                                PositionEmptyPanel();
                                _emptyPanel.BringToFront();
                            }

                            try
                            {
                                _list.SelectedIndices.Clear();
                                if (_rows.Count > 0) _list.SelectedIndices.Add(0);
                            }
                            catch { }

                            UpdatePreview();
                            UpdateSelectionStatus();

                            _status.Text = (_recycleBin.Checked ? "回收站 · " : "") +
                                           "共 " + _rows.Count + " 条" +
                                           (_rows.Count >= query.Limit ? "（上限 " + query.Limit + "）" : "") +
                                           " · " + sw.ElapsedMilliseconds + "ms";
                        }

                        if (_pendingReload)
                        {
                            _pendingReload = false;
                            ReloadAsync();
                        }
                    }));
                }
                catch (InvalidOperationException) { /* 窗口已关闭 */ }
            });
        }

        private ExecutionSearch BuildQuery()
        {
            var query = new ExecutionSearch
            {
                Text = _search.Text,
                ServerName = SelectedText(_server),
                DatabaseName = SelectedText(_database),
                Limit = 2000,
                Deleted = _recycleBin.Checked
                    ? ExecutionDeletedFilter.Deleted
                    : ExecutionDeletedFilter.Active,
                Kind = _kind.SelectedIndex == 1
                    ? ExecutionKindFilter.Execute
                    : _kind.SelectedIndex == 2
                        ? ExecutionKindFilter.Auto
                        : ExecutionKindFilter.All,
            };

            switch (_range.SelectedIndex)
            {
                case 1: query.FromUtc = DateTime.UtcNow.AddDays(-1); break;
                case 2: query.FromUtc = DateTime.UtcNow.AddDays(-7); break;
                case 3: query.FromUtc = DateTime.UtcNow.AddDays(-30); break;
                case 4: query.FromUtc = DateTime.UtcNow.AddDays(-90); break;
            }

            return query;
        }

        private static string SelectedText(ComboBox combo)
        {
            if (combo.SelectedIndex <= 0) return null;
            return combo.SelectedItem as string;
        }

        private static int FindItem(ComboBox combo, string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;

            for (int i = 1; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i] as string, value, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        // ── 列表 ──

        private void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count)
            {
                e.Item = new ListViewItem(new[] { "", "", "", "", "", "", "", "", "", "" });
                return;
            }

            var row = _rows[e.ItemIndex];
            bool isExecute = string.IsNullOrEmpty(row.Kind) || row.Kind == "execute";

            e.Item = new ListViewItem(new[]
            {
                FormatListTime(row.StartedUtc),
                row.ServerName ?? "",
                row.DatabaseName ?? "",
                FileName(row),
                isExecute ? (row.DurationMs > 0 ? row.DurationMs + " ms" : "—") : "—",
                isExecute && row.Succeeded >= 0 ? row.RowsAffected.ToString() : "—",
                FormatKind(row.Kind),
                Summarize(row.SqlText),
                FormatListTime(row.DeletedUtc),
            });
        }

        private void UpdatePreview()
        {
            if (_previewText == null) return;

            var row = GetSelected();
            if (row == null)
            {
                _previewInfo.Text = "SQL 预览";
                _previewMeta.Text = "选中一条记录查看完整 SQL 与连接信息";
                _previewPath.Text = "";
                _previewToolTip.SetToolTip(_previewPath, "");
                _previewText.Text = "";
                return;
            }

            string pathText = !string.IsNullOrEmpty(row.SourcePath)
                ? "文件：" + row.SourcePath
                : !string.IsNullOrEmpty(row.EditorMoniker)
                    ? "临时查询：" + row.EditorMoniker
                    : "";

            _previewPath.Text = pathText;
            _previewToolTip.SetToolTip(_previewPath, pathText);

            bool isExecute = string.IsNullOrEmpty(row.Kind) || row.Kind == "execute";
            string duration = isExecute
                ? (row.DurationMs > 0 ? row.DurationMs + " ms" : "耗时未知")
                : "—";
            string result = isExecute ? FormatResult(row.Succeeded) : "—";
            string deletedInfo = string.IsNullOrEmpty(row.DeletedUtc)
                ? ""
                : " · 删除于 " + FormatTime(row.DeletedUtc);

            _previewInfo.Text = "#" + row.Id +
                                "  ·  " + FormatKind(row.Kind) +
                                "  ·  " + FormatTime(row.StartedUtc);

            _previewMeta.Text = (row.ServerName ?? "?") + " / " + (row.DatabaseName ?? "?") +
                                "  ·  " + duration +
                                "  ·  " + result +
                                deletedInfo;

            string sql = row.SqlText;
            if (string.IsNullOrEmpty(sql))
            {
                _previewText.Text = "（这条记录没有保存 SQL 文本）";
                return;
            }

            const int maxPreview = 100000;
            if (sql.Length > maxPreview)
            {
                sql = sql.Substring(0, maxPreview) +
                      "\r\n-- …（超过 10 万字符，预览已截断；完整内容请用「复制 SQL」或双击打开）";
            }

            _previewText.Text = sql;
            ApplyPreviewFormatting();
        }

        private void ApplyPreviewFormatting()
        {
            try
            {
                _previewText.SelectAll();
                // 左右留白 + 悬挂缩进：自动换行后的续行会明显缩进，
                // 不会把整段 SQL 挤成一坨。
                _previewText.SelectionIndent = 8;
                _previewText.SelectionRightIndent = 8;
                _previewText.SelectionHangingIndent = 18;
                _previewText.SelectionStart = 0;
                _previewText.SelectionLength = 0;
                _previewText.ScrollToCaret();
            }
            catch { }
        }

        private static string FormatTime(string utc)
        {
            if (DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime value))
            {
                return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
            return utc ?? "";
        }

        private static string FormatListTime(string utc)
        {
            if (DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime value))
            {
                return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            return utc ?? "";
        }

        private static string FormatResult(int succeeded)
        {
            if (succeeded == 1) return "成功";
            if (succeeded == 0) return "失败";
            return "未知";
        }

        private static string FormatKind(string kind)
        {
            if (kind == "timer") return "定时";
            if (kind == "edit") return "修改";
            return "执行";
        }

        private static string FileName(ExecutionRecord row)
        {
            string path = row.SourcePath;
            if (string.IsNullOrEmpty(path)) path = row.EditorMoniker;
            if (string.IsNullOrEmpty(path) ||
                path.StartsWith("view:", StringComparison.OrdinalIgnoreCase))
                return "";

            try { return Path.GetFileName(path); }
            catch { return ""; }
        }

        private static string Summarize(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";

            var sb = new StringBuilder(sql.Length > 400 ? 400 : sql.Length);
            bool lastSpace = false;
            foreach (char c in sql)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace) sb.Append(' ');
                    lastSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastSpace = false;
                }

                if (sb.Length >= 120) break;
            }

            return sb.ToString().Trim();
        }

        private void PositionEmptyPanel()
        {
            if (_emptyPanel == null || _split == null) return;

            const int headerHeight = 30;
            int width = _split.Panel1.ClientSize.Width;
            int height = Math.Max(0, _split.Panel1.ClientSize.Height - headerHeight);
            _emptyPanel.Bounds = new Rectangle(0, headerHeight, width, height);
        }

        private ExecutionRecord GetSelected()
        {
            if (_list.SelectedIndices.Count == 0) return null;
            int index = _list.SelectedIndices[0];
            return index >= 0 && index < _rows.Count ? _rows[index] : null;
        }

        private void SelectAllRows()
        {
            if (_rows.Count == 0) return;

            _list.BeginUpdate();
            try
            {
                _list.SelectedIndices.Clear();
                for (int i = 0; i < _rows.Count; i++) _list.SelectedIndices.Add(i);
            }
            finally
            {
                _list.EndUpdate();
            }

            UpdatePreview();
            UpdateSelectionStatus();
        }

        private void UpdateSelectionStatus()
        {
            int count = _list.SelectedIndices.Count;
            _selectionLabel.Text = count > 1 ? "已选 " + count + " 条" : "";
        }

        private void ShowToast(string message, bool error = false)
        {
            _toast.Text = message;
            _toast.BackColor = error
                ? Color.FromArgb(220, 53, 69)
                : Color.FromArgb(22, 163, 74);
            _toast.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 80), 0);
            _toast.Visible = true;
            _toast.BringToFront();

            _toast.Left = Math.Max(16, (ClientSize.Width - _toast.Width) / 2);
            _toast.Top = ClientSize.Height - 96 - _toast.Height;

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void CopySelected()
        {
            var row = GetSelected();
            if (row == null)
            {
                _status.Text = "先选中一条记录";
                ShowToast("请先选中一条 SQL 记录", true);
                return;
            }

            if (string.IsNullOrEmpty(row.SqlText))
            {
                _status.Text = "这条记录没有 SQL 文本";
                ShowToast("这条记录没有 SQL 文本", true);
                return;
            }

            try
            {
                Clipboard.SetDataObject(row.SqlText, true);
                int selected = _list.SelectedIndices.Count;
                string suffix = selected > 1 ? "（选中 " + selected + " 条，已复制第一条）" : "";
                _status.Text = "已复制 SQL #" + row.Id +
                               "（" + row.SqlText.Length + " 字符）" + suffix;
                ShowToast("已复制 SQL（" + row.SqlText.Length + " 字符）" + suffix);
                Log.Info("[历史] 已复制 SQL #" + row.Id + "，长度=" + row.SqlText.Length +
                         (selected > 1 ? "，选中=" + selected : ""));
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 复制失败: " + ex.Message);
                _status.Text = "复制失败：" + ex.Message;
                ShowToast("复制失败：" + ex.Message, true);
            }
        }

        // ── 回收站 / 删除 ──

        private void ApplyRecycleMode()
        {
            bool recycle = _recycleBin.Checked;

            _deletedColumn.Width = recycle ? 130 : 0;
            _summaryColumn.Width = recycle ? 0 : 183;
            _deleteButton.Text = recycle ? "恢复" : "删除";
            _deleteButton.Style = recycle
                ? ModernButtonStyle.Success
                : ModernButtonStyle.Danger;

            ReloadAsync();
        }

        private void DeleteOrRestoreSelected()
        {
            var ids = GetSelectedIds();
            bool recycle = _recycleBin.Checked;
            string action = recycle ? "恢复" : "删除";

            if (ids.Count == 0)
            {
                _status.Text = "先选中要" + action + "的记录";
                ShowToast("请先选中要" + action + "的记录", true);
                return;
            }

            string message = recycle
                ? "恢复选中的 " + ids.Count + " 条记录？"
                : "删除选中的 " + ids.Count + " 条记录？\r\n\r\n" +
                  "删除后可在「回收站」恢复，保留 30 天。";

            if (MessageBox.Show(this, message, action + "记录",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            var db = HistoryRuntime.Database;
            if (db == null || !db.IsAvailable)
            {
                _status.Text = "历史存储不可用";
                return;
            }

            var task = recycle
                ? db.RestoreExecutionsAsync(ids)
                : db.DeleteExecutionsAsync(ids);

            task.ContinueWith(t =>
            {
                if (IsDisposed) return;

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (t.IsFaulted)
                        {
                            var error = t.Exception?.GetBaseException();
                            Log.Warn("[历史] " + action + "失败: " + error?.Message);
                            _status.Text = action + "失败：" + error?.Message;
                            return;
                        }

                        _status.Text = "已" + action + " " + t.Result + " 条";
                        ShowToast("已" + action + " " + t.Result + " 条");
                        ReloadAsync();
                    }));
                }
                catch (InvalidOperationException) { /* 窗口已关闭 */ }
            });
        }

        private List<long> GetSelectedIds()
        {
            var ids = new List<long>();

            foreach (int index in _list.SelectedIndices)
            {
                if (index >= 0 && index < _rows.Count) ids.Add(_rows[index].Id);
            }

            return ids;
        }

        private void OpenDataDirectory()
        {
            try
            {
                string dir = Path.GetDirectoryName(HistoryRuntime.DatabasePath);
                if (string.IsNullOrEmpty(dir)) return;

                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                _status.Text = "历史目录：" + dir;
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 打开目录失败: " + ex.Message);
                _status.Text = "打开目录失败：" + ex.Message;
            }
        }

        // ── 重开 ──

        private void OpenSelected()
        {
            var row = GetSelected();
            if (row == null)
            {
                _status.Text = "先选中一条记录";
                ShowToast("请先选中一条 SQL 记录", true);
                return;
            }

            if (string.IsNullOrEmpty(row.SqlText))
            {
                _status.Text = "这条记录没有 SQL 文本";
                ShowToast("这条记录没有 SQL 文本", true);
                return;
            }

            var current = CompletionRuntime.Current?.Connection;

            if (!string.IsNullOrEmpty(row.ServerName) &&
                !string.IsNullOrEmpty(current?.Server) &&
                !string.Equals(row.ServerName.Trim(), current.Server.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(
                    this,
                    "当前连接是：\r\n" + current.Server + " / " + (current.Database ?? "?") + "\r\n\r\n" +
                    "这条历史来自：\r\n" + row.ServerName + " / " + (row.DatabaseName ?? "?") + "\r\n\r\n" +
                    "仍要在当前连接下打开吗？",
                    "连接不一致",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes) return;
            }

            string text = row.SqlText;
            if (!string.IsNullOrEmpty(row.DatabaseName) &&
                !string.Equals(row.DatabaseName, current?.Database, StringComparison.OrdinalIgnoreCase))
            {
                text = "USE [" + row.DatabaseName.Replace("]", "]]") + "];\r\n" + text;
            }

            if (_dte == null)
            {
                CopyToClipboard(text, "DTE 不可用，SQL 已复制到剪贴板");
                return;
            }

            try
            {
                if (!TryNewQuery(_dte))
                {
                    CopyToClipboard(text, "无法新建查询窗口，SQL 已复制到剪贴板");
                    return;
                }

                var document = _dte.ActiveDocument;
                var textDocument = document?.Object("TextDocument") as TextDocument;
                if (textDocument == null)
                {
                    CopyToClipboard(text, "无法写入查询窗口，SQL 已复制到剪贴板");
                    return;
                }

                textDocument.StartPoint.CreateEditPoint().Insert(text);
                _status.Text = "已在新的查询窗口打开（历史 #" + row.Id + "）";
                ShowToast("已在新的查询窗口打开（历史 #" + row.Id + "）");
            }
            catch (Exception ex)
            {
                Log.Error("[历史] 打开 SQL 失败", ex);
                CopyToClipboard(text, "打开失败：" + ex.Message + "\r\nSQL 已复制到剪贴板");
            }
        }

        private static bool TryNewQuery(DTE dte)
        {
            string[] commands = { "File.NewQuery", "Query.NewQuery" };

            foreach (string command in commands)
            {
                try
                {
                    dte.ExecuteCommand(command);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("[历史] 命令 " + command + " 失败: " + ex.Message);
                }
            }

            return false;
        }

        private void CopyToClipboard(string text, string message)
        {
            try { Clipboard.SetText(text); }
            catch { }

            _status.Text = message;
            MessageBox.Show(this, message, "SQL 历史",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private sealed class HandleOwner : IWin32Window
        {
            public HandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
