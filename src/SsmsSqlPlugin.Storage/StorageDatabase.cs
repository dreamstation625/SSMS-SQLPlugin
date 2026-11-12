using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsSqlPlugin.Storage
{
    /// <summary>
    /// 执行历史/文件快照的 SQLite 存储。
    ///
    /// 所有数据库操作都在一个专用后台线程上执行（SQLite 单写者），
    /// UI 通过 Task 调用，不直接碰 SQLiteConnection。
    /// </summary>
    public sealed class StorageDatabase : IDisposable
    {
        private readonly SqliteHost _host;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly TaskCompletionSource<bool> _started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Thread _thread;
        private object _connection;
        private bool _disposed;

        public string DatabasePath { get; }
        public bool IsAvailable { get; private set; }
        public string Error { get; private set; }

        public StorageDatabase(string databasePath = null, string sqliteDirectory = null)
        {
            DatabasePath = databasePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SsmsSqlPlugin", "data.db");

            try
            {
                string dir = Path.GetDirectoryName(DatabasePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }

            _host = new SqliteHost(sqliteDirectory);
            if (!_host.IsReady)
            {
                Error = _host.Error;
                return;
            }

            _thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SsmsSqlPlugin.Storage",
            };
            _thread.Start();

            try
            {
                _started.Task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Error = "打开数据库失败: " + Describe(ex);
                try { _queue.CompleteAdding(); } catch { }
                return;
            }

            try
            {
                RunSync(InitializeSchema);
                IsAvailable = true;
            }
            catch (Exception ex)
            {
                Error = "建表/PRAGMA 失败: " + ex.Message + " / " + Describe(ex);
                try { _queue.CompleteAdding(); } catch { }
            }
        }

        private void WriterLoop()
        {
            try
            {
                _connection = _host.Open(DatabasePath);
                _started.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _started.TrySetException(ex);
                return;
            }

            try
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try { action(); } catch { }
                }
            }
            finally
            {
                if (_connection != null) _host.Close(_connection);
                _connection = null;
            }
        }

        private void InitializeSchema()
        {
            // WAL + 5 秒忙等；写入都在单线程上，不存在同进程写竞争
            try { _host.ExecuteScalar<string>(_connection, "PRAGMA journal_mode=WAL"); }
            catch (Exception ex) { throw new InvalidOperationException("PRAGMA journal_mode 失败", ex); }

            try { _host.Execute(_connection, "PRAGMA synchronous=NORMAL"); }
            catch (Exception ex) { throw new InvalidOperationException("PRAGMA synchronous 失败", ex); }

            // busy_timeout 会返回新值，必须用 ExecuteScalar；Execute 会报 "not an error"
            try { _host.ExecuteScalar<long>(_connection, "PRAGMA busy_timeout=5000"); }
            catch (Exception ex) { throw new InvalidOperationException("PRAGMA busy_timeout 失败", ex); }

            foreach (string sql in StorageSchema.Statements)
            {
                try
                {
                    _host.Execute(_connection, sql);
                }
                catch (Exception ex)
                {
                    string firstLine = sql.Split('\n')[0].Trim();
                    throw new InvalidOperationException("SQL 失败: " + firstLine, ex);
                }
            }

            EnsureExecutionDeletedColumn();
            EnsureExecutionKindColumn();
        }

        /// <summary>
        /// 老库升级：Executions 表补 DeletedUtc（软删除/回收站）。
        /// CREATE TABLE IF NOT EXISTS 不会给已存在的表加列，必须显式迁移。
        /// </summary>
        private void EnsureExecutionDeletedColumn()
        {
            long exists = _host.ExecuteScalar<long>(_connection,
                "SELECT COUNT(*) FROM pragma_table_info('Executions') WHERE name = 'DeletedUtc'");

            if (exists == 0)
            {
                _host.Execute(_connection, "ALTER TABLE Executions ADD COLUMN DeletedUtc TEXT");
            }

            _host.Execute(_connection,
                "CREATE INDEX IF NOT EXISTS IX_Executions_Deleted_Time ON Executions(DeletedUtc)");
        }

        private void EnsureExecutionKindColumn()
        {
            long exists = _host.ExecuteScalar<long>(_connection,
                "SELECT COUNT(*) FROM pragma_table_info('Executions') WHERE name = 'Kind'");

            if (exists == 0)
            {
                _host.Execute(_connection,
                    "ALTER TABLE Executions ADD COLUMN Kind TEXT NOT NULL DEFAULT 'execute'");
            }

            _host.Execute(_connection,
                "CREATE INDEX IF NOT EXISTS IX_Executions_Kind ON Executions(Kind)");
        }

        public Task InsertExecutionAsync(ExecutionRecord record)
        {
            if (record == null) return Task.CompletedTask;
            if (!IsAvailable) return Faulted("存储不可用: " + (Error ?? "unknown"));

            return Post(() =>
            {
                if (string.IsNullOrEmpty(record.StartedUtc))
                    record.StartedUtc = DateTime.UtcNow.ToString("o");

                byte[] contentId = EnsureContent(record.SqlText, 0, record.StartedUtc);
                record.ContentId = contentId;

                _host.Execute(_connection,
                    "INSERT INTO Executions (StartedUtc, DurationMs, ServerName, DatabaseName, LoginName, ContentId, RowsAffected, Succeeded, ErrorNumber, ErrorMessage, SourcePath, EditorMoniker, IsSelectionOnly, Kind) " +
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    record.StartedUtc, record.DurationMs, record.ServerName, record.DatabaseName,
                    record.LoginName, contentId, record.RowsAffected, record.Succeeded,
                    record.ErrorNumber, record.ErrorMessage, record.SourcePath,
                    record.EditorMoniker, record.IsSelectionOnly,
                    string.IsNullOrEmpty(record.Kind) ? "execute" : record.Kind);

                record.Id = _host.ExecuteScalar<long>(_connection, "SELECT last_insert_rowid()");
            });
        }

        /// <summary>
        /// 执行结束后补写耗时/统计。记录在开始时已插入，这里按 Id 更新；
        /// 统计未知时调用方保留 Succeeded=-1。
        /// </summary>
        public Task UpdateExecutionAsync(ExecutionRecord record)
        {
            if (record == null || record.Id <= 0) return Task.CompletedTask;
            if (!IsAvailable) return Task.CompletedTask;

            return Post(() => _host.Execute(_connection,
                "UPDATE Executions SET DurationMs = ?, Succeeded = ?, RowsAffected = ?, " +
                "ErrorNumber = ?, ErrorMessage = ? WHERE Id = ?",
                record.DurationMs, record.Succeeded, record.RowsAffected,
                record.ErrorNumber, record.ErrorMessage, record.Id));
        }

        public Task<long> CountExecutionsAsync()
        {
            if (!IsAvailable) return Task.FromResult(0L);
            return Post(() => _host.ExecuteScalar<long>(_connection,
                "SELECT COUNT(*) FROM Executions WHERE DeletedUtc IS NULL"));
        }

        public Task<long> CountDeletedExecutionsAsync()
        {
            if (!IsAvailable) return Task.FromResult(0L);
            return Post(() => _host.ExecuteScalar<long>(_connection,
                "SELECT COUNT(*) FROM Executions WHERE DeletedUtc IS NOT NULL"));
        }

        /// <summary>软删除：移到回收站。返回实际移动的条数。</summary>
        public Task<int> DeleteExecutionsAsync(IEnumerable<long> ids)
            => SetDeletedAsync(ids, true);

        /// <summary>从回收站恢复。返回实际恢复的条数。</summary>
        public Task<int> RestoreExecutionsAsync(IEnumerable<long> ids)
            => SetDeletedAsync(ids, false);

        private Task<int> SetDeletedAsync(IEnumerable<long> ids, bool deleted)
        {
            var unique = new HashSet<long>();
            if (ids != null)
            {
                foreach (long id in ids)
                    if (id > 0) unique.Add(id);
            }

            if (unique.Count == 0) return Task.FromResult(0);
            if (!IsAvailable) return Task.FromResult(0);

            return Post(() =>
            {
                int changed = 0;
                string now = DateTime.UtcNow.ToString("o");

                foreach (long id in unique)
                {
                    changed += deleted
                        ? _host.Execute(_connection,
                            "UPDATE Executions SET DeletedUtc = ? WHERE Id = ? AND DeletedUtc IS NULL",
                            now, id)
                        : _host.Execute(_connection,
                            "UPDATE Executions SET DeletedUtc = NULL WHERE Id = ? AND DeletedUtc IS NOT NULL",
                            id);
                }

                return changed;
            });
        }

        /// <summary>彻底删除回收站里超过保留期的记录，并清理孤立内容。</summary>
        public Task<int> PurgeDeletedExecutionsAsync(TimeSpan retention)
        {
            if (!IsAvailable) return Task.FromResult(0);
            if (retention < TimeSpan.Zero) retention = TimeSpan.Zero;

            return Post(() =>
            {
                string cutoff = DateTime.UtcNow.Subtract(retention).ToString("o");
                int removed = _host.Execute(_connection,
                    "DELETE FROM Executions WHERE DeletedUtc IS NOT NULL AND DeletedUtc < ?", cutoff);

                if (removed > 0) CleanupOrphanContentsLocked();
                return removed;
            });
        }

        public Task<long> CountContentsAsync()
        {
            if (!IsAvailable) return Task.FromResult(0L);
            return Post(() => _host.ExecuteScalar<long>(_connection, "SELECT COUNT(*) FROM Contents"));
        }

        public Task<string> GetJournalModeAsync()
        {
            if (!IsAvailable) return Task.FromResult<string>(null);
            return Post(() => _host.ExecuteScalar<string>(_connection, "PRAGMA journal_mode"));
        }

        public Task<List<ExecutionRecord>> GetRecentExecutionsAsync(int limit)
        {
            if (!IsAvailable) return Task.FromResult(new List<ExecutionRecord>());

            if (limit <= 0) limit = 100;

            return Post(() => _host.Query<ExecutionRecord>(_connection,
                ExecutionSelect + " WHERE e.DeletedUtc IS NULL ORDER BY e.Id DESC LIMIT ?", limit));
        }

        /// <summary>
        /// 历史窗口搜索。SQL 文本用 instr 子串匹配（不做 FTS，10 万条仍在要求内），
        /// 服务器/库/时间为精确/范围过滤。
        /// </summary>
        public Task<List<ExecutionRecord>> SearchExecutionsAsync(ExecutionSearch query)
        {
            if (!IsAvailable) return Task.FromResult(new List<ExecutionRecord>());

            if (query == null) query = new ExecutionSearch();
            int limit = query.Limit <= 0 ? 2000 : query.Limit;

            return Post(() =>
            {
                var where = new List<string>();
                var args = new List<object>();

                if (!string.IsNullOrWhiteSpace(query.Text))
                {
                    where.Add("instr(lower(CAST(c.SqlBytes AS TEXT)), lower(?)) > 0");
                    args.Add(query.Text.Trim());
                }

                if (!string.IsNullOrEmpty(query.ServerName))
                {
                    where.Add("e.ServerName = ?");
                    args.Add(query.ServerName);
                }

                if (!string.IsNullOrEmpty(query.DatabaseName))
                {
                    where.Add("e.DatabaseName = ?");
                    args.Add(query.DatabaseName);
                }

                if (query.FromUtc.HasValue)
                {
                    where.Add("e.StartedUtc >= ?");
                    args.Add(query.FromUtc.Value.ToUniversalTime().ToString("o"));
                }

                if (query.Deleted == ExecutionDeletedFilter.Active)
                {
                    where.Add("e.DeletedUtc IS NULL");
                }
                else if (query.Deleted == ExecutionDeletedFilter.Deleted)
                {
                    where.Add("e.DeletedUtc IS NOT NULL");
                }

                if (query.Kind == ExecutionKindFilter.Execute)
                {
                    where.Add("e.Kind = 'execute'");
                }
                else if (query.Kind == ExecutionKindFilter.Auto)
                {
                    where.Add("e.Kind IS NOT NULL AND e.Kind <> 'execute'");
                }

                string sql = ExecutionSelect +
                             (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                             " ORDER BY e.Id DESC LIMIT ?";
                args.Add(limit);

                return _host.Query<ExecutionRecord>(_connection, sql, args.ToArray());
            });
        }

        public Task<List<string>> GetDistinctServersAsync()
        {
            if (!IsAvailable) return Task.FromResult(new List<string>());

            return Post(() =>
            {
                var rows = _host.Query<ExecutionRecord>(_connection,
                    "SELECT DISTINCT ServerName AS ServerName FROM Executions " +
                    "WHERE ServerName IS NOT NULL AND ServerName <> '' ORDER BY ServerName");

                var list = new List<string>();
                foreach (var row in rows) list.Add(row.ServerName);
                return list;
            });
        }

        public Task<List<string>> GetDistinctDatabasesAsync(string serverName)
        {
            if (!IsAvailable) return Task.FromResult(new List<string>());

            return Post(() =>
            {
                var rows = _host.Query<ExecutionRecord>(_connection,
                    "SELECT DISTINCT DatabaseName AS DatabaseName FROM Executions " +
                    "WHERE DatabaseName IS NOT NULL AND DatabaseName <> '' " +
                    (string.IsNullOrEmpty(serverName) ? "" : "AND ServerName = ? ") +
                    "ORDER BY DatabaseName",
                    string.IsNullOrEmpty(serverName) ? new object[0] : new object[] { serverName });

                var list = new List<string>();
                foreach (var row in rows) list.Add(row.DatabaseName);
                return list;
            });
        }

        private const string ExecutionSelect =
            "SELECT e.Id, e.StartedUtc, e.DurationMs, e.ServerName, e.DatabaseName, e.LoginName, " +
            "e.RowsAffected, e.Succeeded, e.ErrorNumber, e.ErrorMessage, e.SourcePath, " +
            "e.EditorMoniker, e.IsSelectionOnly, e.DeletedUtc, e.Kind, CAST(c.SqlBytes AS TEXT) AS SqlText " +
            "FROM Executions e JOIN Contents c ON c.ContentId = e.ContentId";

        // ── 文件清单 / 快照 ──

        public Task<long> RecordFileOpenedAsync(string fullPath, string serverName, string databaseName)
        {
            if (string.IsNullOrEmpty(fullPath)) return Task.FromResult(0L);
            if (!IsAvailable) return Task.FromResult(0L);

            return Post(() =>
            {
                string now = DateTime.UtcNow.ToString("o");
                long fileId = EnsureFileLocked(fullPath, now);

                // RDT 对同一文件的打开可能连发多次（读锁/编辑锁各一次），
                // 10 秒内只算一次打开，避免 OpenCount 虚高。
                string lastOpened = _host.ExecuteScalar<string>(_connection,
                    "SELECT LastOpenedUtc FROM Files WHERE Id = ?", fileId);

                bool isNewOpen = true;
                if (!string.IsNullOrEmpty(lastOpened) &&
                    DateTime.TryParse(lastOpened, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastTime) &&
                    (DateTime.UtcNow - lastTime.ToUniversalTime()).TotalSeconds < 10)
                {
                    isNewOpen = false;
                }

                if (isNewOpen)
                {
                    _host.Execute(_connection,
                        "UPDATE Files SET LastOpenedUtc = ?, OpenCount = OpenCount + 1 WHERE Id = ?",
                        now, fileId);
                }
                else
                {
                    _host.Execute(_connection,
                        "UPDATE Files SET LastOpenedUtc = ? WHERE Id = ?", now, fileId);
                }

                long contextId = _host.ExecuteScalar<long>(_connection,
                    "SELECT Id FROM FileContexts WHERE FileId = ? AND IFNULL(ServerName, '') = ? AND IFNULL(DatabaseName, '') = ? LIMIT 1",
                    fileId, serverName ?? "", databaseName ?? "");

                if (contextId > 0)
                {
                    _host.Execute(_connection,
                        "UPDATE FileContexts SET LastUsedUtc = ? WHERE Id = ?", now, contextId);
                }
                else
                {
                    _host.Execute(_connection,
                        "INSERT INTO FileContexts (FileId, ServerName, DatabaseName, LastUsedUtc) VALUES (?, ?, ?, ?)",
                        fileId, serverName, databaseName, now);
                }

                return fileId;
            });
        }

        /// <summary>
        /// 保存时抓一份快照：连续相同内容、或低于最小间隔的自动快照会被跳过；
        /// 每个文件最多保留 maxRevisions 个版本。
        /// 返回新版本 Id，跳过时返回 0。
        /// </summary>
        public Task<long> AddFileRevisionAsync(string fullPath, string sqlText, string kind,
                                               RetentionOptions options = null)
        {
            if (string.IsNullOrEmpty(fullPath)) return Task.FromResult(0L);
            if (!IsAvailable) return Task.FromResult(0L);

            options = options ?? RetentionOptions.Default;

            return Post(() =>
            {
                var now = DateTime.UtcNow;
                string nowText = now.ToString("o");

                long fileId = EnsureFileLocked(fullPath, nowText);
                byte[] contentId = EnsureContent(sqlText, options.MaxSnapshotBytes, nowText);

                var last = _host.Query<FileRevisionRecord>(_connection,
                    "SELECT Id, ContentId, CapturedUtc FROM FileRevisions WHERE FileId = ? ORDER BY Id DESC LIMIT 1",
                    fileId);

                if (last.Count > 0)
                {
                    if (SameBytes(last[0].ContentId, contentId)) return 0L;

                    if (options.SnapshotMinIntervalSeconds > 0 &&
                        DateTime.TryParse(last[0].CapturedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastTime) &&
                        (now - lastTime.ToUniversalTime()).TotalSeconds < options.SnapshotMinIntervalSeconds)
                    {
                        return 0L;
                    }
                }

                _host.Execute(_connection,
                    "INSERT INTO FileRevisions (FileId, ContentId, CapturedUtc, Kind) VALUES (?, ?, ?, ?)",
                    fileId, contentId, nowText, kind);

                long revisionId = _host.ExecuteScalar<long>(_connection, "SELECT last_insert_rowid()");

                _host.Execute(_connection,
                    "UPDATE Files SET LastSavedUtc = ? WHERE Id = ?", nowText, fileId);

                TrimRevisionsLocked(fileId, options.MaxRevisionsPerFile);

                return revisionId;
            });
        }

        public Task<List<FileRevisionRecord>> GetFileRevisionsAsync(string fullPath, int limit)
        {
            if (string.IsNullOrEmpty(fullPath)) return Task.FromResult(new List<FileRevisionRecord>());
            if (!IsAvailable) return Task.FromResult(new List<FileRevisionRecord>());

            if (limit <= 0) limit = 30;

            return Post(() => _host.Query<FileRevisionRecord>(_connection,
                "SELECT r.Id, r.FileId, r.ContentId, r.CapturedUtc, r.Kind, " +
                "CAST(c.SqlBytes AS TEXT) AS SqlText, IFNULL(c.Length, 0) AS SqlLength " +
                "FROM FileRevisions r JOIN Files f ON f.Id = r.FileId " +
                "LEFT JOIN Contents c ON c.ContentId = r.ContentId " +
                "WHERE f.FullPath = ? ORDER BY r.Id DESC LIMIT ?", fullPath, limit));
        }

        public Task<List<FileRecord>> GetRecentFilesAsync(int limit)
        {
            if (!IsAvailable) return Task.FromResult(new List<FileRecord>());
            if (limit <= 0) limit = 100;

            return Post(() => _host.Query<FileRecord>(_connection,
                "SELECT Id, FullPath, FirstSeenUtc, LastOpenedUtc, LastSavedUtc, OpenCount " +
                "FROM Files ORDER BY IFNULL(LastOpenedUtc, FirstSeenUtc) DESC LIMIT ?", limit));
        }

        private long EnsureFileLocked(string fullPath, string nowUtc)
        {
            _host.Execute(_connection,
                "INSERT OR IGNORE INTO Files (FullPath, FirstSeenUtc, OpenCount) VALUES (?, ?, 0)",
                fullPath, nowUtc);

            return _host.ExecuteScalar<long>(_connection,
                "SELECT Id FROM Files WHERE FullPath = ?", fullPath);
        }

        private void TrimRevisionsLocked(long fileId, int maxRevisions)
        {
            if (maxRevisions <= 0) return;

            _host.Execute(_connection,
                "DELETE FROM FileRevisions WHERE FileId = ? AND Id NOT IN " +
                "(SELECT Id FROM FileRevisions WHERE FileId = ? ORDER BY Id DESC LIMIT ?)",
                fileId, fileId, maxRevisions);
        }

        // ── 回收 ──

        public Task<bool> ShouldRunRetentionAsync(TimeSpan minInterval)
        {
            if (!IsAvailable) return Task.FromResult(false);

            return Post(() =>
            {
                string last = _host.ExecuteScalar<string>(_connection,
                    "SELECT Value FROM Meta WHERE Key = 'LastRetentionUtc'");

                if (string.IsNullOrEmpty(last)) return true;
                if (!DateTime.TryParse(last, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastTime))
                    return true;

                return DateTime.UtcNow - lastTime.ToUniversalTime() >= minInterval;
            });
        }

        public Task<RetentionResult> ApplyRetentionAsync(RetentionOptions options = null)
        {
            if (!IsAvailable) return Task.FromResult(new RetentionResult());

            options = options ?? RetentionOptions.Default;

            return Post(() =>
            {
                var result = new RetentionResult { RunUtc = DateTime.UtcNow };
                string cutoff = result.RunUtc.AddDays(-Math.Max(1, options.ExecutionDays)).ToString("o");

                // 回收站只保留 RecycleBinDays 天，到期彻底删除
                string recycleCutoff = result.RunUtc
                    .AddDays(-Math.Max(1, options.RecycleBinDays)).ToString("o");

                result.PurgedRecycleBin = _host.Execute(_connection,
                    "DELETE FROM Executions WHERE DeletedUtc IS NOT NULL AND DeletedUtc < ?",
                    recycleCutoff);

                result.DeletedExecutions = _host.Execute(_connection,
                    "DELETE FROM Executions WHERE StartedUtc < ?", cutoff);

                // 旧快照只清历史版本，每个文件至少保留最新一份；
                // Files 行永远不删，文件失效后清单仍在。
                result.DeletedRevisions = _host.Execute(_connection,
                    "DELETE FROM FileRevisions WHERE CapturedUtc < ? AND Id NOT IN " +
                    "(SELECT MAX(Id) FROM FileRevisions GROUP BY FileId)", cutoff);

                result.DeletedContents = _host.Execute(_connection,
                    "DELETE FROM Contents WHERE LastSeenUtc < ? " +
                    "AND ContentId NOT IN (SELECT ContentId FROM Executions) " +
                    "AND ContentId NOT IN (SELECT ContentId FROM FileRevisions) " +
                    "AND ContentId NOT IN (SELECT ContentId FROM Favorites WHERE ContentId IS NOT NULL)",
                    cutoff);

                long maxBytes = options.MaxDatabaseBytes > 0
                    ? options.MaxDatabaseBytes
                    : 200L * 1024 * 1024;

                int protect = Math.Max(1, options.ProtectRecentExecutions);

                for (int i = 0; i < 10 && GetLiveDatabaseBytes() > maxBytes; i++)
                {
                    int removed = _host.Execute(_connection,
                        "DELETE FROM Executions WHERE Id IN " +
                        "(SELECT Id FROM Executions WHERE Id NOT IN " +
                        "(SELECT Id FROM Executions ORDER BY Id DESC LIMIT ?) " +
                        "ORDER BY Id ASC LIMIT 5000)", protect);

                    if (removed <= 0) break;
                    result.DeletedExecutions += removed;

                    result.DeletedContents += CleanupOrphanContentsLocked();

                    try { _host.ExecuteScalar<long>(_connection, "PRAGMA wal_checkpoint(TRUNCATE)"); }
                    catch { }
                }

                bool vacuum = false;
                try
                {
                    long free = _host.ExecuteScalar<long>(_connection, "PRAGMA freelist_count");
                    long pages = _host.ExecuteScalar<long>(_connection, "PRAGMA page_count");

                    if (free > 256 && (pages <= 0 || free * 100 / pages > 20)) vacuum = true;
                    if (GetDatabaseBytes() > maxBytes) vacuum = true;
                }
                catch { }

                if (vacuum)
                {
                    try
                    {
                        _host.Execute(_connection, "VACUUM");
                        result.Vacuumed = true;

                        // WAL 模式下 VACUUM 的结果先写进 -wal，必须再截断一次才算真正缩小。
                        _host.ExecuteScalar<long>(_connection, "PRAGMA wal_checkpoint(TRUNCATE)");
                    }
                    catch { }
                }

                _host.Execute(_connection,
                    "INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('LastRetentionUtc', ?)",
                    result.RunUtc.ToString("o"));

                result.DatabaseBytesAfter = GetDatabaseBytes();
                return result;
            });
        }

        private byte[] EnsureContent(string sqlText, long maxBytes, string nowUtc)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(sqlText ?? string.Empty);
            byte[] contentId = Sha256(bytes);

            bool oversize = maxBytes > 0 && bytes.Length > maxBytes;
            byte[] stored = oversize ? new byte[0] : bytes;

            _host.Execute(_connection,
                "INSERT OR IGNORE INTO Contents (ContentId, SqlBytes, Length, LastSeenUtc) VALUES (?, ?, ?, ?)",
                contentId, stored, bytes.Length, nowUtc);

            _host.Execute(_connection,
                "UPDATE Contents SET LastSeenUtc = ? WHERE ContentId = ?", nowUtc, contentId);

            if (!oversize)
            {
                // 之前可能以「只存哈希」的空内容占位，这里补成完整内容。
                _host.Execute(_connection,
                    "UPDATE Contents SET SqlBytes = ?, Length = ? WHERE ContentId = ? AND length(SqlBytes) < ?",
                    bytes, bytes.Length, contentId, bytes.Length);
            }

            return contentId;
        }

        private int CleanupOrphanContentsLocked()
        {
            return _host.Execute(_connection,
                "DELETE FROM Contents WHERE ContentId NOT IN (SELECT ContentId FROM Executions) " +
                "AND ContentId NOT IN (SELECT ContentId FROM FileRevisions) " +
                "AND ContentId NOT IN (SELECT ContentId FROM Favorites WHERE ContentId IS NOT NULL)");
        }

        private long GetLiveDatabaseBytes()
        {
            try
            {
                long pageSize = _host.ExecuteScalar<long>(_connection, "PRAGMA page_size");
                long pageCount = _host.ExecuteScalar<long>(_connection, "PRAGMA page_count");
                long free = _host.ExecuteScalar<long>(_connection, "PRAGMA freelist_count");

                return Math.Max(0, pageCount - free) * pageSize;
            }
            catch
            {
                return GetDatabaseBytes();
            }
        }

        private long GetDatabaseBytes()
        {
            long total = 0;

            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var info = new FileInfo(DatabasePath + suffix);
                    if (info.Exists) total += info.Length;
                }
                catch { }
            }

            return total;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;

            return true;
        }

        // ── 单写线程队列 ──

        private void RunSync(Action action)
        {
            Enqueue(action).GetAwaiter().GetResult();
        }

        private Task Post(Action action)
        {
            if (!IsAvailable) return Faulted("存储不可用: " + (Error ?? "unknown"));
            return Enqueue(action);
        }

        private Task<T> Post<T>(Func<T> func)
        {
            if (!IsAvailable) return Faulted<T>("存储不可用: " + (Error ?? "unknown"));
            return Enqueue(func);
        }

        private Task Enqueue(Action action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _queue.Add(() =>
                {
                    try { action(); tcs.TrySetResult(true); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        private Task<T> Enqueue<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _queue.Add(() =>
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        private static Task Faulted(string message)
        {
            var tcs = new TaskCompletionSource<bool>();
            tcs.TrySetException(new InvalidOperationException(message));
            return tcs.Task;
        }

        private static Task<T> Faulted<T>(string message)
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.TrySetException(new InvalidOperationException(message));
            return tcs.Task;
        }

        private static byte[] Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(bytes);
            }
        }

        private static string Describe(Exception ex)
        {
            var cur = ex;
            while (cur.InnerException != null) cur = cur.InnerException;
            return cur.Message;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _queue.CompleteAdding(); } catch { }
            try { if (_thread != null && _thread.IsAlive) _thread.Join(3000); } catch { }
            try { _queue.Dispose(); } catch { }
            IsAvailable = false;
        }
    }
}
