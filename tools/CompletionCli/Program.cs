using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SsmsSqlPlugin.Core.Completion;
using SsmsSqlPlugin.Core.Metadata;

// P2 补全纯逻辑回归：上下文判定 / 词首排序 / 同步段性能
// 用法：
//   dotnet run             → 跑场景 + 性能
//   dotnet run -- --quiet  → 只打印失败项和统计
class Program
{
    private sealed class Scenario
    {
        public string Name;
        public string SqlWithCaret;
        public string Slot;
        public string[] Contains;
        public string[] Absent;
        public string TopDisplay;
    }

    static int Main(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        var metadata = BuildMetadata();
        var engine = new SqlCompletionEngine();
        var options = new CompletionOptions { UppercaseKeywords = true, MaxItems = 200 };

        var scenarios = BuildScenarios();
        int pass = 0, fail = 0;
        var failures = new List<string>();

        foreach (var s in scenarios)
        {
            int caret = s.SqlWithCaret.IndexOf('|');
            string sql = s.SqlWithCaret.Remove(caret, 1);

            CompletionResult result;
            try
            {
                result = engine.GetCompletions(sql, caret, metadata, options);
            }
            catch (Exception ex)
            {
                fail++;
                failures.Add(s.Name + " 异常: " + ex.Message);
                continue;
            }

            var problem = Check(s, result);
            if (problem == null)
            {
                pass++;
                if (!quiet)
                {
                    Console.WriteLine("PASS  " + s.Name + "  slot=" + result.Slot +
                                      " prefix=\"" + result.Prefix + "\" top=" +
                                      (result.Items.Count > 0 ? result.Items[0].DisplayText : "(空)"));
                }
            }
            else
            {
                fail++;
                failures.Add(s.Name + " -> " + problem +
                             " | slot=" + result.Slot +
                             " prefix=\"" + result.Prefix + "\"" +
                             " items=" + string.Join(",", result.Items.Take(8).Select(x => x.DisplayText)));
            }
        }

        Console.WriteLine();
        Console.WriteLine("场景: " + pass + " 通过 / " + fail + " 失败");
        foreach (var f in failures) Console.WriteLine("  FAIL " + f);

        if (!args.Contains("--no-bench"))
        {
            Console.WriteLine();
            RunBenchmark(engine, options);
        }

        return fail == 0 ? 0 : 1;
    }

    private static string Check(Scenario s, CompletionResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
            return "引擎错误: " + result.Error;

        if (!string.Equals(result.Slot, s.Slot, StringComparison.Ordinal))
            return "槽位期望 " + s.Slot + "，实际 " + result.Slot;

        var names = new HashSet<string>(
            result.Items.Select(x => x.DisplayText), StringComparer.OrdinalIgnoreCase);

        foreach (var expected in s.Contains)
        {
            if (!names.Contains(expected))
                return "缺少候选 " + expected;
        }

        if (s.Absent != null)
        {
            foreach (var bad in s.Absent)
            {
                if (names.Contains(bad))
                    return "不应出现候选 " + bad;
            }
        }

        if (!string.IsNullOrEmpty(s.TopDisplay))
        {
            if (result.Items.Count == 0 || !string.Equals(
                    result.Items[0].DisplayText, s.TopDisplay, StringComparison.OrdinalIgnoreCase))
                return "首位期望 " + s.TopDisplay + "，实际 " +
                       (result.Items.Count > 0 ? result.Items[0].DisplayText : "(空)");
        }

        return null;
    }

    private static MetadataSnapshot BuildMetadata()
    {
        var snapshot = new MetadataSnapshot
        {
            Server = "test", Database = "demo", LoadedUtc = DateTime.UtcNow,
        };

        AddTable(snapshot, "dbo", "Users", "U",
            ("id", "int"), ("name", "nvarchar"), ("email", "nvarchar"));
        AddTable(snapshot, "dbo", "Orders", "U",
            ("id", "int"), ("user_id", "int"), ("amount", "decimal"), ("status", "nvarchar"));
        AddTable(snapshot, "dbo", "Lib_Reader", "U",
            ("reader_id", "int"), ("name", "nvarchar"));
        AddTable(snapshot, "dbo", "Library", "U",
            ("id", "int"), ("title", "nvarchar"));
        AddTable(snapshot, "dbo", "CBO_Cl", "U",
            ("id", "int"), ("name", "nvarchar"));
        AddTable(snapshot, "dbo", "ABC_ABCClassGroup", "U",
            ("id", "int"), ("name", "nvarchar"));
        AddTable(snapshot, "dbo", "vw_UserOrders", "V",
            ("user_id", "int"), ("total", "decimal"));

        snapshot.Objects.Add(new TableMetadata { Schema = "dbo", Name = "usp_GetUser", Kind = "P" });
        snapshot.Objects.Add(new TableMetadata { Schema = "dbo", Name = "usp_SaveOrder", Kind = "P" });
        snapshot.Objects.Add(new TableMetadata { Schema = "dbo", Name = "fn_Count", Kind = "IF" });

        return snapshot;
    }

    private static void AddTable(
        MetadataSnapshot snapshot, string schema, string name, string kind,
        params (string Column, string Type)[] columns)
    {
        var table = new TableMetadata { Schema = schema, Name = name, Kind = kind };
        foreach (var c in columns)
        {
            table.Columns.Add(new ColumnMetadata
            {
                Name = c.Column,
                DataType = c.Type,
                Ordinal = table.Columns.Count + 1,
            });
        }
        snapshot.Objects.Add(table);
    }

