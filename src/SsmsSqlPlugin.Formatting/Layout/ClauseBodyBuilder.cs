using System;
using System.Collections.Generic;
using SsmsSqlPlugin.Formatting.Doc;
using static SsmsSqlPlugin.Formatting.Doc.DocFactory;
using SsmsSqlPlugin.Formatting.Tokens;

namespace SsmsSqlPlugin.Formatting.Layout
{
    /// <summary>
    /// 子句体 → Doc。
    ///
    /// 只负责一段连续 token 的排版，不关心语句结构（那是 TokenLayoutBuilder 的事）。
    ///
    /// 关键规则：
    ///   · 逗号 → 断点（由外层 Group 决定压平还是换行）
    ///   · 括号里是子查询 → 独立块展开（SELECT/FROM/WHERE 各占一行，收括号回退对齐）
    ///   · 其余括号 → 内部独立 Group，放得下就单行
    ///   · CASE WHEN → 逐行缩进
    ///   · AND/OR → 逐行缩进（BETWEEN...AND 里的 AND 除外）
    ///   · 注释 → 原位保留
    /// </summary>
    internal sealed class ClauseBodyBuilder
    {
        private readonly List<SqlToken> _tokens;
        private readonly FormatOptions _opt;
        private readonly TokenLayoutBuilder _root;
        private readonly StatementSplitter.Result _split;

        public ClauseBodyBuilder(List<SqlToken> tokens, FormatOptions opt,
                                 TokenLayoutBuilder root, StatementSplitter.Result split)
        {
            _tokens = tokens;
            _opt = opt;
            _root = root;
            _split = split;
        }

        public DocNode Build(int from, int to, string clauseKind)
        {
            var ctx = new Ctx(_tokens, _opt, _split, from, to);
            return ctx.BuildAll();
        }

        /// <summary>
        /// 单行模式：AND/OR/CASE 都不强制换行，整段尽量压成一行。
        /// 用于 MERGE 的 WHEN 条件这类"属于同一行"的短片段。
        /// </summary>
        public DocNode BuildSingleLine(int from, int to)
        {
            var ctx = new Ctx(_tokens, _opt, _split, from, to) { NoForcedBreak = true };
            return ctx.BuildAll();
        }

        /// <summary>
        /// 按逗号切成若干段（括号内、CASE 内不切）。
        /// 供"每项一行"的列表子句使用。
        /// </summary>
        public List<(int From, int To)> SplitByComma(int from, int to)
        {
            var items = new List<(int, int)>();
            int depth = 0, caseDepth = 0, start = from;

            for (int i = from; i <= to; i++)
            {
                var t = _tokens[i];

                if (t.Kind == SqlTokenKind.Punctuation)
                {
                    if (t.IsText("(")) depth++;
                    else if (t.IsText(")")) { if (depth > 0) depth--; }
                    else if (t.IsText(",") && depth == 0 && caseDepth == 0)
                    {
                        items.Add((start, i - 1));
                        start = i + 1;
                    }
                    continue;
                }

                if (t.Kind == SqlTokenKind.Keyword && depth == 0)
                {
                    if (t.Is("CASE")) caseDepth++;
                    else if (t.Is("END") && caseDepth > 0) caseDepth--;
                }
            }

            if (start <= to) items.Add((start, to));
            return items;
        }

        // ────────────────────────────────────────────────────────────

        private sealed class Ctx
        {
            private readonly List<SqlToken> _t;
            private readonly FormatOptions _o;
            private readonly StatementSplitter.Result _split;
            private readonly int _first;
            private readonly int _last;
            private int _i;

            private readonly List<DocNode> _out = new List<DocNode>();
            private readonly List<DocNode> _line = new List<DocNode>();
            private int _caseDepth;
            private int _blockDepth;
            /// <summary>BETWEEN 后面的 AND 属于 BETWEEN 语法，不是逻辑连接词。</summary>
            private bool _betweenPending;

            /// <summary>
            /// true = 不做任何强制换行（AND/OR/CASE WHEN 都压平）。
            /// MERGE 的 WHEN 条件用得到：`WHEN MATCHED AND s.flag = 1 THEN`
            /// 是一句完整语义，拆成两行反而更难读。
            /// </summary>
            public bool NoForcedBreak;

            public Ctx(List<SqlToken> tokens, FormatOptions opt, StatementSplitter.Result split, int first, int last)
            {
                _t = tokens;
                _o = opt;
                _split = split;
                _first = first;
                _last = last;
                _i = first;
            }

