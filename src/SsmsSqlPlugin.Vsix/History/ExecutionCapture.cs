using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsSqlPlugin.Completion;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Storage;

namespace SsmsSqlPlugin.History
{
    /// <summary>
    /// 执行捕获。
    ///
    /// 两条入口，按「先到先建、后来去重」配对：
    ///   1) 编辑器命令过滤器的 F5（VSStd2K/0x0067）—— 键盘执行；
    ///   2) DTE CommandEvents.BeforeExecute/AfterExecute —— 键盘 + 工具栏 + 菜单。
    ///
    /// 文本在命令真正执行前抓：选区优先，无选区取全文。
    /// 开始时就落一条 Executions（Succeeded=-1 表示统计未知），
    /// AfterExecute 再按 Id 补写 DurationMs —— 即使中途 SSMS 崩溃，
    /// 也能留下「执行过什么」。
    /// </summary>
    internal sealed class ExecutionCapture
    {
        internal static readonly Guid VsStd2K = new Guid("1496a755-94de-11d0-8c3f-00c04fc2aae2");
        private const uint CmdExecute = 0x0067;

        /// <summary>同一视图、同一 SQL 在极短时间内被两条入口同时看到时的去重窗口。</summary>
        private const int DedupeWindowMs = 1500;

        private static readonly object StartGate = new object();
        private static ExecutionCapture _current;

        private readonly DTE _dte;
        private CommandEvents _commandEvents;
        private readonly Dictionary<string, string> _commandNames = new Dictionary<string, string>();
        private readonly HashSet<string> _loggedCommands = new HashSet<string>();
        private readonly object _gate = new object();
        private readonly List<Capture> _active = new List<Capture>();

        public static ExecutionCapture Current => _current;

        private ExecutionCapture(DTE dte)
        {
            _dte = dte;
        }

        public static void Start(DTE dte)
        {
            lock (StartGate)
            {
                if (_current != null)
                {
                    _current.TrySubscribeDte();
                    return;
                }

                _current = new ExecutionCapture(dte);
                _current.TrySubscribeDte();
            }
        }

        public static void Stop()
        {
            lock (StartGate)
            {
                var current = _current;
                _current = null;
                current?.UnsubscribeDte();
            }
        }

        /// <summary>编辑器命令过滤器回调：目前只处理实测确认的 F5。</summary>
        public static void OnEditorCommand(IVsTextView view, Guid group, uint id)
        {
            if (group != VsStd2K || id != CmdExecute) return;

            try { _current?.HandleEditorCommand(view, group, id); }
            catch (Exception ex) { Log.Warn("[历史] F5 捕获失败: " + ex.Message); }
        }

        // ── DTE 事件 ──

        private void TrySubscribeDte()
        {
            if (_dte == null || _commandEvents != null) return;

            try
            {
                _commandEvents = _dte.Events.CommandEvents;
                if (_commandEvents == null)
                {
                    Log.Warn("[历史] DTE.CommandEvents 不可用，工具栏执行不会被记录");
                    return;
                }

                _commandEvents.BeforeExecute += OnBeforeExecute;
                _commandEvents.AfterExecute += OnAfterExecute;
                Log.Info("[历史] DTE 执行事件已挂");
            }
            catch (Exception ex)
            {
                Log.Error("[历史] 挂 DTE 执行事件失败", ex);
            }
        }

        private void UnsubscribeDte()
        {
            if (_commandEvents == null) return;

            try
            {
                _commandEvents.BeforeExecute -= OnBeforeExecute;
                _commandEvents.AfterExecute -= OnAfterExecute;
            }
            catch { }

            _commandEvents = null;
        }

        private void OnBeforeExecute(string guidText, int id, object customIn, object customOut,
                                     ref bool cancelDefault)
        {
            try
            {
                if (!IsExecuteCommand(guidText, id)) return;

                var view = EditorLocator.GetActiveView(ServiceProviderHolder.Provider, mustBeSql: false);
                if (view == null || !EditorLocator.IsSqlView(view))
                {
                    Log.Info("[历史] 执行命令命中，但当前不是 SQL 编辑器，跳过");
                    return;
                }

                Begin(KeyOf(guidText, id), view, "DTE");
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] BeforeExecute 处理失败: " + ex.Message);
            }
        }

