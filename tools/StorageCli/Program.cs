using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SsmsSqlPlugin.Storage;

// P3 S3.1 存储回归：
//   dotnet run --project tools/StorageCli
//   dotnet run --project tools/StorageCli -- --crash-test
class Program
{
    private const string DefaultSqliteDir =
        @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\PublicAssemblies\SQLite";

    static int Main(string[] args)
    {
        string sqliteDir = Arg(args, "--sqlite-dir") ?? DefaultSqliteDir;
        string db = Arg(args, "--db") ?? Path.Combine(
            Path.GetTempPath(), "SsmsSqlPlugin", "storage-test.db");

        if (Has(args, "--crash-writer")) return RunWriter(db, sqliteDir);
        if (Has(args, "--crash-test")) return RunCrash(db, sqliteDir);
        if (Has(args, "--search-test")) return RunSearchTest(db, sqliteDir, Arg(args, "--count"));
        if (Has(args, "--files-test")) return RunFilesTest(db, sqliteDir);
        if (Has(args, "--retention-test")) return RunRetentionTest(db, sqliteDir);
        if (Has(args, "--recycle-test")) return RunRecycleTest(db, sqliteDir);
        if (Has(args, "--auto-test")) return RunAutoRecordTest(db, sqliteDir);
        if (Has(args, "--inspect")) return RunInspect(Arg(args, "--db") ?? UserDbPath(), sqliteDir);

        return RunBasic(db, sqliteDir);
    }

    private static int RunBasic(string db, string sqliteDir)
    {
        ResetDb(db);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                var record = NewRecord(i, "SELECT " + i + " AS n");
                storage.InsertExecutionAsync(record).GetAwaiter().GetResult();
            }
            sw.Stop();

            long executions = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long contents = storage.CountContentsAsync().GetAwaiter().GetResult();
            string journal = storage.GetJournalModeAsync().GetAwaiter().GetResult();

            // 相同 SQL 再去重一次：执行记录 +1，内容不增加
            storage.InsertExecutionAsync(NewRecord(999, "SELECT 999 AS n"))
                .GetAwaiter().GetResult();

            long executions2 = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long contents2 = storage.CountContentsAsync().GetAwaiter().GetResult();

            var recent = storage.GetRecentExecutionsAsync(3).GetAwaiter().GetResult();

            // 执行结束补写统计：开始时 Succeeded=-1，结束后按 Id 更新
            var updated = NewRecord(4242, "SELECT 4242 AS n");
            storage.InsertExecutionAsync(updated).GetAwaiter().GetResult();
            updated.DurationMs = 321;
            updated.Succeeded = 1;
            updated.RowsAffected = 7;
            storage.UpdateExecutionAsync(updated).GetAwaiter().GetResult();

            var after = storage.GetRecentExecutionsAsync(1).GetAwaiter().GetResult();
            bool updateOk = after.Count == 1 &&
                            after[0].Id == updated.Id &&
                            after[0].DurationMs == 321 &&
                            after[0].Succeeded == 1 &&
                            after[0].RowsAffected == 7;

            Console.WriteLine("数据库: " + storage.DatabasePath);
            Console.WriteLine("journal_mode: " + journal);
            Console.WriteLine("1000 次写入: " + sw.ElapsedMilliseconds + "ms");
            Console.WriteLine("Executions=" + executions + " Contents=" + contents);
            Console.WriteLine("重复 SQL 后: Executions=" + executions2 + " Contents=" + contents2);
            Console.WriteLine("最近记录数: " + recent.Count +
                              (recent.Count > 0 ? " first=" + recent[0].SqlText : ""));
            Console.WriteLine("结束补写: " + (updateOk ? "PASS" : "FAIL") +
                              (after.Count > 0
                                  ? " duration=" + after[0].DurationMs +
                                    " succeeded=" + after[0].Succeeded +
                                    " rows=" + after[0].RowsAffected
                                  : ""));