            public DocNode BuildAll()
            {
                while (_i <= _last)
                {
                    int before = _i;
                    var t = _t[_i];

                    switch (t.Kind)
                    {
                        case SqlTokenKind.Comment:
                            EmitComment(t);
                            _i++;
                            break;

                        case SqlTokenKind.Semicolon:
                            // 分号紧贴前一个 token（"DELETE;" 而不是 "DELETE ;"）
                            AppendToLine(Text(";"));
                            _i++;
                            break;

                        case SqlTokenKind.Punctuation:
                            HandlePunctuation(t);   // 自己推进 _i
                            break;

                        case SqlTokenKind.Keyword:
                            HandleKeyword(t);
                            _i++;
                            break;

                        default:
                            AppendWithSpacing(t, Text(RenderText(t)));
                            _i++;
                            break;
                    }

                    // 防御：任何分支都必须推进，否则死循环
                    if (_i == before) _i++;
                }

                FlushLine();
                return Concat(_out);
            }

            // ── 关键字 ──
            private void HandleKeyword(SqlToken t)
            {
                // CASE 表达式
                if (t.Is("CASE"))
                {
                    AppendWithSpacing(t, Text(RenderText(t)));
                    _caseDepth++;
                    return;
                }

                if (t.Is("BETWEEN"))
                {
                    _betweenPending = true;
                    AppendWithSpacing(t, Text(RenderText(t)));
                    return;
                }

                if (t.Is("WHEN"))
                {
                    Break();
                    // 缩进 = (层数 - 1) 级。
                    // 外层 CASE 的 WHEN 与子句体同级（缩进由外层 Indent 提供，这里补 0）；
                    // 嵌套 CASE 的 WHEN 要多缩一级，否则内外层 WHEN 会压到同一列。
                    int lv = Math.Max(1, _caseDepth) - 1;
                    string ind = lv > 0 ? new string(' ', _o.IndentSize * lv) : string.Empty;
                    AppendToLine(Text(ind + RenderText(t)));
                    return;
                }

                // IF...ELSE 的 ELSE：回到块起始层
                if (t.Is("ELSE") && _caseDepth == 0)
                {
                    Break();
                    string eind = _blockDepth > 0 ? new string(' ', _o.IndentSize * (_blockDepth - 1)) : string.Empty;
                    AppendToLine(Text(eind + RenderText(t)));
                    return;
                }

                if (t.Is("ELSE") && _caseDepth > 0)
                {
                    Break();
                    int lv = _caseDepth - 1;
                    string ind = lv > 0 ? new string(' ', _o.IndentSize * lv) : string.Empty;
                    AppendToLine(Text(ind + RenderText(t)));
                    return;
                }

                if (t.Is("END"))
                {
                    // CASE 的 END
                    if (_caseDepth > 0)
                    {
                        _caseDepth--;
                        Break();
                        // 外层子句的 Indent 已经缩了一级，CASE 的 END 与 WHEN 同一层，
                        // 不再额外加缩进。
                        AppendToLine(Text(RenderText(t)));
                        return;
                    }

                    // BEGIN...END 的 END：回退一级
                    _blockDepth = Math.Max(0, _blockDepth - 1);
                    Break();
                    string bind = _blockDepth > 0 ? new string(' ', _o.IndentSize * _blockDepth) : string.Empty;
                    AppendToLine(Text(bind + RenderText(t)));
                    return;
                }

                // BEGIN：块开始，强制换行并缩进一级
                if (t.Is("BEGIN"))
                {
                    Break();
                    string bind = _blockDepth > 0 ? new string(' ', _o.IndentSize * _blockDepth) : string.Empty;
                    AppendToLine(Text(bind + RenderText(t)));
                    _blockDepth++;
                    FlushLine();
                    _out.Add(HardLine);
                    _out.Add(Text(new string(' ', _o.IndentSize * _blockDepth)));
                    return;
                }

                // AND / OR：强制每个条件一行。
                // 外层子句是 Group(Concat(head, Indent(Line, body)))，
                // body 在 Indent 作用域内，HardLine 会带上正确缩进。
                if (SqlKeywords.IsLogicalOperator(t.KeywordUpper))
                {
                    if (t.Is("AND") && _betweenPending)
                    {
                        // BETWEEN x AND y 的 AND：跟在同一行
                        _betweenPending = false;
                        AppendToLine(Text(" "));
                        AppendToLine(Text(RenderText(t)));
                        AppendToLine(Text(" "));
                        return;
                    }

                    if (NoForcedBreak)
                    {
                        // 单行模式：AND/OR 就地占位，不断行。
                        // 必须自己补两侧空格 —— NeedsSpaceBefore 里有一条
                        // "逻辑操作符后不再补空格"的规则（那是给强制换行路径用的），
                        // 走这里的话会拼出 "ANDs.flag"。
                        AppendToLine(Text(" "));
                        AppendToLine(Text(RenderText(t)));
                        AppendToLine(Text(" "));
                        return;
                    }

                    Break();
                    AppendToLine(Text(RenderText(t)));
                    AppendToLine(Text(" "));
                    return;
                }

                AppendWithSpacing(t, Text(RenderText(t)));
            }