        private void OnAfterExecute(string guidText, int id, object customIn, object customOut)
        {
            try
            {
                if (!IsExecuteCommand(guidText, id)) return;
                Finish(KeyOf(guidText, id));
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] AfterExecute 处理失败: " + ex.Message);
            }
        }

        private void HandleEditorCommand(IVsTextView view, Guid group, uint id)
        {
            if (view == null) return;
            Begin(KeyOf(group.ToString("D"), (int)id), view, "F5");
        }

        // ── 命令识别 ──

        private bool IsExecuteCommand(string guidText, int id)
        {
            if (Guid.TryParse(guidText, out Guid guid) && guid == VsStd2K && (uint)id == CmdExecute)
                return true;

            string name = GetCommandName(guidText, id);

            if (Environment.GetEnvironmentVariable("SQLKIT_EXEC_PROBE") == "1" &&
                !string.IsNullOrEmpty(name) &&
                (name.IndexOf("xecut", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("Query", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                lock (_loggedCommands)
                {
                    if (_loggedCommands.Add(guidText + ":" + id))
                        Log.Info("[历史探针] 命令 " + name + " = " + guidText + " / 0x" + id.ToString("X4"));
                }
            }

            return IsExecuteCommandName(name);
        }

        /// <summary>
        /// 不硬编码 SSMS 的 Query 命令 GUID（版本间会变），用名字判定：
        /// 只认 Query/SQL 前缀的 .Execute，排除 Cancel。
        /// 工具栏「执行」在 SSMS 里是 Query.Execute。
        /// </summary>
        private static bool IsExecuteCommandName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (name.Equals("Execute", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("Query.Execute", StringComparison.OrdinalIgnoreCase)) return true;

            if (!name.EndsWith(".Execute", StringComparison.OrdinalIgnoreCase)) return false;

            return name.StartsWith("Query.", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("SQL.", StringComparison.OrdinalIgnoreCase);
        }

        private string GetCommandName(string guidText, int id)
        {
            string key = guidText + ":" + id;
            lock (_commandNames)
            {
                if (_commandNames.TryGetValue(key, out string cached)) return cached;
            }

            string name = null;
            try
            {
                if (_dte != null)
                {
                    Command command = _dte.Commands.Item(guidText, id);
                    name = command?.Name;
                }
            }
            catch { }

            lock (_commandNames) { _commandNames[key] = name; }
            return name;
        }

        private static string KeyOf(string guidText, int id) => guidText + ":" + id;

        // ── 捕获生命周期 ──

        private void Begin(string key, IVsTextView view, string source)
        {
            if (!TryGetSqlText(view, out string sql, out bool selectionOnly))
            {
                Log.Info("[历史] 执行命令未取到 SQL 文本，跳过（" + source + "）");
                return;
            }

            string sqlHash = Sha256Hex(sql);
            string viewKey = RuntimeHelpers.GetHashCode(view).ToString("x8");
            Capture capture;

            lock (_gate)
            {
                CleanupLocked();

                capture = FindDedupeLocked(viewKey, sqlHash, source);
                if (capture != null)
                {
                    capture.Keys.Add(key);
                    Log.Info("[历史] 两条入口同时命中，合并为一条（" + source + "）");
                    return;
                }

                capture = CreateLocked(key, source, view, viewKey, sql, sqlHash, selectionOnly);
            }

            capture.InsertTask = StartInsert(capture.Record);
        }

        private Capture CreateLocked(string key, string source, IVsTextView view,
                                     string viewKey, string sql, string sqlHash, bool selectionOnly)
        {
            var connection = CompletionRuntime.Current?.Connection;
            string path = null;

            try { path = EditorLocator.GetDocumentPath(view); }
            catch { }

            // IVsUserData 在某些投影缓冲区上读不到路径，用宿主提供的编辑器 moniker 兜底。
            if (string.IsNullOrEmpty(path)) path = connection?.EditorMoniker;

            string moniker = connection?.EditorMoniker;
            if (string.IsNullOrEmpty(moniker)) moniker = path;
            if (string.IsNullOrEmpty(moniker)) moniker = "view:" + viewKey;

            // 未保存查询的 moniker 在 %TEMP% 下，不能当成用户文件；
            // 只有真实存在的 .sql/.sqlplan/.ddl 才记 SourcePath（S3.4 快照只收这些）。
            bool isUserFile = !string.IsNullOrEmpty(path) &&
                              !path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
                              (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".ddl", StringComparison.OrdinalIgnoreCase));

            var record = new ExecutionRecord
            {
                StartedUtc = DateTime.UtcNow.ToString("o"),
                DurationMs = 0,
                ServerName = connection?.Server,
                DatabaseName = connection?.Database,
                LoginName = TryGetLoginName(connection?.ConnectionString),
                RowsAffected = 0,
                Succeeded = -1,       // 统计未知；UI 显示“未知”，不要显示成失败
                ErrorNumber = 0,
                ErrorMessage = null,
                SourcePath = isUserFile ? path : null,
                EditorMoniker = moniker,
                IsSelectionOnly = selectionOnly ? 1 : 0,
                SqlText = sql,
            };

            var capture = new Capture
            {
                SqlHash = sqlHash,
                ViewKey = viewKey,
                StartedUtc = DateTime.UtcNow,
                Record = record,
            };
            capture.Keys.Add(key);
            capture.Sources.Add(source);

            _active.Add(capture);
            if (_active.Count > 64) _active.RemoveAt(0);

            Log.Info("[历史] 捕获执行: " + source +
                     " 长度=" + sql.Length + " 选区=" + selectionOnly +
                     " 服务器=" + (record.ServerName ?? "?") +
                     " 库=" + (record.DatabaseName ?? "?") +
                     " 文件=" + (path ?? "(未保存)"));
            return capture;
        }

        private Task StartInsert(ExecutionRecord record)
        {
            return HistoryRuntime.InsertAsync(record).ContinueWith(t =>
            {
                var ex = t.Exception?.GetBaseException();
                if (ex != null) Log.Warn("[历史] 写入执行记录失败: " + ex.Message);
            }, TaskScheduler.Default);
        }

        private void Finish(string key)
        {
            Capture capture = null;

            lock (_gate)
            {
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    var item = _active[i];
                    if (item.Finished || !item.Keys.Contains(key)) continue;

                    item.Finished = true;
                    capture = item;
                    _active.RemoveAt(i);
                    break;
                }
            }

            if (capture == null) return;

            capture.Record.DurationMs = capture.Clock.ElapsedMilliseconds;
            Log.Info("[历史] 执行结束: 耗时=" + capture.Record.DurationMs + "ms");

            _ = FinishAsync(capture);
        }

        private static async Task FinishAsync(Capture capture)
        {
            try
            {
                await capture.InsertTask.ConfigureAwait(false);
                if (capture.Record.Id <= 0) return;

                await HistoryRuntime.UpdateAsync(capture.Record).ConfigureAwait(false);
                Log.Info("[历史] 已更新耗时: Id=" + capture.Record.Id +
                         " " + capture.Record.DurationMs + "ms");
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 更新执行耗时失败: " + ex.Message);
            }
        }

        private Capture FindDedupeLocked(string viewKey, string sqlHash, string source)
        {
            var now = DateTime.UtcNow;

            foreach (var item in _active)
            {
                if (item.Finished) continue;
                if (item.ViewKey != viewKey) continue;
                if (item.SqlHash != sqlHash) continue;
                if ((now - item.StartedUtc).TotalMilliseconds > DedupeWindowMs) continue;

                // 同一入口重复上报（例如 DTE 对 F5 连发两次）不当成重复执行；
                // 只有「编辑器 F5」与「DTE」互相合并。
                if (item.Sources.Contains(source)) continue;

                item.Sources.Add(source);
                return item;
            }

            return null;
        }

        private void CleanupLocked()
        {
            var now = DateTime.UtcNow;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if ((now - _active[i].StartedUtc).TotalSeconds > 60) _active.RemoveAt(i);
            }
        }

        // ── SQL 文本 ──

        /// <summary>选区优先；无选区取全文。与 M0 探针一致。</summary>
        internal static bool TryGetSqlText(IVsTextView view, out string sql, out bool selectionOnly)
        {
            sql = null;
            selectionOnly = false;

            if (view == null) return false;

            try
            {
                if (view.GetSelectedText(out string selected) == 0 &&
                    !string.IsNullOrWhiteSpace(selected))
                {
                    sql = selected;
                    selectionOnly = true;
                    return true;
                }
            }
            catch { }

            return TryGetFullText(view, out sql);
        }

        /// <summary>只取全文（忽略选区），自动记录用。</summary>
        internal static bool TryGetFullText(IVsTextView view, out string sql)
        {
            sql = null;
            if (view == null) return false;

            try
            {
                if (view.GetBuffer(out IVsTextLines buffer) != 0 || buffer == null) return false;

                int lastLine, lastIndex;
                if (buffer.GetLastLineIndex(out lastLine, out lastIndex) != 0) return false;
                if (buffer.GetLineText(0, 0, lastLine, lastIndex, out sql) != 0) return false;

                return !string.IsNullOrWhiteSpace(sql);
            }
            catch
            {
                return false;
            }
        }

        // ── 杂项 ──

        /// <summary>从连接串里只取登录名；不记录、不返回连接串本身（含密码）。</summary>
        private static string TryGetLoginName(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString)) return null;

            foreach (string part in connectionString.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;

                string key = part.Substring(0, eq).Trim();
                if (!key.Equals("User ID", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("UID", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("User", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = part.Substring(eq + 1).Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }

            return null;
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private sealed class Capture
        {
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Stopwatch Clock = Stopwatch.StartNew();

            public string SqlHash;
            public string ViewKey;
            public DateTime StartedUtc;
            public ExecutionRecord Record;
            public Task InsertTask;
            public bool Finished;
        }
    }
}
