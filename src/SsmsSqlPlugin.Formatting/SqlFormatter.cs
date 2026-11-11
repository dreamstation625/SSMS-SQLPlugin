using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SsmsSqlPlugin.Formatting.Doc;
using SsmsSqlPlugin.Formatting.Layout;
using SsmsSqlPlugin.Formatting.Tokens;

namespace SsmsSqlPlugin.Formatting
{
    /// <summary>
    /// T-SQL 格式化入口（token 驱动版）。
    ///
    /// 流程：
    ///   ① 按 GO 切批 —— GO 不是 T-SQL token，必须自己切
    ///   ② 每批 GetTokenStream → token 列表（含注释）
    ///   ③ AST 只用来找语句边界（token 索引区间），文本仍从 token 出
    ///   ④ token → Document IR
    ///   ⑤ Wadler printer 按行宽渲染
    ///
    /// 与旧实现的区别：旧版直接用 SqlScriptGenerator，注释会被丢掉，
    /// 所以只能整批跳过。现在注释走 token 流，完整保留。
    ///
    /// 设计约束：
    /// - 任何异常都不往外抛，最坏情况是原样返回
    /// - 单批失败不影响其他批
    /// - 幂等：format(format(x)) == format(x)
    /// </summary>
    public sealed class SqlFormatter
    {
        private static readonly Regex GoLine =
            new Regex(@"^[ \t]*GO[ \t]*(\d+)?[ \t]*(--.*)?$",
                      RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public FormatResult Format(string sql, FormatOptions options = null)
        {
            options = options ?? FormatOptions.Default;

            if (string.IsNullOrWhiteSpace(sql))
            {
                return FormatResult.Empty(sql ?? string.Empty);
            }

            string newLine = options.NewLine
                ?? (sql.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n");

            var batches = SplitBatches(sql);
            var output = new StringBuilder();
            var errors = new List<FormatError>();
            int formatted = 0, skipped = 0;
            bool first = true;

            foreach (var batch in batches)
            {
                if (!batch.IsCode)
                {
                    if (!first) output.Append(newLine);
                    // GO 是批次分隔符，统一大写（小写 go 在 SSMS 里也能识别，但输出要规范）
                    output.Append(NormalizeGo(batch.Text));
                    output.Append(newLine);
                    first = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(batch.Text))
                {
                    continue;
                }

                var outcome = FormatBatch(batch.Text, options, newLine);

                if (outcome.Error != null) errors.Add(outcome.Error);

                if (outcome.Formatted != null)
                {
                    if (!first) output.Append(newLine);
                    output.Append(outcome.Formatted);
                    first = false;
                    formatted++;
                }
                else
                {
                    // 失败：原样保留（不能因为格式化失败就改用户代码）
                    if (!first) output.Append(newLine);
                    output.Append(batch.Text.TrimEnd('\r', '\n'));
                    first = false;
                    skipped++;
                }
            }

            return new FormatResult(output.ToString(), errors, formatted, skipped);
        }

        private static (string Formatted, FormatError Error) FormatBatch(
            string text, FormatOptions opt, string newLine)
        {
            // ① 分词
            var tokResult = SqlTokenizer.Tokenize(text);

            if (tokResult.Tokens.Count == 0)
            {
                return (null, null);
            }

            // 语法错误：仍然尝试格式化（token 流对残缺 SQL 也有效），
            // 但把错误报上去。这比整批跳过更有用 —— 用户看到的至少是整齐的。
            FormatError error = null;
            if (!tokResult.IsValid && tokResult.Errors.Count > 0)
            {
                var e0 = tokResult.Errors[0];
                error = e0;
            }

            try
            {
                // ② 语句边界（失败就退化成整批一条）
                var split = StatementSplitter.Split(text, tokResult.Tokens.Count, tokResult.RawToLayout);

                // ③ 建 IR
                var builder = new TokenLayoutBuilder(tokResult.Tokens, opt, split);
                var doc = builder.Build(0, tokResult.Tokens.Count - 1);
                // ④ 渲染
                string rendered = PrettyPrinter.Print(doc, opt.MaxLineWidth, opt.IndentSize, newLine);

                // 收尾：去掉尾部多余空行/空格
                rendered = TrimTrailingBlankLines(rendered, newLine);

                // 兜底：分号前不留空格（各层分支都可能漏掉这条规则，
                // 在出口统一收一次最可靠）
                rendered = Regex.Replace(rendered, @"[ \t]+;", ";");

                return (rendered, error);
            }
            catch (Exception ex)
            {
                return (null, new FormatError(0, 0, ex.Message));
            }
        }

        private static string TrimTrailingBlankLines(string s, string newLine)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var lines = s.Replace("\r\n", "\n").Split('\n');
            int end = lines.Length - 1;
            while (end >= 0 && lines[end].Trim().Length == 0) end--;

            var sb = new StringBuilder();
            for (int i = 0; i <= end; i++)
            {
                if (i > 0) sb.Append(newLine);
                sb.Append(lines[i].TrimEnd());
            }
            return sb.ToString();
        }

        /// <summary>把 GO 行规范成 "GO"（保留行尾注释）。</summary>
        private static string NormalizeGo(string text)
        {
            string t = text.TrimEnd('\r', '\n').Trim();
            if (t.Length == 0) return string.Empty;

            int comment = t.IndexOf("--", StringComparison.Ordinal);
            string code = comment >= 0 ? t.Substring(0, comment).Trim() : t;
            string tail = comment >= 0 ? t.Substring(comment) : string.Empty;

            // 形如 "GO 5" 的要保留次数
            var m = Regex.Match(code, @"^GO(?<n>\s+\d+)?$", RegexOptions.IgnoreCase);
            if (!m.Success) return t;

            string result = "GO" + (m.Groups["n"].Success ? m.Groups["n"].Value : string.Empty);
            return tail.Length > 0 ? result + "  " + tail : result;
        }

        /// <summary>按 GO 切批。GO 是 SSMS/sqlcmd 的批次分隔符，不是 T-SQL 语句。</summary>
        private static List<Batch> SplitBatches(string sql)
        {
            var result = new List<Batch>();
            int last = 0;

            foreach (Match m in GoLine.Matches(sql))
            {
                if (m.Index > last)
                {
                    result.Add(new Batch(sql.Substring(last, m.Index - last), true));
                }
                result.Add(new Batch(m.Value, false));
                last = m.Index + m.Length;
            }

            if (last < sql.Length)
            {
                result.Add(new Batch(sql.Substring(last), true));
            }

            return result;
        }

        private readonly struct Batch
        {
            public Batch(string text, bool isCode)
            {
                Text = text;
                IsCode = isCode;
            }

            public string Text { get; }
            public bool IsCode { get; }
        }
    }
}
