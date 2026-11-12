using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SsmsSqlPlugin.Core.Metadata;

namespace SsmsSqlPlugin.Core.Completion
{
    /// <summary>
    /// 从整段脚本里捞临时表/表变量名。
    /// 当前语句窗口里的列定义由 token 解析；窗口之前的定义只补名字，不猜列。
    /// </summary>
    internal static class ScriptTempScanner
    {
        private static readonly Regex CreateTable = new Regex(
            @"\bCREATE\s+TABLE\s+(?<name>#\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DeclareTable = new Regex(
            @"\bDECLARE\s+(?<name>@\w+)\s+TABLE\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<TableMetadata> Scan(string sql)
        {
            var result = new List<TableMetadata>();
            if (string.IsNullOrEmpty(sql)) return result;

            Collect(CreateTable, sql, result);
            Collect(DeclareTable, sql, result);
            return result;
        }

        private static void Collect(Regex regex, string sql, List<TableMetadata> result)
        {
            foreach (Match match in regex.Matches(sql))
            {
                string name = match.Groups["name"].Value;
                if (string.IsNullOrEmpty(name)) continue;

                bool exists = false;
                for (int i = 0; i < result.Count; i++)
                {
                    if (string.Equals(result[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) result.Add(new TableMetadata { Name = name, Kind = "TEMP" });
            }
        }
    }
}
