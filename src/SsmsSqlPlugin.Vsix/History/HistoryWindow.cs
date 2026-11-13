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
using SsmsSqlPlugin.Localization;
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
        private readonly ColumnHeader _fillerColumn;
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

            Text = Loc.T("SQL History");
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
                Text = Loc.T("SQL History"),
                AutoSize = true,
                Font = HistoryTheme.TitleFont,
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Background,
                Margin = Padding.Empty,
            };

            var subtitle = new Label
            {
                Text = Loc.T("Search, preview, reopen and auto-save"),
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
                FillColor = HistoryTheme.CardSoft,
                BorderColor = HistoryTheme.Border,
                Padding = new Padding(10, 7, 10, 0),
                Margin = new Padding(0, 0, 16, 0),
            };

            _search = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = HistoryTheme.UiFont,
                BackColor = HistoryTheme.CardSoft,
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

            top.Controls.Add(MakeLabel(Loc.T("Server")));
            _server = MakeCombo(140);
            _server.SelectedIndexChanged += async (s, e) => await ReloadDatabasesAsync();
            top.Controls.Add(_server);

            top.Controls.Add(MakeLabel(Loc.T("Database")));
            _database = MakeCombo(130);
            top.Controls.Add(_database);

            top.Controls.Add(MakeLabel(Loc.T("Time")));
            _range = MakeCombo(105);
            _range.Items.AddRange(new object[]
            {
                Loc.T("All time"),
                Loc.T("Last 1 day"),
                Loc.T("Last 7 days"),
                Loc.T("Last 30 days"),
                Loc.T("Last 90 days"),
            });
            _range.SelectedIndex = 0;
            top.Controls.Add(_range);

            top.Controls.Add(MakeLabel(Loc.T("Type")));
            _kind = MakeCombo(100);
            _kind.Items.AddRange(new object[]
            {
                Loc.T("All types"),
                Loc.T("Execute"),
                Loc.T("Auto-save"),
            });
            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (s, e) => ReloadAsync();
            top.Controls.Add(_kind);

            _queryButton = new ModernButton
            {
                Text = Loc.T("Query"),
                Style = ModernButtonStyle.Primary,
                Width = 80,
                Height = 32,
                Margin = new Padding(4, 0, 12, 0),
            };
            _queryButton.Click += (s, e) => ReloadAsync();
            top.Controls.Add(_queryButton);

            _recycleBin = new ModernCheckBox
            {
                Text = Loc.T("Recycle Bin"),
                Width = 120,
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
            _list.Columns.Add(Loc.T("Time"), 140, HorizontalAlignment.Left);
            _list.Columns.Add(Loc.T("Server"), 110, HorizontalAlignment.Left);
            _list.Columns.Add(Loc.T("Database"), 90, HorizontalAlignment.Left);
            _list.Columns.Add(Loc.T("File Name"), 120, HorizontalAlignment.Left);
            _list.Columns.Add(Loc.T("Duration"), 90, HorizontalAlignment.Right);
            _list.Columns.Add(Loc.T("Rows"), 64, HorizontalAlignment.Right);
            // 类型列用 Tag 标记，HistoryUi 按 Tag 决定是否画徽标（不比较本地化后的标题）
            var kindColumn = _list.Columns.Add(Loc.T("Type"), 76, HorizontalAlignment.Left);
            kindColumn.Tag = "kind";
            _summaryColumn = _list.Columns.Add(Loc.T("Summary"), 171, HorizontalAlignment.Left);
            _deletedColumn = _list.Columns.Add(Loc.T("Deleted"), 0, HorizontalAlignment.Left);
            _fillerColumn = _list.Columns.Add("", 0, HorizontalAlignment.Left);
            _list.SizeChanged += (s, e) => UpdateFillerColumn();
            UpdateFillerColumn();

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
                Text = Loc.T("SQL Preview"),
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
                Text = Loc.T("Select a record to see the full SQL and connection details"),
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
                Text = Loc.T("No matching records") + "\r\n" +
                       Loc.T("Adjust the search or filters and try again"),
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
                Text = Loc.T("Preparing history storage…"),
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
                Text = Loc.T("Close"),
                Style = ModernButtonStyle.Ghost,
                Width = 68,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            close.Click += (s, e) => Close();

            var open = new ModernButton
            {
                Text = Loc.T("Open SQL"),
                Style = ModernButtonStyle.Primary,
                Width = 96,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            open.Click += (s, e) => OpenSelected();

            var copy = new ModernButton
            {
                Text = Loc.T("Copy SQL"),
                Style = ModernButtonStyle.Secondary,
                Width = 94,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            copy.Click += (s, e) => CopySelected();

            _deleteButton = new ModernButton
            {
                Text = Loc.T("Delete"),
                Style = ModernButtonStyle.Danger,
                Width = 76,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            _deleteButton.Click += (s, e) => DeleteOrRestoreSelected();

            var refresh = new ModernButton
            {
                Text = Loc.T("Refresh"),
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
                Text = Loc.T("Open Folder"),
                Style = ModernButtonStyle.Ghost,
                Width = 96,
                Height = 32,
                Margin = new Padding(8, 0, 0, 0),
            };
            openDir.Click += (s, e) => OpenDataDirectory();

            var selectAll = new ModernButton
            {
                Text = Loc.T("Select All"),
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

            _previewToolTip.SetToolTip(open, Loc.T("Open the selected SQL in a new query window (double-click or Enter)"));
            _previewToolTip.SetToolTip(copy, Loc.T("Copy the selected SQL (Ctrl+C)"));
            _previewToolTip.SetToolTip(_deleteButton,
                Loc.T("Delete selected records (Ctrl/Shift to multi-select, Ctrl+A to select all, Delete key to delete)"));
            _previewToolTip.SetToolTip(selectAll, Loc.T("Select all records in the list (Ctrl+A)"));

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
            => new ThemedComboBox
            {
                Width = width,
                DropDownStyle = ComboBoxStyle.DropDownList,
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
            try { owner = (IntPtr)(long)dte.MainWindow.HWnd; } catch { }

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

            CueBanner.Set(_search, Loc.T("Search SQL text..."));

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
                _status.Text = Loc.F("History storage is not ready: {0}",
                    db?.Error ?? Loc.T("Opening database"));
                return;
            }

            try
            {
                var servers = await db.GetDistinctServersAsync();

                string selectedServer = SelectedText(_server);
                _server.BeginUpdate();
                _server.Items.Clear();
                _server.Items.Add(Loc.T("(All)"));
                foreach (string value in servers) _server.Items.Add(value);
                _server.SelectedIndex = FindItem(_server, selectedServer);
                _server.EndUpdate();

                await ReloadDatabasesAsync();
                _filtersLoaded = true;
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 读取筛选项失败: " + ex.Message);
                _status.Text = Loc.F("Failed to load filters: {0}", ex.Message);
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
                _database.Items.Add(Loc.T("(All)"));
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
                _status.Text = Loc.F("History storage is unavailable: {0}",
                    db?.Error ?? Loc.T("Opening database"));
                return;
            }

            var query = BuildQuery();
            var sw = Stopwatch.StartNew();

            _loading = true;
            _queryButton.Enabled = false;
            _status.Text = Loc.T("Querying…");

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
                            _status.Text = Loc.F("Query failed: {0}", error?.Message);
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

                            _status.Text = (_recycleBin.Checked ? Loc.T("Recycle Bin ·") + " " : "") +
                                           Loc.F("Total: {0}", _rows.Count) +
                                           (_rows.Count >= query.Limit ? Loc.F("(limit {0})", query.Limit) : "") +
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
            // VirtualMode 要求每个列都有子项，包括末尾用于补齐表头的占位列。
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count)
            {
                e.Item = new ListViewItem(new string[_list.Columns.Count]);
                return;
            }

            var row = _rows[e.ItemIndex];
            bool isExecute = string.IsNullOrEmpty(row.Kind) || row.Kind == "execute";

            // 数组长度必须跟随列数，否则 ListView 会抛 InvalidOperationException。
            var values = new string[_list.Columns.Count];
            values[0] = FormatListTime(row.StartedUtc);
            values[1] = row.ServerName ?? "";
            values[2] = row.DatabaseName ?? "";
            values[3] = FileName(row);
            values[4] = isExecute ? (row.DurationMs > 0 ? row.DurationMs + " ms" : "—") : "—";
            values[5] = isExecute && row.Succeeded >= 0 ? row.RowsAffected.ToString() : "—";
            values[6] = FormatKind(row.Kind);
            values[7] = Summarize(row.SqlText);
            values[8] = FormatListTime(row.DeletedUtc);

            var item = new ListViewItem(values);

            // 徽标样式按数据值（execute/edit/timer）决定，不能按本地化后的文案判断
            item.Tag = row.Kind == "timer" ? "timer" : row.Kind == "edit" ? "edit" : "execute";
            e.Item = item;
        }

        private void UpdatePreview()
        {
            if (_previewText == null) return;

            var row = GetSelected();
            if (row == null)
            {
                _previewInfo.Text = Loc.T("SQL Preview");
                _previewMeta.Text = Loc.T("Select a record to see the full SQL and connection details");
                _previewPath.Text = "";
                _previewToolTip.SetToolTip(_previewPath, "");
                _previewText.Text = "";
                return;
            }

            string pathText = !string.IsNullOrEmpty(row.SourcePath)
                ? Loc.F("File: {0}", row.SourcePath)
                : !string.IsNullOrEmpty(row.EditorMoniker)
                    ? Loc.F("Ad-hoc query: {0}", row.EditorMoniker)
                    : "";

            _previewPath.Text = pathText;
            _previewToolTip.SetToolTip(_previewPath, pathText);

            bool isExecute = string.IsNullOrEmpty(row.Kind) || row.Kind == "execute";
            string duration = isExecute
                ? (row.DurationMs > 0 ? row.DurationMs + " ms" : Loc.T("Duration unknown"))
                : "—";
            string result = isExecute ? FormatResult(row.Succeeded) : "—";
            string deletedInfo = string.IsNullOrEmpty(row.DeletedUtc)
                ? ""
                : " · " + Loc.F("Deleted at {0}", FormatTime(row.DeletedUtc));

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
                _previewText.Text = Loc.T("(No SQL text was saved for this record)");
                return;
            }

            const int maxPreview = 100000;
            if (sql.Length > maxPreview)
            {
                sql = sql.Substring(0, maxPreview) +
                      "\r\n-- " + Loc.T("…(Preview truncated at 100,000 characters; use Copy SQL or double-click to open the full text)");
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
            if (succeeded == 1) return Loc.T("Succeeded");
            if (succeeded == 0) return Loc.T("Failed");
            return Loc.T("Unknown");
        }

        private static string FormatKind(string kind)
        {
            if (kind == "timer") return Loc.T("Timer");
            if (kind == "edit") return Loc.T("Edit");
            return Loc.T("Execute");
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
            _selectionLabel.Text = count > 1 ? Loc.F("{0} selected", count) : "";
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
                _status.Text = Loc.T("Select a record first");
                ShowToast(Loc.T("Select a SQL record first"), true);
                return;
            }

            if (string.IsNullOrEmpty(row.SqlText))
            {
                _status.Text = Loc.T("This record has no SQL text");
                ShowToast(Loc.T("This record has no SQL text"), true);
                return;
            }

            try
            {
                Clipboard.SetDataObject(row.SqlText, true);
                int selected = _list.SelectedIndices.Count;
                string suffix = selected > 1
                    ? Loc.F("({0} selected; the first was copied)", selected)
                    : "";
                _status.Text = Loc.F("Copied SQL #{0} ({1} characters){2}",
                    row.Id, row.SqlText.Length, suffix);
                ShowToast(Loc.F("Copied SQL ({0} characters){1}", row.SqlText.Length, suffix));
                Log.Info("[历史] 已复制 SQL #" + row.Id + "，长度=" + row.SqlText.Length +
                         (selected > 1 ? "，选中=" + selected : ""));
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 复制失败: " + ex.Message);
                string message = Loc.F("Copy failed: {0}", ex.Message);
                _status.Text = message;
                ShowToast(message, true);
            }
        }

        // ── 回收站 / 删除 ──

        private void UpdateFillerColumn()
        {
            if (_fillerColumn == null || _list == null) return;

            int used = 0;
            foreach (ColumnHeader column in _list.Columns)
            {
                if (!ReferenceEquals(column, _fillerColumn)) used += column.Width;
            }

            _fillerColumn.Width = Math.Max(0, _list.ClientSize.Width - used);
        }

        private void ApplyRecycleMode()
        {
            bool recycle = _recycleBin.Checked;

            _deletedColumn.Width = recycle ? 130 : 0;
            _summaryColumn.Width = recycle ? 0 : 171;
            UpdateFillerColumn();
            _deleteButton.Text = recycle ? Loc.T("Restore") : Loc.T("Delete");
            _deleteButton.Style = recycle
                ? ModernButtonStyle.Success
                : ModernButtonStyle.Danger;

            ReloadAsync();
        }

        private void DeleteOrRestoreSelected()
        {
            var ids = GetSelectedIds();
            bool recycle = _recycleBin.Checked;
            string logAction = recycle ? "恢复" : "删除";

            if (ids.Count == 0)
            {
                string hint = recycle
                    ? Loc.T("Select the records to restore first")
                    : Loc.T("Select the records to delete first");
                _status.Text = hint;
                ShowToast(hint, true);
                return;
            }

            string message = recycle
                ? Loc.F("Restore the selected {0} record(s)?", ids.Count)
                : Loc.F("Delete the selected {0} record(s)?", ids.Count) + "\r\n\r\n" +
                  Loc.T("Deleted records can be restored from the Recycle Bin and are kept for 30 days.");

            if (MessageBox.Show(this, message,
                    recycle ? Loc.T("Restore Records") : Loc.T("Delete Records"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            var db = HistoryRuntime.Database;
            if (db == null || !db.IsAvailable)
            {
                _status.Text = Loc.T("History storage is unavailable");
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
                            Log.Warn("[历史] " + logAction + "失败: " + error?.Message);
                            _status.Text = recycle
                                ? Loc.F("Restore failed: {0}", error?.Message)
                                : Loc.F("Delete failed: {0}", error?.Message);
                            return;
                        }

                        string done = recycle
                            ? Loc.F("Restored {0} record(s)", t.Result)
                            : Loc.F("Deleted {0} record(s)", t.Result);
                        _status.Text = done;
                        ShowToast(done);
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
                _status.Text = Loc.F("History folder: {0}", dir);
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 打开目录失败: " + ex.Message);
                _status.Text = Loc.F("Failed to open the folder: {0}", ex.Message);
            }
        }

        // ── 重开 ──

        private void OpenSelected()
        {
            var row = GetSelected();
            if (row == null)
            {
                _status.Text = Loc.T("Select a record first");
                ShowToast(Loc.T("Select a SQL record first"), true);
                return;
            }

            if (string.IsNullOrEmpty(row.SqlText))
            {
                _status.Text = Loc.T("This record has no SQL text");
                ShowToast(Loc.T("This record has no SQL text"), true);
                return;
            }

            var current = CompletionRuntime.Current?.Connection;

            if (!string.IsNullOrEmpty(row.ServerName) &&
                !string.IsNullOrEmpty(current?.Server) &&
                !string.Equals(row.ServerName.Trim(), current.Server.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(
                    this,
                    Loc.T("The current connection is:") + "\r\n" +
                    current.Server + " / " + (current.Database ?? "?") + "\r\n\r\n" +
                    Loc.T("This history item is from:") + "\r\n" +
                    row.ServerName + " / " + (row.DatabaseName ?? "?") + "\r\n\r\n" +
                    Loc.T("Open it on the current connection anyway?"),
                    Loc.T("Connection Mismatch"),
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
                CopyToClipboard(text, Loc.T("DTE is unavailable; the SQL was copied to the clipboard"));
                return;
            }

            try
            {
                if (!TryNewQuery(_dte))
                {
                    CopyToClipboard(text, Loc.T("Could not create a new query window; the SQL was copied to the clipboard"));
                    return;
                }

                var document = _dte.ActiveDocument;
                var textDocument = document?.Object("TextDocument") as TextDocument;
                if (textDocument == null)
                {
                    CopyToClipboard(text, Loc.T("Could not write to the query window; the SQL was copied to the clipboard"));
                    return;
                }

                textDocument.StartPoint.CreateEditPoint().Insert(text);
                string opened = Loc.F("Opened in a new query window (history #{0})", row.Id);
                _status.Text = opened;
                ShowToast(opened);
            }
            catch (Exception ex)
            {
                Log.Error("[历史] 打开 SQL 失败", ex);
                CopyToClipboard(text,
                    Loc.F("Open failed: {0}", ex.Message) + "\r\n" +
                    Loc.T("SQL was copied to the clipboard"));
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
            MessageBox.Show(this, message, Loc.T("SQL History"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private sealed class HandleOwner : IWin32Window
        {
            public HandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
