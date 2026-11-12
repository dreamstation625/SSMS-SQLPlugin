using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Storage;

namespace SsmsSqlPlugin.History
{
    /// <summary>
    /// 执行历史运行时：持有唯一的 StorageDatabase。
    ///
    /// 数据库在后台线程打开（SQLite 宿主加载 + 建表约百毫秒），
    /// 不拖慢包初始化；打开完成前发生的执行会静默跳过并写日志。
    /// </summary>
    internal static class HistoryRuntime
    {
        private static readonly object Gate = new object();
        private static StorageDatabase _database;
        private static bool _started;

        public static StorageDatabase Database
        {
            get { lock (Gate) { return _database; } }
        }

        public static bool IsAvailable
        {
            get { var db = Database; return db != null && db.IsAvailable; }
        }

        public static void Start()
        {
            lock (Gate)
            {
                if (_started) return;
                _started = true;
            }

            Task.Run(() =>
            {
                try
                {
                    var db = new StorageDatabase();

                    lock (Gate) { _database = db; }

                    if (!db.IsAvailable)
                    {
                        Log.Warn("[历史] 存储不可用，本次不做执行记录: " + db.Error);
                        return;
                    }

                    Log.Info("[历史] 存储就绪: " + db.DatabasePath);

                    var count = db.CountExecutionsAsync().GetAwaiter().GetResult();
                    var deleted = db.CountDeletedExecutionsAsync().GetAwaiter().GetResult();
                    Log.Info("[历史] 当前已存记录: " + count + " 回收站: " + deleted);

                    RunRetentionIfDue(db);
                }
                catch (Exception ex)
                {
                    Log.Error("[历史] 启动失败", ex);
                }
            });
        }

        private static void RunRetentionIfDue(StorageDatabase db)
        {
            try
            {
                if (!db.ShouldRunRetentionAsync(TimeSpan.FromHours(24)).GetAwaiter().GetResult())
                    return;

                var result = db.ApplyRetentionAsync(RetentionOptions.Default).GetAwaiter().GetResult();

                Log.Info("[历史] 回收完成: 执行-" + result.DeletedExecutions +
                         " 回收站-" + result.PurgedRecycleBin +
                         " 版本-" + result.DeletedRevisions +
                         " 内容-" + result.DeletedContents +
                         (result.Vacuumed ? " VACUUM" : "") +
                         " 体积=" + (result.DatabaseBytesAfter / 1024) + "KB");
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 回收失败: " + ex.Message);
            }
        }

        public static void Stop()
        {
            StorageDatabase db;
            lock (Gate)
            {
                db = _database;
                _database = null;
                _started = false;
            }

            try { db?.Dispose(); }
            catch (Exception ex) { Log.Warn("[历史] 关闭失败: " + ex.Message); }
        }

        /// <summary>
        /// 开始一条执行记录：先落库（拿到 Id），完成统计后由
        /// <see cref="StorageDatabase.UpdateExecutionAsync"/> 补写。
        /// 返回的 Task 失败只写日志，不向调用方抛。
        /// </summary>
        public static Task InsertAsync(ExecutionRecord record)
        {
            var db = Database;
            if (db == null || !db.IsAvailable) return Task.CompletedTask;

            return db.InsertExecutionAsync(record);
        }

        public static Task UpdateAsync(ExecutionRecord record)
        {
            var db = Database;
            if (db == null || !db.IsAvailable) return Task.CompletedTask;

            return db.UpdateExecutionAsync(record);
        }

        public static void RecordFileOpened(string fullPath, string serverName, string databaseName)
        {
            if (!IsUserSqlFile(fullPath)) return;

            var db = Database;
            if (db == null || !db.IsAvailable) return;

            db.RecordFileOpenedAsync(fullPath, serverName, databaseName).ContinueWith(t =>
            {
                var ex = t.Exception?.GetBaseException();
                if (ex != null) Log.Warn("[历史] 记录文件打开失败: " + ex.Message);
                else Log.Info("[历史] 文件打开: " + Path.GetFileName(fullPath));
            }, TaskScheduler.Default);
        }

        /// <summary>保存后抓快照。读取/哈希在后台线程，RDT 事件不被大文件拖住。</summary>
        public static void CaptureFileSnapshot(string fullPath, string kind)
        {
            if (!IsUserSqlFile(fullPath)) return;

            var db = Database;
            if (db == null || !db.IsAvailable) return;

            Task.Run(() =>
            {
                try
                {
                    var info = new FileInfo(fullPath);
                    if (!info.Exists) return;
                    if (info.Length > 32L * 1024 * 1024)
                    {
                        Log.Info("[历史] 文件过大，跳过快照: " + fullPath);
                        return;
                    }

                    string text = File.ReadAllText(fullPath);
                    long id = db.AddFileRevisionAsync(fullPath, text, kind, RetentionOptions.Default)
                                .GetAwaiter().GetResult();

                    if (id > 0)
                    {
                        Log.Info("[历史] 文件快照: " + Path.GetFileName(fullPath) +
                                 " 版本=" + id + " 大小=" + text.Length);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[历史] 文件快照失败 " + fullPath + ": " + ex.Message);
                }
            });
        }

        /// <summary>只收真实用户文件：SQL/执行计划/DDL，排除 %TEMP% 下的未保存查询。</summary>
        internal static bool IsUserSqlFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                if (path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return false;

                return path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".ddl", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>自动记录：不执行也落一条，Kind=edit/timer；路径已由调用方筛过。</summary>
        public static void RecordAutoSnapshot(string sqlText, string kind, string serverName,
                                              string databaseName, string sourcePath,
                                              string editorMoniker)
        {
            var db = Database;
            if (db == null || !db.IsAvailable) return;
            if (string.IsNullOrWhiteSpace(sqlText)) return;

            var record = new ExecutionRecord
            {
                StartedUtc = DateTime.UtcNow.ToString("o"),
                DurationMs = 0,
                ServerName = serverName,
                DatabaseName = databaseName,
                LoginName = null,
                RowsAffected = 0,
                Succeeded = -1,
                ErrorNumber = 0,
                ErrorMessage = null,
                SourcePath = sourcePath,
                EditorMoniker = editorMoniker,
                IsSelectionOnly = 0,
                Kind = kind,
                SqlText = sqlText,
            };

            Log.Info("[历史] 自动记录(" + (kind == "timer" ? "定时" : "修改") + "): " +
                     "长度=" + sqlText.Length +
                     " 服务器=" + (serverName ?? "?") +
                     " 库=" + (databaseName ?? "?") +
                     " 文件=" + (editorMoniker ?? "?"));

            db.InsertExecutionAsync(record).ContinueWith(t =>
            {
                var ex = t.Exception?.GetBaseException();
                if (ex != null) Log.Warn("[历史] 自动记录写入失败: " + ex.Message);
            }, TaskScheduler.Default);
        }

        public static string DatabasePath
        {
            get
            {
                var db = Database;
                if (db != null) return db.DatabasePath;

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SsmsSqlPlugin", "data.db");
            }
        }
    }
}