            // ── 标点（自行推进 _i） ──
            private void HandlePunctuation(SqlToken t)
            {
                if (t.IsText(","))
                {
                    AppendToLine(Text(","));
                    AppendToLine(Line);   // 断点：放得下变空格，放不下换行
                    _i++;
                    return;
                }

                if (t.IsText("("))
                {
                    EmitParenthesized();
                    return;
                }

                if (t.IsText(")"))
                {
                    AppendToLine(Text(")"));
                    _i++;
                    return;
                }

                if (t.IsText("."))
                {
                    AppendToLine(Text("."));
                    _i++;
                    return;
                }

                if (t.IsText(";"))
                {
                    // 分号紧贴前一个 token —— 不能走 AppendWithSpacing
                    AppendToLine(Text(";"));
                    _i++;
                    return;
                }

                AppendWithSpacing(t, Text(t.Text));
                _i++;
            }

            // ── 括号 ──
            private void EmitParenthesized()
            {
                int open = _i;
                int close = FindClose(open);

                bool isSubquery = close > 0 && IsSubqueryBody(open, close);

                // 必须在写入 "(" 之前判断：一旦 AppendToLine，_line 就非空了。
                //
                // atLineStart 单独用不够：垂直模式下 SELECT 的每一列都是
                // 从行首独立排的，`(select ...) as x` 这一项也满足 atLineStart，
                // 但它其实是标量子查询，不该按块展开。
                // 真正的判据是"括号前面没有同行的表达式" —— 即括号就是这一段的起点
                // 且它前面没有紧邻的操作数/运算符。
                bool atLineStart = _line.Count == 0;
                bool clauseLeading = IsClauseLeadingParen(open, close);

                if (NeedsSpaceBeforeParen(open)) AppendToLine(Text(" "));
                AppendToLine(Text("("));

                if (close < 0)
                {
                    for (int k = open + 1; k <= _last; k++)
                    {
                        if (k > open + 1) AppendToLine(Text(" "));
                        AppendToLine(Text(_t[k].Text));
                    }
                    _i = _last + 1;
                    return;
                }

                if (close == open + 1)
                {
                    AppendToLine(Text(")"));
                    _i = close + 1;
                    return;
                }

                if (isSubquery && _o.ExpandSubqueries)
                {
                    // 标量子查询（括号出现在表达式中间/列表项里，如 SELECT (SELECT ...) AS x）
                    // 不按块展开 —— 块展开会把 "AS mx" 挤到收括号后、缩进还错位。
                    //
                    // 判据：括号前一个有意义 token 是**引导词**（FROM/IN/EXISTS/AS…）
                    // 才当块；是逗号/运算符/标识符 → 表达式括号，走普通 Group。
                    if (clauseLeading)
                    {
                        EmitSubquery(open, close);
                        return;
                    }
                }

                // 内部递归，独立 Group
                var inner = new Ctx(_t, _o, _split, open + 1, close - 1);
                var innerDoc = inner.BuildAll();

                AppendToLine(Group(Concat(
                    Indent(Concat(SoftLine, innerDoc)),
                    SoftLine,
                    Text(")"))));

                _i = close + 1;
            }