            bool ok = executions == 1000 &&
                      contents == 1000 &&
                      executions2 == 1001 &&
                      contents2 == 1000 &&
                      string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase) &&
                      updateOk;

            TryPrintSizes(db);
            Console.WriteLine(ok ? "[PASS] S3.1 基本存储回归" : "[FAIL] S3.1 基本存储回归");
            return ok ? 0 : 1;
        }
    }

    private static int RunWriter(string db, string sqliteDir)
    {
        var storage = new StorageDatabase(db, sqliteDir);
        if (!storage.IsAvailable)
        {
            Console.WriteLine("[FAIL] writer 存储不可用: " + storage.Error);
            return 1;
        }

        Console.WriteLine("WRITER_READY");
        Console.Out.Flush();

        for (int i = 0; ; i++)
        {
            storage.InsertExecutionAsync(NewRecord(i, "SELECT " + i + " AS n"))
                .GetAwaiter().GetResult();
            Thread.Sleep(10);
        }
    }

    private static int RunCrash(string db, string sqliteDir)
    {
        ResetDb(db);

        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--crash-writer --db \"" + db + "\" --sqlite-dir \"" + sqliteDir + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        Process child = Process.Start(psi);
        if (child == null)
        {
            Console.WriteLine("[FAIL] 无法启动 writer 子进程");
            return 1;
        }

        string line = null;
        while ((line = child.StandardOutput.ReadLine()) != null)
        {
            if (line == "WRITER_READY") break;
            Console.WriteLine("child: " + line);
        }

        if (line != "WRITER_READY")
        {
            Console.WriteLine("[FAIL] writer 未就绪");
            try { child.Kill(); } catch { }
            return 1;
        }

        Thread.Sleep(800);
        try { child.Kill(); } catch { }
        child.WaitForExit(3000);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 恢复存储不可用: " + storage.Error);
                return 1;
            }

            long count = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            Console.WriteLine("杀进程后恢复记录数: " + count);
            Console.WriteLine(count > 0 ? "[PASS] 杀进程后数据可恢复" : "[FAIL] 恢复后无记录");
            return count > 0 ? 0 : 1;
        }
    }

    private static int RunSearchTest(string db, string sqliteDir, string countArg)
    {
        int count = 100000;
        if (!string.IsNullOrEmpty(countArg)) int.TryParse(countArg, out count);
        if (count <= 0) count = 100000;

        ResetDb(db);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                var record = NewRecord(i, "SELECT " + i + " AS n, 'token_" + (i % 1000) + "' AS tag");
                storage.InsertExecutionAsync(record).GetAwaiter().GetResult();
            }
            sw.Stop();
            Console.WriteLine("写入 " + count + " 条: " + sw.ElapsedMilliseconds + "ms");

            var query = new ExecutionSearch { Text = "token_777", Limit = 2000 };
            sw.Restart();
            var hits = storage.SearchExecutionsAsync(query).GetAwaiter().GetResult();
            sw.Stop();
            long searchMs = sw.ElapsedMilliseconds;
            Console.WriteLine("搜索 token_777: " + hits.Count + " 条, " + searchMs + "ms");

            var miss = new ExecutionSearch { Text = "zzz_not_found_token", Limit = 2000 };
            sw.Restart();
            var none = storage.SearchExecutionsAsync(miss).GetAwaiter().GetResult();
            sw.Stop();
            long missMs = sw.ElapsedMilliseconds;
            Console.WriteLine("搜索无命中: " + none.Count + " 条, " + missMs + "ms");

            var servers = storage.GetDistinctServersAsync().GetAwaiter().GetResult();
            var dbs = storage.GetDistinctDatabasesAsync(null).GetAwaiter().GetResult();
            Console.WriteLine("服务器=" + servers.Count + " 库=" + dbs.Count);

            bool ok = hits.Count == count / 1000 &&
                      searchMs < 300 &&
                      missMs < 300;
            Console.WriteLine(ok ? "[PASS] S3.3 搜索性能" : "[FAIL] S3.3 搜索性能");
            return ok ? 0 : 1;
        }
    }

    private static int RunFilesTest(string db, string sqliteDir)
    {
        ResetDb(db);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            var options = new RetentionOptions
            {
                MaxRevisionsPerFile = 30,
                SnapshotMinIntervalSeconds = 0,
                MaxSnapshotBytes = 1024,
            };

            string path = @"C:\history-test\query.sql";
            for (int i = 0; i < 3; i++)
                storage.RecordFileOpenedAsync(path, "localhost", "db1").GetAwaiter().GetResult();

            // 20 次保存：内容各不相同
            for (int i = 0; i < 20; i++)
            {
                storage.AddFileRevisionAsync(path, "SELECT " + i + " AS n", "save", options)
                    .GetAwaiter().GetResult();
            }

            var after20 = storage.GetFileRevisionsAsync(path, 100).GetAwaiter().GetResult();

            // 与上一版相同：应去重
            storage.AddFileRevisionAsync(path, "SELECT 19 AS n", "save", options)
                .GetAwaiter().GetResult();
            var afterDup = storage.GetFileRevisionsAsync(path, 100).GetAwaiter().GetResult();

            // 超过 1KB：只存哈希，SqlText 为空但长度保留
            string big = new string('x', 2000);
            storage.AddFileRevisionAsync(path, big, "save", options).GetAwaiter().GetResult();
            var afterBig = storage.GetFileRevisionsAsync(path, 100).GetAwaiter().GetResult();
            var bigRev = afterBig[0];

            var files = storage.GetRecentFilesAsync(10).GetAwaiter().GetResult();

            // 失效文件：只记录打开/保存，不要求磁盘上真的存在
            string missing = @"C:\history-test\deleted.sql";
            storage.RecordFileOpenedAsync(missing, null, null).GetAwaiter().GetResult();
            storage.AddFileRevisionAsync(missing, "SELECT 404", "save", options).GetAwaiter().GetResult();

            var retention = storage.ApplyRetentionAsync(new RetentionOptions
            {
                ExecutionDays = 90,
                MaxDatabaseBytes = 50L * 1024 * 1024,
            }).GetAwaiter().GetResult();

            var missingRevs = storage.GetFileRevisionsAsync(missing, 10).GetAwaiter().GetResult();
            var filesAfter = storage.GetRecentFilesAsync(10).GetAwaiter().GetResult();

            Console.WriteLine("打开 3 次后文件数: " + files.Count +
                              " OpenCount=" + (files.Count > 0 ? files[0].OpenCount : -1));
            Console.WriteLine("保存 20 次后的版本数: " + after20.Count);
            Console.WriteLine("重复内容后版本数: " + afterDup.Count);
            Console.WriteLine("超限版本: SqlText长度=" + (bigRev.SqlText?.Length ?? -1) +
                              " SqlLength=" + bigRev.SqlLength);
            Console.WriteLine("回收后失效文件版本数: " + missingRevs.Count +
                              " 文件记录数: " + filesAfter.Count);

            bool ok = files.Count == 1 &&
                      files[0].OpenCount == 1 &&   // RDT 连发多次，10 秒内只算一次打开
                      after20.Count == 20 &&
                      afterDup.Count == 20 &&
                      bigRev.SqlText != null && bigRev.SqlText.Length == 0 &&
                      bigRev.SqlLength == 2000 &&
                      missingRevs.Count == 1 &&
                      filesAfter.Count == 2;

            Console.WriteLine(ok ? "[PASS] S3.4 文件清单/快照" : "[FAIL] S3.4 文件清单/快照");
            return ok ? 0 : 1;
        }
    }

    private static int RunRetentionTest(string db, string sqliteDir)
    {
        ResetDb(db);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            var now = DateTime.UtcNow;
            for (int i = 0; i < 2500; i++)
            {
                var record = NewRecord(i, "SELECT " + i + " AS n, '" + new string('y', 300) + "' AS pad");
                record.StartedUtc = now.AddDays(-100).ToString("o");
                storage.InsertExecutionAsync(record).GetAwaiter().GetResult();
            }

            for (int i = 0; i < 2500; i++)
            {
                storage.InsertExecutionAsync(NewRecord(i, "SELECT " + i + " AS n, '" + new string('z', 300) + "' AS pad"))
                    .GetAwaiter().GetResult();
            }

            string path = @"C:\history-test\retention.sql";
            var options = new RetentionOptions
            {
                MaxRevisionsPerFile = 5,
                SnapshotMinIntervalSeconds = 0,
                MaxSnapshotBytes = 1024 * 1024,
            };

            for (int i = 0; i < 10; i++)
                storage.AddFileRevisionAsync(path, "SELECT " + i, "save", options).GetAwaiter().GetResult();

            var revisionsBefore = storage.GetFileRevisionsAsync(path, 100).GetAwaiter().GetResult();

            long beforeCount = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long beforeSize = new FileInfo(storage.DatabasePath).Length;

            // 第一次：只按时间淘汰
            var timeResult = storage.ApplyRetentionAsync(new RetentionOptions
            {
                ExecutionDays = 90,
                MaxDatabaseBytes = 50L * 1024 * 1024,
                MaxRevisionsPerFile = 5,
            }).GetAwaiter().GetResult();

            long afterTime = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            var newest = storage.GetRecentExecutionsAsync(1).GetAwaiter().GetResult();

            // 第二次：强制体积上限，验证淘汰最旧、不影响最新
            var sizeResult = storage.ApplyRetentionAsync(new RetentionOptions
            {
                ExecutionDays = 90,
                MaxDatabaseBytes = 200L * 1024,
                MaxRevisionsPerFile = 5,
            }).GetAwaiter().GetResult();

            long afterSize = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            var newestAfter = storage.GetRecentExecutionsAsync(1).GetAwaiter().GetResult();
            var revisionsAfter = storage.GetFileRevisionsAsync(path, 100).GetAwaiter().GetResult();

            Console.WriteLine("写入记录: " + beforeCount + " 体积=" + (beforeSize / 1024) + "KB");
            Console.WriteLine("时间回收: 删除=" + timeResult.DeletedExecutions +
                              " 剩余=" + afterTime +
                              " 内容删除=" + timeResult.DeletedContents);
            Console.WriteLine("体积回收: 删除=" + sizeResult.DeletedExecutions +
                              " 剩余=" + afterSize +
                              " VACUUM=" + sizeResult.Vacuumed +
                              " 体积=" + (sizeResult.DatabaseBytesAfter / 1024) + "KB");
            Console.WriteLine("版本数: " + revisionsBefore.Count + " -> " + revisionsAfter.Count);
            Console.WriteLine("最新记录保留: " + (newestAfter.Count == 1 && newestAfter[0].Id == newest[0].Id));

            bool ok = timeResult.DeletedExecutions == 2500 &&
                      afterTime == 2500 &&
                      newest.Count == 1 &&
                      afterSize > 0 && afterSize <= 50 &&
                      newestAfter.Count == 1 &&
                      newestAfter[0].Id == newest[0].Id &&
                      revisionsBefore.Count == 5 &&
                      revisionsAfter.Count == 5 &&
                      sizeResult.DatabaseBytesAfter <= 200L * 1024 + 64 * 1024;

            Console.WriteLine(ok ? "[PASS] S3.5 回收策略" : "[FAIL] S3.5 回收策略");
            return ok ? 0 : 1;
        }
    }

    private static int RunRecycleTest(string db, string sqliteDir)
    {
        ResetDb(db);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            var records = new List<ExecutionRecord>();
            for (int i = 0; i < 10; i++)
            {
                var record = NewRecord(i, "SELECT " + i + " AS recycle_probe");
                storage.InsertExecutionAsync(record).GetAwaiter().GetResult();
                records.Add(record);
            }

            long active0 = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long deleted0 = storage.CountDeletedExecutionsAsync().GetAwaiter().GetResult();

            var first3 = new[] { records[0].Id, records[1].Id, records[2].Id };
            int moved = storage.DeleteExecutionsAsync(first3).GetAwaiter().GetResult();

            long active1 = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long deleted1 = storage.CountDeletedExecutionsAsync().GetAwaiter().GetResult();

            var deletedRows = storage.SearchExecutionsAsync(new ExecutionSearch
            {
                Deleted = ExecutionDeletedFilter.Deleted,
                Limit = 100,
            }).GetAwaiter().GetResult();

            bool marked = deletedRows.Count == 3;
            foreach (var row in deletedRows)
                if (string.IsNullOrEmpty(row.DeletedUtc)) marked = false;

            int restored = storage.RestoreExecutionsAsync(new[] { records[0].Id, records[1].Id })
                .GetAwaiter().GetResult();

            long active2 = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long deleted2 = storage.CountDeletedExecutionsAsync().GetAwaiter().GetResult();

            // 再删回去，凑 3 条回收站记录
            storage.DeleteExecutionsAsync(new[] { records[0].Id, records[1].Id })
                .GetAwaiter().GetResult();

            // 30 天保留期内不能清
            var retention = storage.ApplyRetentionAsync(new RetentionOptions
            {
                ExecutionDays = 90,
                RecycleBinDays = 30,
                MaxDatabaseBytes = 50L * 1024 * 1024,
            }).GetAwaiter().GetResult();

            long deletedAfterRetention = storage.CountDeletedExecutionsAsync().GetAwaiter().GetResult();

            // 强制到期：彻底删除并清孤立 Contents
            int purged = storage.PurgeDeletedExecutionsAsync(TimeSpan.Zero).GetAwaiter().GetResult();

            long activeFinal = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long deletedFinal = storage.CountDeletedExecutionsAsync().GetAwaiter().GetResult();
            long contentsFinal = storage.CountContentsAsync().GetAwaiter().GetResult();

            Console.WriteLine("初始: 正常=" + active0 + " 回收站=" + deleted0);
            Console.WriteLine("删除 3 条: moved=" + moved + " 正常=" + active1 + " 回收站=" + deleted1 +
                              " 删除时间已写=" + marked);
            Console.WriteLine("恢复 2 条: restored=" + restored + " 正常=" + active2 + " 回收站=" + deleted2);
            Console.WriteLine("30 天保留: 回收站=" + deletedAfterRetention +
                              " 本次清理=" + retention.PurgedRecycleBin);
            Console.WriteLine("强制清理: purged=" + purged + " 正常=" + activeFinal +
                              " 回收站=" + deletedFinal + " Contents=" + contentsFinal);

            bool ok = active0 == 10 && deleted0 == 0 &&
                      moved == 3 && active1 == 7 && deleted1 == 3 && marked &&
                      restored == 2 && active2 == 9 && deleted2 == 1 &&
                      deletedAfterRetention == 3 && retention.PurgedRecycleBin == 0 &&
                      purged == 3 && activeFinal == 7 && deletedFinal == 0 &&
                      contentsFinal == 7;

            Console.WriteLine(ok ? "[PASS] 回收站软删除/恢复/清理" : "[FAIL] 回收站软删除/恢复/清理");
            return ok ? 0 : 1;
        }
    }

    private static int RunAutoRecordTest(string db, string sqliteDir)
    {
        ResetDb(db);

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            for (int i = 0; i < 3; i++)
                storage.InsertExecutionAsync(NewRecord(i, "SELECT " + i + " AS exec")).GetAwaiter().GetResult();

            string filePath = @"C:\history-test\auto.sql";

            for (int i = 0; i < 2; i++)
            {
                storage.InsertExecutionAsync(new ExecutionRecord
                {
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    Kind = "edit",
                    Succeeded = -1,
                    SourcePath = filePath,
                    EditorMoniker = filePath,
                    SqlText = "SELECT " + i + " AS auto_edit",
                }).GetAwaiter().GetResult();
            }

            storage.InsertExecutionAsync(new ExecutionRecord
            {
                StartedUtc = DateTime.UtcNow.ToString("o"),
                Kind = "timer",
                Succeeded = -1,
                SourcePath = filePath,
                EditorMoniker = filePath,
                SqlText = "SELECT 99 AS auto_timer",
            }).GetAwaiter().GetResult();

            long active = storage.CountExecutionsAsync().GetAwaiter().GetResult();

            var all = storage.SearchExecutionsAsync(new ExecutionSearch { Limit = 100 })
                .GetAwaiter().GetResult();
            var executes = storage.SearchExecutionsAsync(new ExecutionSearch
            {
                Kind = ExecutionKindFilter.Execute,
                Limit = 100,
            }).GetAwaiter().GetResult();
            var autos = storage.SearchExecutionsAsync(new ExecutionSearch
            {
                Kind = ExecutionKindFilter.Auto,
                Limit = 100,
            }).GetAwaiter().GetResult();

            bool pathKept = autos.Count > 0;
            bool kindsOk = true;
            foreach (var row in autos)
            {
                if (row.Kind != "edit" && row.Kind != "timer") kindsOk = false;
                if (row.SourcePath != filePath) pathKept = false;
            }

            var timerOnly = storage.SearchExecutionsAsync(new ExecutionSearch
            {
                Text = "auto_timer",
                Kind = ExecutionKindFilter.Auto,
                Limit = 100,
            }).GetAwaiter().GetResult();

            Console.WriteLine("记录: 正常=" + active +
                              " 全部=" + all.Count +
                              " 执行=" + executes.Count +
                              " 自动=" + autos.Count);
            Console.WriteLine("自动记录路径保留=" + pathKept +
                              " 类型正确=" + kindsOk +
                              " 搜索定时记录=" + timerOnly.Count);

            bool ok = active == 6 &&
                      all.Count == 6 &&
                      executes.Count == 3 &&
                      autos.Count == 3 &&
                      pathKept && kindsOk &&
                      timerOnly.Count == 1;

            Console.WriteLine(ok ? "[PASS] 自动记录落库/类型筛选/路径" : "[FAIL] 自动记录落库/类型筛选/路径");
            return ok ? 0 : 1;
        }
    }

    private static int RunInspect(string db, string sqliteDir)
    {
        if (!File.Exists(db))
        {
            Console.WriteLine("[FAIL] 数据库不存在: " + db);
            return 1;
        }

        using (var storage = new StorageDatabase(db, sqliteDir))
        {
            if (!storage.IsAvailable)
            {
                Console.WriteLine("[FAIL] 存储不可用: " + storage.Error);
                return 1;
            }

            long executions = storage.CountExecutionsAsync().GetAwaiter().GetResult();
            long deleted = storage.CountDeletedExecutionsAsync().GetAwaiter().GetResult();
            long contents = storage.CountContentsAsync().GetAwaiter().GetResult();
            var recent = storage.GetRecentExecutionsAsync(10).GetAwaiter().GetResult();
            var files = storage.GetRecentFilesAsync(10).GetAwaiter().GetResult();

            Console.WriteLine("数据库: " + storage.DatabasePath);
            Console.WriteLine("Executions=" + executions + " 回收站=" + deleted + " Contents=" + contents);
            Console.WriteLine("最近 " + recent.Count + " 条:");

            foreach (var r in recent)
            {
                string sql = (r.SqlText ?? "").Replace("\r", " ").Replace("\n", " ");
                if (sql.Length > 70) sql = sql.Substring(0, 70) + "...";

                Console.WriteLine("  #" + r.Id + " " + r.StartedUtc +
                                  " " + (r.ServerName ?? "?") + "/" + (r.DatabaseName ?? "?") +
                                  " " + r.DurationMs + "ms" +
                                  " sel=" + r.IsSelectionOnly +
                                  " ok=" + r.Succeeded +
                                  " kind=" + (r.Kind ?? "execute") +
                                  " src=" + (r.SourcePath ?? "—") +
                                  " mon=" + (r.EditorMoniker ?? "—") +
                                  " :: " + sql);
            }

            Console.WriteLine("文件 " + files.Count + " 个:");
            foreach (var f in files)
            {
                var revisions = storage.GetFileRevisionsAsync(f.FullPath, 100)
                    .GetAwaiter().GetResult();

                Console.WriteLine("  " + f.FullPath +
                                  " open=" + f.OpenCount +
                                  " revs=" + revisions.Count +
                                  " lastSaved=" + (f.LastSavedUtc ?? "—"));
            }

            return executions > 0 || files.Count > 0 ? 0 : 2;
        }
    }

    private static string UserDbPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SsmsSqlPlugin", "data.db");
    }

    private static ExecutionRecord NewRecord(int i, string sql)
    {
        return new ExecutionRecord
        {
            StartedUtc = DateTime.UtcNow.ToString("o"),
            DurationMs = i,
            ServerName = "localhost,11433",
            DatabaseName = "test",
            LoginName = "sa",
            RowsAffected = 1,
            Succeeded = 1,
            ErrorNumber = 0,
            ErrorMessage = null,
            SourcePath = null,
            EditorMoniker = "storagecli",
            IsSelectionOnly = 0,
            SqlText = sql,
        };
    }

    private static void ResetDb(string db)
    {
        string dir = Path.GetDirectoryName(db);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(db + suffix)) File.Delete(db + suffix); } catch { }
        }
    }

    private static void TryPrintSizes(string db)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string path = db + suffix;
            try
            {
                if (File.Exists(path))
                    Console.WriteLine("  " + Path.GetFileName(path) + " = " +
                                      new FileInfo(path).Length + " B");
            }
            catch { }
        }
    }

    private static bool Has(string[] args, string name)
    {
        foreach (string arg in args)
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
