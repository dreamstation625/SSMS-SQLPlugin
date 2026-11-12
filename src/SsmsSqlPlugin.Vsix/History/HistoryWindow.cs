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
        private readonly Button _queryButton;
        private readonly CheckBox _recycleBin;
        private readonly ListView _list;
        private readonly SplitContainer _split;
        private readonly Label _previewInfo;
        private readonly Label _previewPath;
        private readonly ToolTip _previewToolTip;
        private readonly TextBox _previewText;
        private readonly ColumnHeader _summaryColumn;
        private readonly ColumnHeader _deletedColumn;
        private readonly Button _deleteButton;
        private readonly Label _status;
        private readonly Timer _debounce;

        private List<ExecutionRecord> _rows = new List<ExecutionRecord>();
        private bool _loading;
        private bool _pendingReload;
        private bool _filtersLoaded;

        private HistoryWindow(DTE dte)
        {
            _dte = dte;

            Text = "SQL 历史";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1320, 680);
            MinimumSize = new Size(1000, 480);
            ShowInTaskbar = false;
            KeyPreview = true;
            Font = SystemFonts.MessageBoxFont;

            _debounce = new Timer { Interval = 350 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); ReloadAsync(); };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(6),
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            // ── 筛选行 ──
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 4, 0, 0),
            };

            top.Controls.Add(MakeLabel("搜索 SQL"));
            _search = new TextBox { Width = 240, Margin = new Padding(3, 3, 10, 3) };
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ReloadAsync();
            };
            top.Controls.Add(_search);

            top.Controls.Add(MakeLabel("服务器"));
            _server = MakeCombo(150);
            _server.SelectedIndexChanged += async (s, e) => await ReloadDatabasesAsync();
            top.Controls.Add(_server);

            top.Controls.Add(MakeLabel("库"));
            _database = MakeCombo(150);
            top.Controls.Add(_database);

            top.Controls.Add(MakeLabel("时间"));
            _range = MakeCombo(110);
            _range.Items.AddRange(new object[] { "全部时间", "最近 1 天", "最近 7 天", "最近 30 天", "最近 90 天" });
            _range.SelectedIndex = 0;
            top.Controls.Add(_range);

            top.Controls.Add(MakeLabel("类型"));
            _kind = MakeCombo(100);
            _kind.Items.AddRange(new object[] { "全部类型", "执行", "自动记录" });
            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (s, e) => ReloadAsync();
            top.Controls.Add(_kind);
            _queryButton = new Button { Text = "查询", Width = 70, Height = 26, Margin = new Padding(6, 3, 3, 3) };
            _queryButton.Click += (s, e) => ReloadAsync();
            top.Controls.Add(_queryButton);

            _recycleBin = new CheckBox
            {
                Text = "回收站",
                AutoSize = true,
                Margin = new Padding(10, 6, 3, 3),
            };
            _recycleBin.CheckedChanged += (s, e) => ApplyRecycleMode();
            top.Controls.Add(_recycleBin);

            root.Controls.Add(top, 0, 0);

            // ── 列表 + 右侧预览 ──
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                VirtualMode = true,
                MultiSelect = true,
                GridLines = false,
            };
            _list.Columns.Add("时间", 125, HorizontalAlignment.Left);
            _list.Columns.Add("服务器", 120, HorizontalAlignment.Left);
            _list.Columns.Add("库", 95, HorizontalAlignment.Left);
            _list.Columns.Add("文件名", 135, HorizontalAlignment.Left);
            _list.Columns.Add("耗时", 65, HorizontalAlignment.Right);
            _list.Columns.Add("行数", 45, HorizontalAlignment.Right);
            _list.Columns.Add("结果", 45, HorizontalAlignment.Left);
            _list.Columns.Add("类型", 45, HorizontalAlignment.Left);
            _summaryColumn = _list.Columns.Add("摘要", 180, HorizontalAlignment.Left);
            _deletedColumn = _list.Columns.Add("删除时间", 0, HorizontalAlignment.Left);

            _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
            _list.ItemActivate += (s, e) => OpenSelected();
            _list.SelectedIndexChanged += (s, e) => UpdatePreview();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSelected(); }
                else if (e.Control && e.KeyCode == Keys.C) CopySelected();
            };

            var preview = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(6, 0, 0, 0),
            };
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var infoPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
            };
            infoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            infoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

            _previewInfo = new Label
            {
                Dock = DockStyle.Fill,
                Text = "预览 · 选中一条记录查看 SQL",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };

            _previewPath = new Label
            {
                Dock = DockStyle.Fill,
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font(Font.FontFamily, 8.25f),
                ForeColor = SystemColors.GrayText,
            };

            _previewToolTip = new ToolTip();
            infoPanel.Controls.Add(_previewInfo, 0, 0);
            infoPanel.Controls.Add(_previewPath, 0, 1);

            _previewText = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Font = new Font("Consolas", 9.5f),
            };

            preview.Controls.Add(infoPanel, 0, 0);
            preview.Controls.Add(_previewText, 0, 1);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                SplitterWidth = 5,
            };
            _split.Panel1.Controls.Add(_list);
            _split.Panel2.Controls.Add(preview);

            root.Controls.Add(_split, 0, 1);

            // ── 底栏 ──
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _status = new Label
            {
                Text = "历史存储准备中…",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            bottom.Controls.Add(_status, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Dock = DockStyle.Fill,
            };

            var close = new Button { Text = "关闭", Width = 70, Height = 26 };
            close.Click += (s, e) => Close();
            var copy = new Button { Text = "复制 SQL", Width = 84, Height = 26 };
            copy.Click += (s, e) => CopySelected();
            var open = new Button { Text = "打开 SQL", Width = 84, Height = 26 };
            open.Click += (s, e) => OpenSelected();

            _deleteButton = new Button { Text = "删除", Width = 70, Height = 26 };
            _deleteButton.Click += (s, e) => DeleteOrRestoreSelected();

            var refresh = new Button { Text = "刷新", Width = 70, Height = 26 };
            refresh.Click += async (s, e) =>
            {
                _filtersLoaded = false;
                await ReloadFiltersAsync();
                ReloadAsync();
            };

            var openDir = new Button { Text = "打开目录", Width = 84, Height = 26 };
            openDir.Click += (s, e) => OpenDataDirectory();

            // RightToLeft 流：先加的在最右
            buttons.Controls.Add(close);
            buttons.Controls.Add(open);
            buttons.Controls.Add(copy);
            buttons.Controls.Add(_deleteButton);
            buttons.Controls.Add(refresh);
            buttons.Controls.Add(openDir);
            bottom.Controls.Add(buttons, 1, 0);

            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);
        }

        private static Label MakeLabel(string text)
            => new Label { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };

        private static ComboBox MakeCombo(int width)
            => new ComboBox
            {
                Width = width,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(3, 3, 10, 3),
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
                _split.Panel1MinSize = 520;
                _split.Panel2MinSize = 320;
                _split.SplitterDistance = Math.Max(520, ClientSize.Width - 445);
            }
            catch { }

            await ReloadFiltersAsync();
            ReloadAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Dispose();
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

                            try
                            {
                                _list.SelectedIndices.Clear();
                                if (_rows.Count > 0) _list.SelectedIndices.Add(0);
                            }
                            catch { }

                            UpdatePreview();

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
                FormatTime(row.StartedUtc),
                row.ServerName ?? "",
                row.DatabaseName ?? "",
                FileName(row),
                isExecute ? (row.DurationMs > 0 ? row.DurationMs + " ms" : "—") : "—",
                isExecute && row.Succeeded >= 0 ? row.RowsAffected.ToString() : "—",
                isExecute ? FormatResult(row.Succeeded) : "—",
                FormatKind(row.Kind),
                Summarize(row.SqlText),
                FormatTime(row.DeletedUtc),
            });
        }

        private void UpdatePreview()
        {
            if (_previewText == null) return;

            var row = GetSelected();
            if (row == null)
            {
                _previewInfo.Text = "预览 · 选中一条记录查看 SQL";
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

            _previewInfo.Text = "预览 #" + row.Id +
                                " · " + FormatKind(row.Kind) +
                                " · " + FormatTime(row.StartedUtc) +
                                " · " + (row.ServerName ?? "?") + " / " + (row.DatabaseName ?? "?") +
                                " · " + duration +
                                " · " + result +
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
            _previewText.SelectionStart = 0;
            _previewText.SelectionLength = 0;
            try { _previewText.ScrollToCaret(); } catch { }
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

        private ExecutionRecord GetSelected()
        {
            if (_list.SelectedIndices.Count == 0) return null;
            int index = _list.SelectedIndices[0];
            return index >= 0 && index < _rows.Count ? _rows[index] : null;
        }

        private void CopySelected()
        {
            var row = GetSelected();
            if (row == null || string.IsNullOrEmpty(row.SqlText)) return;

            try { Clipboard.SetText(row.SqlText); }
            catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; }
        }

        // ── 回收站 / 删除 ──

        private void ApplyRecycleMode()
        {
            bool recycle = _recycleBin.Checked;

            _deletedColumn.Width = recycle ? 130 : 0;
            _summaryColumn.Width = recycle ? 50 : 180;
            _deleteButton.Text = recycle ? "恢复" : "删除";

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
            if (row == null) return;

            if (string.IsNullOrEmpty(row.SqlText))
            {
                _status.Text = "这条记录没有 SQL 文本";
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