            /// <summary>
            /// 子查询块：括号内按子句纵向展开，收括号回退到子句行首对齐。
            ///
            /// 目标形状：
            ///     FROM (
            ///         SELECT id, name
            ///         FROM users
            ///         WHERE status = 1
            ///     ) x
            ///
            /// 注意缩进是**相对**的：调用方已经处在子句体的 Indent 作用域里
            /// （缩进一级），所以这里只补一级 —— 再叠就会跑到 8 列去。
            /// 收括号用负缩进抵消，回到子句行首。
            /// </summary>
            private void EmitSubquery(int open, int close)
            {
                var sub = new TokenLayoutBuilder(_t, _o, _split);
                var subDoc = sub.BuildRange(open + 1, close - 1);

                AppendToLine(Concat(HardLine, subDoc));

                // 收括号单独起行，回退一级到子句行首
                AppendToLine(Indent(Concat(HardLine, Text(")")), -_o.IndentSize));

                _i = close + 1;
            }

            /// <summary>括号内是否是子查询：第一个有意义的 token 是 SELECT / WITH。</summary>
            private bool IsSubqueryBody(int open, int close)
            {
                for (int k = open + 1; k <= close - 1; k++)
                {
                    var t = _t[k];
                    if (t.Kind == SqlTokenKind.Comment) continue;
                    if (t.Kind != SqlTokenKind.Keyword) return false;
                    return t.IsAny("SELECT", "WITH");
                }
                return false;
            }

            /// <summary>
            /// 这个左括号是不是"子句体引导括号"——即括号前有引导词（FROM/IN/EXISTS…），
            /// 或者括号前面什么都没有但**括号内不是子查询开头位置**。
            ///
            /// 关键点：垂直模式下 SELECT 的每一项都是独立 Ctx（_first == open），
            /// 所以"前面没有 token"根本不能说明它是子句引导 —— 标量子查询
            /// `(SELECT max(v) ...) AS mx` 也满足。
            /// 这种情况下如果括号前紧跟着 AS 别名，它就是列表项里的表达式，按 Group 处理。
            /// </summary>
            private bool IsClauseLeadingParen(int open, int close)
            {
                // 找括号前有意义 token：优先本段内，本段没有就跨段往前找
                int prev = -1;
                for (int k = open - 1; k >= 0; k--)
                {
                    if (_t[k].Kind == SqlTokenKind.Comment) continue;
                    prev = k;
                    break;
                }

                if (prev < 0) return true;      // 整个批次的第一个 token 就是 "("

                var p = _t[prev];

                if (p.Kind == SqlTokenKind.Punctuation)
                {
                    // "(" 叠 "(" （如 ((SELECT ...))）→ 继续当子句引导
                    return p.IsText("(");
                }

                if (p.Kind != SqlTokenKind.Keyword) return false;

                return p.IsAny("IN", "EXISTS", "FROM", "AS", "JOIN", "APPLY", "USING",
                               "SELECT", "WHERE", "HAVING", "ON", "NOT", "AND", "OR",
                               "INSERT", "INTO", "VALUES", "UPDATE", "SET", "BY", "RETURN");
            }

            private int FindClose(int open)
            {
                int depth = 0;
                for (int k = open; k <= _last; k++)
                {
                    var tk = _t[k];
                    if (tk.Kind != SqlTokenKind.Punctuation) continue;
                    if (tk.IsText("(")) depth++;
                    else if (tk.IsText(")"))
                    {
                        depth--;
                        if (depth == 0) return k;
                    }
                }
                return -1;
            }

            /// <summary>
            /// 括号前是否要空格。
            ///   要：IN ( / VALUES ( / AS ( / EXISTS (  —— 关键字引导
            ///   不要：SUM( / COUNT( / dbo.fn(          —— 函数调用
            /// </summary>
            private bool NeedsSpaceBeforeParen(int open)
            {
                int prev = PrevMeaningful(open);
                if (prev < 0) return false;
                var p = _t[prev];

                // 运算符后的括号要空格：(a + b) * (c - d)
                // 之前只判 Keyword，导致 "* (" 变成 "*(" —— 乘号和括号黏在一起。
                if (p.Kind == SqlTokenKind.Operator) return true;

                if (p.Kind != SqlTokenKind.Keyword) return false;

                if (p.IsAny("IN", "VALUES", "AS", "EXISTS", "OVER", "ON", "USING",
                            "INSERT", "SELECT", "FROM", "WHERE", "AND", "OR", "NOT",
                            "BETWEEN", "LIKE", "THEN", "ELSE", "WHEN", "HAVING", "BY",
                            "JOIN", "UNION", "ALL", "DISTINCT", "TOP", "SET", "INTO",
                            "CASE", "RETURN", "PRINT", "EXEC", "EXECUTE", "WITH",
                            "GROUP", "PARTITION", "ORDER", "ROWS", "RANGE"))
                    return true;

                return false;
            }

