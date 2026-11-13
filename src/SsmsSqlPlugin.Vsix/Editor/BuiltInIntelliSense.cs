using System;
using System.Text;
using EnvDTE;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// SSMS 20 的内建 T-SQL IntelliSense 与插件的旧补全共用同一个弹窗。
    /// 通过 DTE 选项页关闭内建补全，让插件补全成为唯一来源。
    /// </summary>
    internal static class BuiltInIntelliSense
    {
        public static bool Apply(DTE dte, bool disable)
        {
            if (dte == null) return false;

            string[] pages = { "SQL", "Transact-SQL", "SQL Server Tools", "SQL Server", "T-SQL", "SQL Editor" };
            foreach (string page in pages)
            {
                try
                {
                    var properties = dte.Properties["TextEditor", page];
                    var names = new StringBuilder();
                    bool applied = false;

                    for (int i = 1; i <= properties.Count; i++)
                    {
                        var item = properties.Item(i);
                        string name = null;
                        try { name = item.Name; } catch { }
                        if (string.IsNullOrEmpty(name)) continue;

                        names.Append(name).Append("; ");

                        bool isSwitch =
                            name.Equals("EnableIntelliSense", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("AutoListMembers", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("AutoListParams", StringComparison.OrdinalIgnoreCase);
                        if (!isSwitch) continue;

                        try
                        {
                            item.Value = !disable;
                            applied = true;
                            Log.Info("[补全] 内建 IntelliSense 选项 " + page + "." + name +
                                     " = " + (!disable));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("[补全] 设置内建 IntelliSense 失败(" + name + "): " + ex.Message);
                        }
                    }

                    if (applied)
                    {
                        Log.Info("[补全] 内建 IntelliSense 已" + (disable ? "关闭" : "开启"));
                        return true;
                    }

                    Log.Info("[补全] TextEditor/" + page + " 选项: " + names);
                }
                catch (Exception ex)
                {
                    Log.Info("[补全] TextEditor/" + page + " 不可用: " + ex.Message);
                }
            }

            Log.Warn("[补全] 未找到内建 IntelliSense 开关");
            return false;
        }
    }
}
