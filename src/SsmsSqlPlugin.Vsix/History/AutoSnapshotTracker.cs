using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using SsmsSqlPlugin.Completion;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Storage;

namespace SsmsSqlPlugin.History
{
    /// <summary>
    /// 自动记录当前 SQL 编辑器内容：
    ///   · 修改后记录：内容变化后最多每分钟落一条；
    ///   · 每 N 分钟：内容有变化才落，间隔按设置。
    /// 每 30 秒检查一次活动 SQL 编辑器；不执行也记录。
    /// </summary>
    internal sealed class AutoSnapshotTracker
    {
        private const int PollMilliseconds = 30000;
        private const int EditMinIntervalSeconds = 60;
        private const int MaxTextChars = 2 * 1024 * 1024;

        private static readonly object StartGate = new object();
        private static AutoSnapshotTracker _current;

        private readonly IServiceProvider _provider;
        private readonly Dictionary<string, EditorState> _states =
            new Dictionary<string, EditorState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _oversizeLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Timer _timer;
        private int _busy;

        private AutoSnapshotTracker(IServiceProvider provider)
        {
            _provider = provider;
        }

        public static void Start(IServiceProvider provider)
        {
            lock (StartGate)
            {
                if (_current != null) return;

                _current = new AutoSnapshotTracker(provider);
                _current._timer = new Timer(_current.OnTimer, null, 10000, PollMilliseconds);

                var settings = HistoryAutoSaveStore.Current;
                Log.Info("[历史] 自动记录已启动: " + HistoryAutoSaveStore.Describe(settings) +
                         (settings.Enabled && settings.Mode == AutoSaveMode.Interval
                             ? "（间隔 " + settings.IntervalMinutes + " 分钟）"
                             : ""));
            }
        }

        public static void Stop()
        {
            lock (StartGate)
            {
                var current = _current;
                _current = null;

                if (current == null) return;
                try { current._timer?.Dispose(); } catch { }
                current._timer = null;
                current._states.Clear();
            }
        }

        private void OnTimer(object state)
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;

            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Poll();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[历史] 自动记录检查失败: " + ex.Message);
                    }
                    finally
                    {
                        Volatile.Write(ref _busy, 0);
                    }
                });
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _busy, 0);
                Log.Warn("[历史] 自动记录调度失败: " + ex.Message);
            }
        }

        private void Poll()
        {
            var settings = HistoryAutoSaveStore.Current;
            if (!settings.Enabled || settings.Mode == AutoSaveMode.Off) return;
            if (!HistoryRuntime.IsAvailable) return;

            var view = EditorLocator.GetActiveView(_provider, mustBeSql: false);
            if (view == null || !EditorLocator.IsSqlView(view)) return;

            if (!ExecutionCapture.TryGetFullText(view, out string sql)) return;
            if (string.IsNullOrWhiteSpace(sql)) return;

            var connection = CompletionRuntime.Current?.Connection;

            string moniker = connection?.EditorMoniker;
            if (string.IsNullOrEmpty(moniker)) moniker = EditorLocator.GetDocumentPath(view);
            if (string.IsNullOrEmpty(moniker))
                moniker = "view:" + RuntimeHelpers.GetHashCode(view).ToString("x8");

            if (sql.Length > MaxTextChars)
            {
                if (_oversizeLogged.Add(moniker))
                    Log.Info("[历史] 自动记录跳过超大 SQL: " + moniker + " 长度=" + sql.Length);
                return;
            }

            string hash = Sha256Hex(sql);
            var now = DateTime.UtcNow;

            if (!_states.TryGetValue(moniker, out EditorState state))
            {
                if (_states.Count >= 64) _states.Clear();
                state = new EditorState();
                _states[moniker] = state;
            }

            if (state.LastHash == hash) return;

            bool due;
            if (settings.Mode == AutoSaveMode.Interval)
            {
                due = state.LastSnapshotUtc == DateTime.MinValue ||
                      (now - state.LastSnapshotUtc).TotalMinutes >= Math.Max(1, settings.IntervalMinutes);
            }
            else
            {
                due = state.LastSnapshotUtc == DateTime.MinValue ||
                      (now - state.LastSnapshotUtc).TotalSeconds >= EditMinIntervalSeconds;
            }

            if (!due) return;

            state.LastHash = hash;
            state.LastSnapshotUtc = now;

            string sourcePath = HistoryRuntime.IsUserSqlFile(moniker) ? moniker : null;
            string kind = settings.Mode == AutoSaveMode.Interval ? "timer" : "edit";

            HistoryRuntime.RecordAutoSnapshot(sql, kind, connection?.Server, connection?.Database,
                sourcePath, moniker);
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

        private sealed class EditorState
        {
            public string LastHash;
            public DateTime LastSnapshotUtc = DateTime.MinValue;
        }
    }
}