            // ── 注释 ──
            private void EmitComment(SqlToken t)
            {
                if (t.IsLineComment)
                {
                    if (_line.Count > 0)
                    {
                        // 行尾注释：跟在当前行后面，行就此结束。
                        // 注意不要再补 HardLine —— AppendToLine 后紧接的 FlushLine
                        // 只会把这一行收进 _out，额外 HardLine 会多出一个空行。
                        AppendToLine(Text("  "));
                        AppendToLine(Text(t.Text.TrimEnd()));
                        Break();
                    }
                    else
                    {
                        Break();
                        _out.Add(Text(t.Text.TrimEnd()));
                        _out.Add(HardLine);
                    }
                    return;
                }

                // 单行块注释就地插入
                bool inlineBlock = _line.Count > 0 && t.Text.IndexOf('\n') < 0;
                if (inlineBlock)
                {
                    // 原来的实现首尾都补空格，导致 "/* c */ , 2" —— 逗号前多个空格。
                    // 这里只补前导空格；逗号紧跟时会由后面的标点处理决定间距。
                    AppendToLine(Text(" "));
                    AppendToLine(Text(t.Text.Trim()));
                    return;
                }

                Break();

                // 多行块注释：每一行都去掉原有前导空白，靠 printer 的缩进重新对齐。
                // 否则原始缩进 + 新缩进会叠加，格式化两次缩进就越跑越远（幂等被破坏）。
                var raw = t.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                for (int k = 0; k < raw.Length; k++)
                {
                    if (k > 0) _out.Add(HardLine);
                    _out.Add(Text(raw[k].Trim()));
                }
                if (raw.Length > 0) _out.Add(HardLine);
            }

            // ── 间距 ──
            private void AppendWithSpacing(SqlToken t, DocNode doc)
            {
                if (NeedsSpaceBefore(t)) AppendToLine(Text(" "));
                AppendToLine(doc);
            }

            private bool NeedsSpaceBefore(SqlToken t)
            {
                if (_line.Count == 0) return false;

                int prev = PrevMeaningful(_i);
                if (prev < 0) return false;
                var p = _t[prev];

                if (p.IsText(".") || t.IsText(".")) return false;
                if (p.IsText("(")) return false;
                if (t.IsText(")")) return false;
                if (t.IsText(",")) return false;
                if (p.IsText(",")) return false;
                if (t.IsText(";")) return false;      // 分号紧贴前一个 token
                if (t.IsText("(")) return NeedsSpaceBeforeParen(_i);

                // 行内块注释之后、逗号之前不留空格
                if (p.Kind == SqlTokenKind.Comment && !p.IsLineComment && t.IsText(",")) return false;

                // 逻辑操作符后已手补空格
                if (p.Kind == SqlTokenKind.Keyword && SqlKeywords.IsLogicalOperator(p.KeywordUpper))
                    return false;

                return true;
            }

            private int PrevMeaningful(int idx)
            {
                for (int k = idx - 1; k >= _first; k--)
                {
                    if (_t[k].Kind != SqlTokenKind.Comment) return k;
                }
                return -1;
            }

            private void AppendToLine(DocNode d)
            {
                if (d != null) _line.Add(d);
            }

            private void FlushLine()
            {
                if (_line.Count == 0) return;
                _out.Add(Concat(_line));
                _line.Clear();
            }

            /// <summary>
            /// 强制断行。已经处在行首时不再重复加 HardLine，
            /// 否则会多出空行（典型：行尾注释本身就以换行收尾，
            /// 后面紧跟的 AND 又断一次 → 中间夹一个空行）。
            /// </summary>
            private void Break()
            {
                FlushLine();
                if (_out.Count == 0) return;                        // 还没输出任何东西，不需要断
                if (_out[_out.Count - 1] is LineNode last && last.IsHard) return;
                _out.Add(HardLine);
            }

            private string RenderText(SqlToken t)
            {
                if (t.Kind == SqlTokenKind.Keyword)
                {
                    return _o.UppercaseKeywords ? (t.KeywordUpper ?? t.Text) : t.Text.ToLowerInvariant();
                }
                return t.Text;
            }
        }
    }
}
