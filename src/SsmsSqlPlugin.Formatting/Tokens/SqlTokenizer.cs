using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SsmsSqlPlugin.Formatting.Tokens
{
    /// <summary>
    /// 把 SQL 文本转成排版用的 token 列表。
    ///
    /// 关键点（已实测）：
    /// GetTokenStream 返回完整 token 流，**包含注释和空白**，且每个 token 带 Line/Column。
    /// 所以不需要 AST 也能保留注释 —— 这正是旧实现（用 ScriptGenerator）丢注释的原因。
    /// </summary>
    internal static class SqlTokenizer
    {
        public sealed class Result
        {
            public List<SqlToken> Tokens = new List<SqlToken>();
            public List<FormatError> Errors = new List<FormatError>();
            public bool IsValid => Errors.Count == 0;

            /// <summary>
            /// ScriptDom 原始 token 索引 → 排版 token 索引。
            /// 值为 -1 表示该原始 token（空白/EOF）没有对应的排版 token。
            /// 语句切分器靠它把 AST 的 token 区间翻译过来。
            /// </summary>
            public int[] RawToLayout = Array.Empty<int>();

            /// <summary>把 AST 给的原始 token 区间翻译成排版 token 区间。</summary>
            public (int From, int To) Map(int rawFirst, int rawLast)
            {
                if (RawToLayout.Length == 0) return (-1, -1);
                if (rawFirst < 0 || rawLast < 0) return (-1, -1);
                if (rawFirst >= RawToLayout.Length) return (-1, -1);

                int from = -1;
                int limit = Math.Min(rawLast, RawToLayout.Length - 1);
                for (int i = rawFirst; i <= limit; i++)
                {
                    if (RawToLayout[i] >= 0) { from = RawToLayout[i]; break; }
                }
                if (from < 0) return (-1, -1);

                int to = from;
                for (int i = limit; i >= rawFirst; i--)
                {
                    if (RawToLayout[i] >= 0) { to = RawToLayout[i]; break; }
                }
                return (from, Math.Max(from, to));
            }
        }

        public static Result Tokenize(string sql)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(sql)) return result;

            IList<ParseError> parseErrors = null;
            IList<TSqlParserToken> raw = null;

            try
            {
                var parser = new TSql180Parser(initialQuotedIdentifiers: true);
                using (var reader = new StringReader(sql))
                {
                    raw = parser.GetTokenStream(reader, out parseErrors);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new FormatError(0, 0, ex.Message));
                return result;
            }

            if (parseErrors != null)
            {
                foreach (var pe in parseErrors)
                {
                    result.Errors.Add(new FormatError(pe.Line, pe.Column, pe.Message));
                }
            }

            if (raw == null) return result;

            // 原始 token 索引 → 排版 token 索引（-1 = 被丢弃）
            var map = new int[raw.Count];
            for (int i = 0; i < map.Length; i++) map[i] = -1;

            int offset = 0;
            for (int idx = 0; idx < raw.Count; idx++)
            {
                var t = raw[idx];

                // 复合运算符：ScriptDom 有时把 >= <> != 拆成两个 token，
                // 排版时必须合并，否则会输出 "> =" 这种错。
                if ((t.Text == ">" || t.Text == "<" || t.Text == "!") && idx + 1 < raw.Count)
                {
                    var nx = raw[idx + 1];
                    if (!string.IsNullOrEmpty(nx.Text) &&
                        (nx.Text[0] == '=' || (t.Text == "<" && nx.Text[0] == '>')))
                    {
                        // 构造合并后的虚拟 token
                        var merged = new SqlToken
                        {
                            Kind = SqlTokenKind.Operator,
                            Text = t.Text + nx.Text,
                            Line = t.Line,
                            Column = t.Column,
                            Offset = offset,
                            RawIndex = idx,
                        };
                        map[idx] = result.Tokens.Count;
                        map[idx + 1] = result.Tokens.Count;
                        result.Tokens.Add(merged);
                        offset += merged.Text.Length;
                        idx++;   // 跳过被合并的那个
                        continue;
                    }
                }

                var kind = ClassifyToken(t);
                if (kind == null) continue;   // 空白和文件结束符跳过

                var tok = new SqlToken
                {
                    Kind = kind.Value,
                    Text = t.Text ?? string.Empty,
                    Line = t.Line,
                    Column = t.Column,
                    Offset = offset,
                    RawIndex = idx,
                    IsLineComment = t.TokenType == TSqlTokenType.SingleLineComment,
                    IsBlockComment = t.TokenType == TSqlTokenType.MultilineComment,
                };

                // ScriptDom 把 int / varchar / datetime 这类**类型名**归为 Identifier，
                // 但排版需要按关键字处理（大写）。这里只升级类型名。
                //
                // ⚠ 不能写成 "在 All 表里就升级"：level / status / type / name / key
                // 这些词 ScriptDom 也报 Identifier，但它们**不是保留字**，
                // 当列名用非常普遍。升级会输出 "WHERE LEVEL = 'vip'"，把列名改坏了。

                if (kind.Value == SqlTokenKind.Identifier)
                {
                    string up = tok.Text.ToUpperInvariant();
                    if (SqlKeywords.IsDataType(up) || IsContextualKeyword(up, result.Tokens))
                    {
                        kind = SqlTokenKind.Keyword;
                        tok.Kind = SqlTokenKind.Keyword;
                    }
                }

                if (kind.Value == SqlTokenKind.Keyword)
                {
                    tok.KeywordUpper = tok.Text.ToUpperInvariant();
                }

                map[idx] = result.Tokens.Count;
                result.Tokens.Add(tok);
                offset += (t.Text ?? string.Empty).Length;
            }

            result.RawToLayout = map;
            return result;
        }

        /// <summary>
        /// 上下文相关的固定搭配词（ScriptDom 报 Identifier 但语义上是关键字）。
        ///
        /// 只按"前一个词"这一个信号判断，不做语法分析：
        ///     WHEN MATCHED        → MATCHED 大写
        ///     SET NOCOUNT ON      → NOCOUNT 大写
        ///     BY TARGET / BY SOURCE → TARGET / SOURCE 大写
        ///     UNBOUNDED PRECEDING  → 大写
        ///
        /// 反例保护：如果前面是 "." （t.matched 这种列引用），一律不升级。
        /// </summary>
        private static bool IsContextualKeyword(string upper, List<SqlToken> sofar)
        {
            if (sofar.Count == 0) return false;

            SqlToken prev = null;
            for (int k = sofar.Count - 1; k >= 0; k--)
            {
                if (sofar[k].Kind == SqlTokenKind.Comment) continue;
                prev = sofar[k];
                break;
            }
            if (prev == null) return false;

            // 列引用：a.matched 里的 matched 不能动（前一个是点号）
            if (prev.IsText(".")) return false;

            // ⚠ 这里**不能**笼统地写 "prev 是 Identifier 就 return false"：
            // MERGE 的 USING 前面正好是目标表别名（Identifier），
            // 那条规则会把 USING 永远挡住。
            // "别名匹配词" 的情况交给各 case 自己判（它们都要求具体前导词）。

            switch (upper)
            {
                case "BY":
                    // PARTITION BY / ORDER BY / GROUP BY —— 前导词可能被 ScriptDom
                    // 报成 Identifier（如 partition），这里放宽：只要往前 3 个 token 内
                    // 出现过这些词就升级。
                    {
                        for (int k = sofar.Count - 1; k >= 0 && k >= sofar.Count - 3; k--)
                        {
                            var t = sofar[k];
                            if (t.Kind == SqlTokenKind.Semicolon) break;
                            if (t.KeywordUpper != null &&
                                (string.Equals(t.KeywordUpper, "PARTITION", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(t.KeywordUpper, "ORDER", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(t.KeywordUpper, "GROUP", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(t.KeywordUpper, "DISTINCT", StringComparison.OrdinalIgnoreCase)))
                                return true;
                            if (t.Kind == SqlTokenKind.Identifier &&
                                (string.Equals(t.Text, "partition", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(t.Text, "order", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(t.Text, "group", StringComparison.OrdinalIgnoreCase)))
                                return true;
                        }
                        return false;
                    }

                case "PARTITION":
                    // OVER (PARTITION BY ...) —— ScriptDom 报 Identifier。
                    // 前面是左括号或 OVER 才算窗口子句，其余（表分区 DDL）不动。
                    return prev.IsAny("OVER") || prev.IsText("(");

                case "MATCHED":
                case "TARGET":
                case "SOURCE":
                    return prev.IsAny("WHEN", "NOT", "BY", "AND");

                // MERGE 的 USING：`MERGE INTO tgt AS t USING src AS s`
                // ScriptDom 报 Identifier，但这是语法固定搭配。
                // 只在前面是"MERGE 的目标表/别名"时才升级 ——
                // 表名/别名都是 Identifier，所以判据是"更早出现过 MERGE 关键字"。
                case "USING":
                    {
                        if (prev.IsAny("MERGE")) return true;

                        // 后面跟的是源表/子查询，前面必然是 MERGE 的目标表或别名
                        // （表名 Identifier / 别名 Identifier / 右括号）。
                        // 往前扫到 MERGE 就升级；扫到别的语句头（SELECT/INSERT…）就放弃。
                        //
                        // ⚠ 起点用 sofar.Count - 1：当前 token 还没进 sofar，
                        // 写 -2 会漏掉紧邻的那个 token。
                        for (int k = sofar.Count - 1; k >= 0; k--)
                        {
                            var t = sofar[k];
                            if (t.Kind == SqlTokenKind.Semicolon) return false;
                            if (t.Kind == SqlTokenKind.Keyword && t.Is("MERGE")) return true;
                            if (t.Kind == SqlTokenKind.Keyword &&
                                t.IsAny("SELECT", "INSERT", "UPDATE", "DELETE", "FROM", "WHERE"))
                                return false;
                        }
                        return false;
                    }

                case "NOCOUNT":
                case "XACT_ABORT":
                case "ANSI_NULLS":
                case "QUOTED_IDENTIFIER":
                case "ANSI_PADDING":
                case "ARITHABORT":
                case "CONCAT_NULL_YIELDS_NULL":
                case "NUMERIC_ROUNDABORT":
                    return prev.Is("SET");

                case "ROLLUP":
                case "CUBE":
                case "GROUPING":
                    return prev.IsAny("BY", "WITH");

                case "PRECEDING":
                case "FOLLOWING":
                case "CURRENT":
                case "UNBOUNDED":
                    return prev.IsAny("ROWS", "RANGE", "BETWEEN", "AND", "UNBOUNDED", "CURRENT");

                // CURRENT ROW 里的 ROW（单数）
                case "ROW":
                    return prev.Is("CURRENT");

                case "ROWS":
                case "RANGE":
                    // 窗口 frame：ROWS BETWEEN ... / RANGE BETWEEN ... / ROWS UNBOUNDED PRECEDING
                    // 前面可能是 OVER(...) 的右括号、BY 的排序项、数字或列名，
                    // 统一判据是"附近出现过 OVER 或 BETWEEN 或 UNBOUNDED"。
                    {
                        for (int k = sofar.Count - 1; k >= 0 && k >= sofar.Count - 8; k--)
                        {
                            var t = sofar[k];
                            if (t.Kind == SqlTokenKind.Semicolon) break;
                            if (t.Kind == SqlTokenKind.Keyword && t.IsAny("OVER", "BETWEEN", "UNBOUNDED", "PRECEDING", "FOLLOWING"))
                                return true;
                            if (t.Kind == SqlTokenKind.Keyword && t.IsAny("SELECT", "FROM", "WHERE"))
                                break;
                        }
                        return false;
                    }

                case "WITHIN":
                    return true;

                // DML 的 OUTPUT 子句：ScriptDom 报 Identifier。
                // 只在同一语句里出现过 INSERT/UPDATE/DELETE/MERGE 时升级，
                // 避免把名为 output 的列/别名大写。
                case "OUTPUT":
                    for (int k = sofar.Count - 1; k >= 0; k--)
                    {
                        var t = sofar[k];
                        if (t.Kind == SqlTokenKind.Semicolon) return false;
                        if (t.Kind == SqlTokenKind.Keyword &&
                            t.IsAny("INSERT", "UPDATE", "DELETE", "MERGE"))
                            return true;
                        if (t.Kind == SqlTokenKind.Keyword && t.Is("SELECT"))
                            return false;
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>null 表示丢弃（空白、EOF）。</summary>
        private static SqlTokenKind? ClassifyToken(TSqlParserToken t)
        {
            switch (t.TokenType)
            {
                case TSqlTokenType.WhiteSpace:
                case TSqlTokenType.EndOfFile:
                    return null;

                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                    return SqlTokenKind.Comment;

                case TSqlTokenType.AsciiStringLiteral:
                case TSqlTokenType.UnicodeStringLiteral:
                case TSqlTokenType.HexLiteral:
                case TSqlTokenType.Integer:
                case TSqlTokenType.Numeric:
                case TSqlTokenType.Money:
                case TSqlTokenType.Real:
                case TSqlTokenType.Variable:
                    return SqlTokenKind.Literal;

                case TSqlTokenType.Identifier:
                case TSqlTokenType.QuotedIdentifier:
                    return SqlTokenKind.Identifier;

                case TSqlTokenType.Semicolon:
                    return SqlTokenKind.Semicolon;

                case TSqlTokenType.Comma:
                case TSqlTokenType.LeftParenthesis:
                case TSqlTokenType.RightParenthesis:
                case TSqlTokenType.Dot:
                    return SqlTokenKind.Punctuation;
            }

            // 剩下的大概率是关键字或运算符；用文本查表判定
            string upper = (t.Text ?? string.Empty).ToUpperInvariant();
            if (SqlKeywords.IsKeyword(upper)) return SqlTokenKind.Keyword;

            // 单字符/双字符运算符
            string txt = t.Text ?? string.Empty;
            switch (txt)
            {
                case "=": case "<": case ">": case "<=": case ">=": case "<>": case "!=":
                case "+": case "-": case "*": case "/": case "%":
                case "&": case "|": case "^": case "~":
                case "+=": case "-=": case "*=": case "/=": case "%=":
                case "::":
                    return SqlTokenKind.Operator;
            }

            // 兜底：带字母的当关键字（大小写转换用），否则当运算符
            if (txt.Length > 0 && (char.IsLetter(txt[0]) || txt[0] == '_' || txt[0] == '@' || txt[0] == '#'))
                return SqlTokenKind.Keyword;

            return SqlTokenKind.Operator;
        }
    }
}
