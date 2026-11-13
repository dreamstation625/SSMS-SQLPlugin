using System;
using SsmsSqlPlugin.Core.Completion;

namespace SsmsSqlPlugin.Localization
{
    /// <summary>
    /// 补全 UI 的文案出口：Core 只产出数据（关键字描述、类型标签），
    /// 这里在显示前换成当前语言，引擎层不需要知道 UI 语言。
    /// </summary>
    internal static class CompletionText
    {
        public static string Describe(CompletionCandidate candidate)
        {
            if (candidate == null) return null;

            string description = candidate.Description;
            if (string.IsNullOrEmpty(description)) return description;

            // 内置关键字/函数/类型：描述是固定短语，按规范名查资源
            if (candidate.Kind == CompletionKind.Keyword ||
                candidate.Kind == CompletionKind.Function ||
                candidate.Kind == CompletionKind.DataType)
            {
                string value;
                if (Loc.TryT(BuiltInKey(candidate), out value)) return value;
                return description;
            }

            // 元数据候选描述形如 "表 · dbo.Users"：保留名字，只翻译类型前缀
            const string separator = " · ";
            int index = description.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) return description;

            string kind = description.Substring(0, index);
            string rest = description.Substring(index + separator.Length);
            string localized = LocalizeKind(kind);
            return localized == null ? description : localized + separator + rest;
        }

        public static string Suffix(CompletionCandidate candidate)
        {
            if (candidate == null) return null;
            if (candidate.Kind == CompletionKind.TempTable) return Loc.T("Temp table");
            return candidate.Suffix;
        }

        private static string BuiltInKey(CompletionCandidate candidate)
        {
            // FilterText 对内置候选是规范大写名（DisplayText 可能被设置改成小写）
            string name = !string.IsNullOrEmpty(candidate.FilterText)
                ? candidate.FilterText
                : candidate.DisplayText;
            return "Completion: " + name;
        }

        /// <summary>
        /// Core 的类型前缀是固定数据值；用中性资源值比对，
        /// 代码里不出现中文字面量，也不受当前 UI 语言影响。
        /// </summary>
        private static string LocalizeKind(string kind)
        {
            if (kind == Loc.Neutral("Table")) return Loc.T("Table");
            if (kind == Loc.Neutral("View")) return Loc.T("View");
            if (kind == Loc.Neutral("Table-valued function")) return Loc.T("Table-valued function");
            if (kind == Loc.Neutral("Stored procedure")) return Loc.T("Stored procedure");
            if (kind == Loc.Neutral("Temp table")) return Loc.T("Temp table");
            return null;
        }
    }
}