    private static List<Scenario> BuildScenarios()
    {
        return new List<Scenario>
        {
            S("SELECT 列表", "SELECT | FROM dbo.Users u", "column", "id", "name", "email"),
            S("别名点号", "SELECT u.| FROM dbo.Users u", "column", "id", "name", "email"),
            S("JOIN 两表", "SELECT | FROM dbo.Users u JOIN dbo.Orders o ON u.id = o.user_id",
                "column", "user_id", "amount"),
            S("FROM 表位置", "SELECT * FROM |", "table", "Users", "Orders", "Lib_Reader", "Library"),
            S("schema 点号", "SELECT * FROM dbo.|", "table", "Users", "Orders", "Lib_Reader"),
            S("JOIN 后表位置", "SELECT * FROM dbo.Users u JOIN |", "table", "Orders", "Lib_Reader"),
            S("WHERE 别名点号", "SELECT * FROM dbo.Users u WHERE u.|", "column", "id", "name", "email"),
            S("EXEC 过程", "EXEC |", "procedure", "usp_GetUser", "usp_SaveOrder"),
            S("EXEC schema 点号", "EXEC dbo.|", "procedure", "usp_GetUser", "usp_SaveOrder"),
            S("CAST 类型", "SELECT CAST(x AS |)", "type", "INT", "NVARCHAR"),
            S("DECLARE 类型", "DECLARE @x |", "type", "INT", "NVARCHAR"),
            new Scenario
            {
                Name = "词首排序",
                SqlWithCaret = "SELECT * FROM lib|",
                Slot = "table",
                Contains = new[] { "Lib_Reader", "Library" },
                TopDisplay = "Lib_Reader",
            },
            S("点号字段", "SELECT * FROM Lib_Reader l WHERE l.|", "column", "reader_id", "name"),
            S("逗号多表", "SELECT * FROM dbo.Users u, dbo.Orders o WHERE o.|", "column", "amount", "status"),
            S("INSERT 表", "INSERT INTO |", "table", "Users", "Orders"),
            S("UPDATE 表", "UPDATE | SET", "table", "Users", "Orders"),
            S("DELETE 表", "DELETE FROM |", "table", "Users", "Orders"),
            S("UPDATE SET 字段", "UPDATE dbo.Users SET |", "column", "id", "name", "email"),
            S("WHERE 表达式", "SELECT * FROM dbo.Users u WHERE u.name = |", "column", "email", "COUNT"),
            S("ORDER BY", "SELECT * FROM dbo.Users u ORDER BY |", "column", "email", "name"),
            S("GROUP BY", "SELECT * FROM dbo.Users u GROUP BY |", "column", "email", "name"),
            S("函数参数", "SELECT COUNT(|) FROM dbo.Users", "column", "id", "COUNT"),
            S("CTE 表位置", "WITH cte AS (SELECT 1 AS x) SELECT * FROM |", "table", "cte"),
            S("临时表", "CREATE TABLE #tmp (a INT, b NVARCHAR(20)); SELECT * FROM |", "table", "#tmp"),
            S("多行新语句", "SELECT * FROM dbo.Users u\nselect |", "default", "SELECT"),
            new Scenario
            {
                Name = "下划线前缀",
                SqlWithCaret = "SELECT * FROM cbo_|",
                Slot = "table",
                Contains = new[] { "CBO_Cl" },
                Absent = new[] { "ABC_ABCClassGroup" },
            },
        };
    }

    private static Scenario S(
        string name, string sql, string slot, params string[] contains)
    {
        return new Scenario
        {
            Name = name,
            SqlWithCaret = sql,
            Slot = slot,
            Contains = contains,
        };
    }

    private static void RunBenchmark(SqlCompletionEngine engine, CompletionOptions options)
    {
        var large = new MetadataSnapshot { Server = "bench", Database = "bench", LoadedUtc = DateTime.UtcNow };
        for (int t = 0; t < 1000; t++)
        {
            var table = new TableMetadata { Schema = "dbo", Name = "Table" + t, Kind = "U" };
            for (int c = 0; c < 20; c++)
            {
                table.Columns.Add(new ColumnMetadata
                {
                    Name = "Column" + c,
                    DataType = "int",
                    Ordinal = c + 1,
                });
            }
            large.Objects.Add(table);
        }

        Bench(engine, options, large, "SELECT * FROM ", "表位置(1000 表)");
        Bench(engine, options, large, "SELECT * FROM dbo.Table500 t WHERE t.", "点号字段");
        Bench(engine, options, large, "SELECT | FROM dbo.Table500 t", "SELECT 列表");
    }

    private static void Bench(
        SqlCompletionEngine engine, CompletionOptions options,
        MetadataSnapshot metadata, string sql, string label)
    {
        // warmup
        for (int i = 0; i < 20; i++) engine.GetCompletions(sql, sql.Length, metadata, options);

        const int iterations = 300;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            engine.GetCompletions(sql, sql.Length, metadata, options);
        }
        sw.Stop();

        double avg = sw.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine("性能  " + label + ": 平均 " + avg.ToString("0.00") + " ms / " +
                          iterations + " 次");
    }
}
