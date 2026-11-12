using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SsmsSqlPlugin.Formatting.Doc;
using static SsmsSqlPlugin.Formatting.Doc.DocFactory;
using SsmsSqlPlugin.Formatting.Tokens;

namespace SsmsSqlPlugin.Formatting.Layout
{
    /// <summary>
    /// Token 序列 → Document IR。
    ///
    /// 结构是三层：
    ///   语句  →  子句(Clause)  →  Group(子句体)
    ///
    /// 为什么要按子句切：
    ///     折行决策必须落在"子句体"这一级。如果整条语句一个 Group，
    ///     那么语句稍长就会全展开，短子句也被迫换行；如果完全不 Group，
    ///     逗号断点永远被压平，长 SELECT 列表一行拉到底。
    ///
    /// 风格参考 VSCode 的 SQL 格式化（垂直风格，一项一行）。
    /// </summary>
    internal sealed class TokenLayoutBuilder
    {
        private readonly List<SqlToken> _tokens;
        private readonly FormatOptions _opt;
        private readonly StatementSplitter.Result _split;

        public TokenLayoutBuilder(List<SqlToken> tokens, FormatOptions opt, StatementSplitter.Result split)
        {
            _tokens = tokens;
            _opt = opt;
            _split = split;
        }

        /// <summary>整批入口：按最外层语句切分。</summary>
        public DocNode Build(int first, int last) => BuildRange(first, last);

        /// <summary>
        /// 任意范围入口（子查询、控制流子语句都用它）。
        ///
        /// 有 AST 语句节点 → 逐条语句；否则 → 当成一条语句做子句切分。
        /// 子查询体没有对应的 TSqlStatement 节点，走的就是后一条路。
        /// </summary>
        public DocNode BuildRange(int first, int last)
        {
            if (first > last) return Nil;

            var stmts = _split != null ? _split.DirectChildren(first, last) : null;

            // 子查询体 / 表达式片段 / AST 切分失败：当一条语句处理
            if (stmts == null || stmts.Count == 0)
            {
                return BuildStatement(first, last, null);
            }

            return BuildStatementSequence(first, last, stmts, true);
        }

        /// <summary>
        /// 按语句区间排版，并把落在语句区间之外的 token 插回原位。
        ///
        /// ScriptDom 的语句区间不包含注释，所以顶层语句之间、文件开头/结尾的
        /// 注释都落在所有 span 之外。只按 span 循环会把它们丢掉 —— 这正是
        /// 「格式化后注释消失」的根因。这里用 cursor 扫描整个范围，把空隙里的
        /// token 也输出。
        /// </summary>
        private DocNode BuildStatementSequence(
            int from, int to, List<StatementSplitter.Span> stmts, bool blankBetweenStatements)
        {
            var parts = new List<DocNode>();
            int cursor = from;
            int prevStatementLast = -1;
            int prevEmittedToken = -1;
            bool prevIsStatement = false;
            bool endsWithHard = false;

            foreach (var st in stmts)
            {
                int s = Math.Max(st.FirstToken, from);
                int e = Math.Min(st.LastToken, to);
                if (s > e) continue;

                if (s > cursor)
                {
                    EmitGap(parts, cursor, s - 1, prevStatementLast, prevEmittedToken,
                            ref endsWithHard, ref prevIsStatement);
                    prevEmittedToken = s - 1;
                    cursor = s;
                }

                var doc = BuildStatement(s, e, st) ?? BuildLooseRange(s, e);

                // 语句末尾不要留硬换行：换行由语句序列统一补，
                // 否则后面的行尾注释会因为 "上一行已结束" 而掉到下一行。
                doc = TrimTrailingHardLines(doc);

                if (parts.Count > 0)
                {
                    if (!endsWithHard) parts.Add(HardLine);

                    // 保留原文空行（最多 2 行）；顶层语句至少保留配置的空行数。
                    int blanks = SourceBlankLines(prevEmittedToken, s);
                    if (prevIsStatement && blankBetweenStatements)
                        blanks = Math.Max(blanks, _opt.BlankLinesBetweenStatements);
                    blanks = Math.Min(blanks, 2);

                    for (int b = 0; b < blanks; b++) parts.Add(HardLine);
                }

                parts.Add(doc);
                prevIsStatement = true;
                endsWithHard = EndsWithHardLine(doc);
                cursor = e + 1;
                prevStatementLast = e;
                prevEmittedToken = e;
            }

            if (cursor <= to)
            {
                EmitGap(parts, cursor, to, prevStatementLast, prevEmittedToken,
                        ref endsWithHard, ref prevIsStatement);
            }

            return Concat(parts);
        }

        /// <summary>
        /// 输出语句之间的空隙 token（基本上是注释）。
        /// 与上一语句同行的尾注释直接贴回行尾，其余按独立行输出。
        /// </summary>
        private void EmitGap(
            List<DocNode> parts, int from, int to, int prevStatementLast, int prevEmittedToken,
            ref bool endsWithHard, ref bool prevIsStatement)
        {
            int cursor = from;

            while (cursor <= to && parts.Count > 0 && !endsWithHard)
            {
                var t = _tokens[cursor];

                // AST span 经常不含语句末尾的分号；直接贴回上一行，别单独起行
                if (t.Kind == SqlTokenKind.Semicolon)
                {
                    parts.Add(Text(";"));
                    cursor++;
                    prevStatementLast = cursor - 1;
                    continue;
                }

                if (t.Kind != SqlTokenKind.Comment) break;
                if (prevStatementLast < 0) break;
                if (t.Line != _tokens[prevStatementLast].Line) break;
                if (t.Text.IndexOf('\n') >= 0 || t.Text.IndexOf('\r') >= 0) break;

                parts.Add(Text(t.IsLineComment ? "  " : " "));
                parts.Add(Text(t.Text.Trim()));
                endsWithHard = false;
                cursor++;
                prevStatementLast = cursor - 1;

                if (t.IsLineComment) break;   // 行注释吃掉本行剩余部分
            }

            if (cursor > to) return;

            var gapDoc = BuildLooseRange(cursor, to);
            if (IsNilDoc(gapDoc)) return;

            if (parts.Count > 0 && !endsWithHard) parts.Add(HardLine);

            // 原文里注释与上一条语句/下一条语句之间的空行
            int blankFrom = cursor > from ? cursor - 1 : prevEmittedToken;
            int blanks = Math.Min(SourceBlankLines(blankFrom, cursor), 2);
            for (int b = 0; b < blanks; b++) parts.Add(HardLine);

            parts.Add(gapDoc);
            endsWithHard = EndsWithHardLine(gapDoc);
            prevIsStatement = false;
        }

        private int SourceBlankLines(int prevIndex, int nextIndex)
        {
            if (prevIndex < 0 || prevIndex >= _tokens.Count) return 0;
            if (nextIndex < 0 || nextIndex >= _tokens.Count) return 0;
            if (_tokens[prevIndex].Line <= 0 || _tokens[nextIndex].Line <= 0) return 0;

            int prevEndLine = EndLine(_tokens[prevIndex]);
            return Math.Max(0, _tokens[nextIndex].Line - prevEndLine - 1);
        }

        private static int EndLine(SqlToken token)
        {
            int extra = 0;
            string text = token.Text ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') extra++;
            }
            return token.Line + extra;
        }

        /// <summary>不切子句、不按语句分块，逐 token 输出一段范围（用于语句间隙）。</summary>
        private DocNode BuildLooseRange(int from, int to)
        {
            if (from > to) return Nil;
            var body = new ClauseBodyBuilder(_tokens, _opt, this, _split);
            return body.Build(from, to, "");
        }

        /// <summary>文档最后是否以硬换行结束（用于避免注释和语句之间多出空行）。</summary>
        private static bool EndsWithHardLine(DocNode node)
        {
            switch (node)
            {
                case LineNode l:
                    return l.IsHard;

                case ConcatNode c:
                    for (int i = c.Parts.Count - 1; i >= 0; i--)
                    {
                        if (IsNilDoc(c.Parts[i])) continue;
                        return EndsWithHardLine(c.Parts[i]);
                    }
                    return false;

                case IndentNode ind:
                    return EndsWithHardLine(ind.Child);

                case GroupNode g:
                    return EndsWithHardLine(g.Child);

                default:
                    return false;
            }
        }

        // ── 语句 → 子句列表 ──

        private sealed class Clause
        {
            /// <summary>子句引导关键字在 token 里的位置；-1 表示无头（比如 SET 列表）。</summary>
            public int HeadIndex = -1;
            public int BodyFrom;
            public int BodyTo;
            /// <summary>子句类型（关键字大写），用于决定缩进/展开策略。</summary>
            public string Kind = "";
        }

        private DocNode BuildStatement(int from, int to, StatementSplitter.Span span)
        {
            // 控制流语句优先：IF / WHILE / BEGIN...END / TRY...CATCH
            // 这些不能靠关键字扫描排版（会把 BEGIN 后的子语句糊成一行），
            // 必须用 AST 的子节点区间逐块处理。
            var flow = TryBuildControlFlow(span);
            if (flow != null) return flow;

            var clauses = SplitClauses(from, to);
            if (clauses.Count == 0) return null;

            if (clauses.Count == 1)
            {
                return BuildClause(clauses[0], false);
            }

            var parts = new List<DocNode>();

            for (int k = 0; k < clauses.Count; k++)
            {
                if (k > 0) parts.Add(HardLine);

                // 去掉子句末尾的硬换行（典型：行尾注释），由子句间的 HardLine
                // 统一提供换行。否则换行落在子句的 Indent 作用域里，
                // 下一个子句会继承 4 个空格。
                parts.Add(TrimTrailingHardLines(BuildClause(clauses[k], k > 0)));
            }
            return Concat(parts);
        }

        // ── 控制流 ──

        /// <summary>
        /// 处理 IF / WHILE / BEGIN...END / TRY...CATCH / DECLARE CURSOR 等块结构。
        /// 返回 null 表示"不是这类结构，走普通子句切分"。
        /// </summary>
        private DocNode TryBuildControlFlow(StatementSplitter.Span span)
        {
            if (span?.Node == null) return null;

            switch (span.Kind)
            {
                case "IfStatement":
                case "WhileStatement":
                    return BuildConditional(span);

                case "BeginEndBlockStatement":
                    return BuildBlock(span, span.FirstToken, span.LastToken);

                case "TryCatchStatement":
                    return BuildTryCatch(span);

                case "MergeStatement":
                    return BuildMerge(span);

                case "BeginTransactionStatement":
                case "CommitTransactionStatement":
                case "RollbackTransactionStatement":
                case "SaveTransactionStatement":
                case "FetchCursorStatement":
                case "FetchStatement":
                    return BuildSimpleRange(span.FirstToken, span.LastToken);

                case "CreateProcedureStatement":
                case "AlterProcedureStatement":
                case "CreateOrAlterProcedureStatement":
                case "CreateFunctionStatement":
                case "AlterFunctionStatement":
                case "CreateOrAlterFunctionStatement":
                case "CreateTriggerStatement":
                case "AlterTriggerStatement":
                case "CreateOrAlterTriggerStatement":
                case "CreateViewStatement":
                case "AlterViewStatement":
                case "CreateOrAlterViewStatement":
                    return BuildRoutine(span);

                default:
                    return null;
            }
        }

        /// <summary>
        /// 存储过程 / 函数 / 触发器：头部（含参数列表）一行，AS 之后是块体。
        ///
        /// 这类语句的 AST 把整个 body 包进 CreateProcedureStatement，
        /// 用普通子句切分会把 body 里每条语句当成子句拼在一起
        /// （输出成 "SET nocount\n    ON IF @a IS NULL ..."）。
        /// 这里定位 AS 之后的第一个 BEGIN / 语句，body 交给语句块排版。
        /// </summary>
        private DocNode BuildRoutine(StatementSplitter.Span span)
        {
            var parts = new List<DocNode>();

            // 在顶层找 AS（参数列表里不会有裸 AS）
            int asIdx = -1;
            int depth = 0;
            for (int i = span.FirstToken; i <= span.LastToken; i++)
            {
                var t = _tokens[i];
                if (t.Kind == SqlTokenKind.Punctuation)
                {
                    if (t.IsText("(")) depth++;
                    else if (t.IsText(")")) { if (depth > 0) depth--; }
                    continue;
                }
                if (depth != 0 || t.Kind != SqlTokenKind.Keyword) continue;
                if (t.Is("AS")) { asIdx = i; break; }
            }

            if (asIdx < 0) return null;   // 没有 AS（比如带 RETURNS TABLE 的函数）→ 退回普通排版

            parts.Add(BuildFlatRange(span.FirstToken, asIdx));

            if (asIdx < span.LastToken)
            {
                parts.Add(HardLine);

                // 内联表值函数：AS 之后是 RETURN (SELECT ...)。
                // 不能走 BuildStatementsBlock —— AST 会把括号里的 SELECT 当成
                // 独立的语句节点，只排 SELECT 会把 RETURN 和括号丢掉。
                int firstBody = NextMeaningful(asIdx + 1, span.LastToken);
                if (firstBody >= 0 && _tokens[firstBody].Is("RETURN"))
                {
                    parts.Add(BuildFlatRange(asIdx + 1, span.LastToken));
                    return Concat(parts);
                }

                // AS 之后可能是 BEGIN...END 块，也可能直接是语句序列。
                // DirectChildren 会跳过被外层包住的情况，这里直接取"AS 之后"的最外层语句。
                var body = _split?.DirectChildren(asIdx + 1, span.LastToken);
                var blockSpan = (body != null && body.Count == 1 && IsBlockLike(body[0])) ? body[0] : null;

                if (blockSpan != null)
                {
                    parts.Add(BuildStatement(blockSpan.FirstToken, blockSpan.LastToken, blockSpan));
                }
                else
                {
                    parts.Add(BuildStatementsBlock(asIdx + 1, span.LastToken));
                }
            }

            return Concat(parts);
        }

        /// <summary>
        /// MERGE：MERGE INTO / USING / ON 各占一行，每个 WHEN ... THEN 动作缩进一级。
        ///
        /// 走普通子句切分时，WHEN 不是主子句（在 CASE 里另有含义），
        /// 于是 MATCHED THEN UPDATE SET ... 会跟 ON 糊在一起、
        /// SET 又被当成主子句提到行首。所以这里单独处理。
        /// </summary>
        private DocNode BuildMerge(StatementSplitter.Span span)
        {
            return BuildMergeWhens(span);
        }

        private DocNode BuildMergeWhens(StatementSplitter.Span span)
        {
            // 按固定骨架顺序处理：MERGE INTO ... / USING ... / ON ... / WHEN ... 分支
            int first = span.FirstToken;
            int last = span.LastToken;

            var parts = new List<DocNode>();

            int usingIdx = FindTopKeyword(first, last, "USING");
            int onIdx = usingIdx > 0 ? FindTopKeyword(usingIdx + 1, last, "ON") : FindTopKeyword(first, last, "ON");
            int firstWhen = FindTopWhen(first, last);
            int outputIdx = FindTopKeyword(firstWhen > 0 ? firstWhen : first, last, "OUTPUT");
            int branchLimit = outputIdx > 0 ? outputIdx - 1 : last;

            int cursor = first;

            // ① MERGE INTO <target>
            int headEnd = usingIdx > 0 ? usingIdx - 1
                        : (onIdx > 0 ? onIdx - 1
                        : (firstWhen > 0 ? firstWhen - 1 : last));

            if (headEnd >= cursor)
            {
                parts.Add(BuildFlatRange(cursor, headEnd));
                cursor = headEnd + 1;
            }

            // ② USING <source>
            if (usingIdx > 0)
            {
                int usingEnd = onIdx > 0 ? onIdx - 1
                             : (firstWhen > 0 ? firstWhen - 1 : last);
                AddIndentedLine(parts, BuildFlatRange(usingIdx, usingEnd));
                cursor = usingEnd + 1;
            }

            // ③ ON <condition>
            if (onIdx > 0)
            {
                int onEnd = firstWhen > 0 ? firstWhen - 1 : last;
                AddIndentedLine(parts, BuildFlatRange(onIdx, onEnd));
                cursor = onEnd + 1;
            }

            // ④ 每个 WHEN ... THEN ... 分支（遇到顶层 OUTPUT 截止）
            while (cursor <= branchLimit)
            {
                int whenIdx = FindTopWhen(cursor, branchLimit);
                if (whenIdx < 0) break;

                int nextWhen = FindTopWhen(whenIdx + 1, branchLimit);
                int branchEnd = nextWhen > 0 ? nextWhen - 1 : branchLimit;

                AddBreak(parts);
                var branchDoc = TrimTrailingHardLines(BuildMergeBranch(whenIdx, branchEnd));
                parts.Add(branchDoc);
                // 把缩进状态收回到顶层，下一行 WHEN/OUTPUT 不会继承 action 的缩进
                parts.Add(HardLine);

                cursor = branchEnd + 1;
            }

            // ⑤ 顶层 OUTPUT ... [INTO ...]
            if (outputIdx > 0)
            {
                int intoIdx = FindTopKeyword(outputIdx + 1, last, "INTO");
                int outEnd = intoIdx > 0 ? intoIdx - 1 : last;

                AddBreak(parts);
                parts.Add(BuildFlatRange(outputIdx, outEnd));

                if (intoIdx > 0)
                {
                    AddBreak(parts);
                    parts.Add(BuildFlatRange(intoIdx, last));
                }
            }

            return Concat(parts);
        }

        private void AddBreak(List<DocNode> parts)
        {
            if (parts.Count == 0) return;
            if (EndsWithHardLine(parts[parts.Count - 1])) return;
            parts.Add(HardLine);
        }

        private void AddIndentedLine(List<DocNode> parts, DocNode content)
        {
            var trimmed = TrimTrailingHardLines(content);

            if (parts.Count > 0 && EndsWithHardLine(parts[parts.Count - 1]))
            {
                parts.Add(Concat(Text(Ind()), trimmed));
            }
            else
            {
                parts.Add(Indent(Concat(HardLine, trimmed), _opt.IndentSize));
            }

            // 缩进块内部的尾换行会留在 indentation 作用域里，
            // 后面再拼顶层内容（WHEN/OUTPUT）就会带上 4 个空格。
            // 这里统一补一个作用域外的硬换行，把列位置收回到 0。
            parts.Add(HardLine);
        }

        /// <summary>一个 WHEN 分支：WHEN MATCHED THEN / UPDATE SET ... / INSERT ... VALUES ...</summary>
        private DocNode BuildMergeBranch(int from, int to)
        {
            // THEN 之前的头部（WHEN MATCHED / WHEN NOT MATCHED BY TARGET ...）
            int thenIdx = -1;
            int depth = 0;
            for (int i = from; i <= to; i++)
            {
                var t = _tokens[i];
                if (t.Kind == SqlTokenKind.Punctuation)
                {
                    if (t.IsText("(")) depth++;
                    else if (t.IsText(")")) { if (depth > 0) depth--; }
                    continue;
                }
                if (depth != 0 || t.Kind != SqlTokenKind.Keyword) continue;
                if (t.Is("THEN")) { thenIdx = i; break; }
            }

            var parts = new List<DocNode>();

            if (thenIdx < 0)
            {
                parts.Add(BuildFlatRange(from, to));
                return Concat(parts);
            }

            parts.Add(BuildMergeHead(from, thenIdx));   // 含 THEN
            if (thenIdx < to)
            {
                int actionFrom = thenIdx + 1;

                // UPDATE SET 是一个整体，不能按普通子句拆成 UPDATE / SET 两行
                bool isUpdate = _tokens[actionFrom].Is("UPDATE");

                var action = isUpdate
                    ? BuildFlatRange(actionFrom, to)
                    : BuildStatementsBlock(actionFrom, to);

                parts.Add(PrefixLines(HardLine, action, Ind()));
            }

            return Concat(parts);
        }

        /// <summary>
        /// MERGE 分支头部（WHEN ... THEN）用单行模式排版：
        /// `WHEN MATCHED AND s.flag = 1 THEN` 是一句完整语义，不该被 AND 拆行。
        /// </summary>
        private DocNode BuildMergeHead(int from, int to)
        {
            if (from > to) return Nil;
            var body = new ClauseBodyBuilder(_tokens, _opt, this, _split);
            return body.BuildSingleLine(from, to);
        }

        /// <summary>一级缩进的空格串。</summary>
        private string Ind() => new string(' ', _opt.IndentSize);

        /// <summary>
        /// 给多行文档的**每一行**加前缀缩进。
        ///
        /// 把内部所有 HardLine 变成 "换行 + 前缀"：做法是在每个 HardLine 之后
        /// 插入一个 Text(prefix)，并在最前面补一个。
        /// 不能用 Indent 节点 —— 那只影响换行后的缩进，对行首第一段文本无效，
        /// 而这里的文本是拼好的 Concat，不会有"外层 Line"来承载缩进。
        /// </summary>
        private static DocNode PrefixLines(DocNode firstSeparator, DocNode doc, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return Concat(firstSeparator, doc);

            var parts = new List<DocNode> { firstSeparator, Text(prefix) };
            Flatten(doc, parts, prefix);
            return Concat(parts);
        }

        /// <summary>递归重写：每个 HardLine 后面跟一个前缀 Text。</summary>
        private static void Flatten(DocNode node, List<DocNode> sink, string prefix)
        {
            switch (node)
            {
                case TextNode t:
                    sink.Add(t);
                    return;

                case LineNode l:
                    sink.Add(l);
                    if (l.IsHard) sink.Add(Text(prefix));
                    return;

                case ConcatNode c:
                    foreach (var p in c.Parts) Flatten(p, sink, prefix);
                    return;

                case IndentNode ind:
                    // 保留内部缩进语义：外层前缀已经补了，Indent 保持原样
                    Flatten(ind.Child, sink, prefix);
                    return;

                case GroupNode g:
                    Flatten(g.Child, sink, prefix);
                    return;
            }
        }

        /// <summary>在括号深度 0 找指定关键字。</summary>
        private int FindTopKeyword(int from, int to, string keyword)
        {
            int depth = 0;
            for (int i = from; i <= to; i++)
            {
                var t = _tokens[i];
                if (t.Kind == SqlTokenKind.Punctuation)
                {
                    if (t.IsText("(")) depth++;
                    else if (t.IsText(")")) { if (depth > 0) depth--; }
                    continue;
                }
                if (depth != 0 || t.Kind != SqlTokenKind.Keyword) continue;
                if (t.Is(keyword)) return i;
            }
            return -1;
        }

        /// <summary>在括号深度 0 找 WHEN（跳过 CASE 内部的 WHEN）。</summary>
        private int FindTopWhen(int from, int to)
        {
            if (from > to) return -1;

            int depth = 0, caseDepth = 0;
            for (int i = from; i <= to; i++)
            {
                var t = _tokens[i];
                if (t.Kind == SqlTokenKind.Punctuation)
                {
                    if (t.IsText("(")) depth++;
                    else if (t.IsText(")")) { if (depth > 0) depth--; }
                    continue;
                }
                if (depth != 0 || t.Kind != SqlTokenKind.Keyword) continue;

                if (t.Is("CASE")) { caseDepth++; continue; }
                if (t.Is("END") && caseDepth > 0) { caseDepth--; continue; }
                if (caseDepth > 0) continue;

                if (t.Is("WHEN")) return i;
            }
            return -1;
        }

        /// <summary>
        /// IF / WHILE：头部条件走普通排版，body 递归。
        ///
        /// 形状：
        ///     IF @x &gt; 0
        ///     BEGIN
        ///         ...
        ///     END
        ///     ELSE
        ///     BEGIN
        ///         ...
        ///     END
        ///
        /// ⚠ 头部区间**只能**用已翻译的 span 算，不能直接读 body.FirstTokenIndex
        /// （那是含空白的原始索引，跟排版 token 列表差好几个位置），
        /// 否则会把 "BEGIN"/"SET" 这些词吃进条件里，输出彻底错乱。
        /// </summary>
        private DocNode BuildConditional(StatementSplitter.Span span)
        {
            var node = span.Node;

            TSqlStatement body = null;
            TSqlStatement elseBody = null;

            if (node is IfStatement ifs)
            {
                body = ifs.ThenStatement;
                elseBody = ifs.ElseStatement;
            }
            else if (node is WhileStatement whs)
            {
                body = whs.Statement;
            }

            var bodySpan = _split?.FindByNode(body);
            int headEnd = bodySpan != null ? bodySpan.FirstToken - 1 : span.LastToken;

            var parts = new List<DocNode>();
            DocNode headDoc = null;

            // 头部：IF <cond> / WHILE <cond>
            if (headEnd >= span.FirstToken)
            {
                headDoc = BuildFlatRange(span.FirstToken, headEnd);
                parts.Add(headDoc);
            }

            if (bodySpan != null)
            {
                var bodyDoc = BuildStatement(bodySpan.FirstToken, bodySpan.LastToken, bodySpan);

                // 单条简单语句（没有 BEGIN 块）→ 跟在条件同一行，视觉上更像代码；
                // 是块（BEGIN...END）才换行。
                if (IsBlockLike(bodySpan))
                {
                    parts.Add(HardLine);
                    parts.Add(bodyDoc);
                }
                else if (headDoc != null && EndsWithHardLine(headDoc))
                {
                    // 条件后面跟了行尾注释，body 已经在下一行，不要再补前导空格
                    parts.Add(bodyDoc);
                }
                else
                {
                    parts.Add(Text(" "));
                    parts.Add(bodyDoc);
                }
            }

            var elseSpan = _split?.FindByNode(elseBody);
            if (elseSpan != null)
            {
                int searchFrom = bodySpan != null ? bodySpan.LastToken + 1 : span.FirstToken;
                int elseIdx = FindPrevKeyword(searchFrom, elseSpan.FirstToken - 1, "ELSE");

                parts.Add(HardLine);

                // body 与 ELSE 之间的注释
                if (elseIdx > 0)
                {
                    var before = BuildLooseRange(searchFrom, elseIdx - 1);
                    if (!IsNilDoc(before))
                    {
                        parts.Add(before);
                        if (!EndsWithHardLine(before)) parts.Add(HardLine);
                    }
                    parts.Add(Text(Head("ELSE")));
                }
                else
                {
                    parts.Add(Text(Head("ELSE")));
                }

                // ELSE 与 else-body 之间的注释
                DocNode after = elseIdx > 0
                    ? BuildLooseRange(elseIdx + 1, elseSpan.FirstToken - 1)
                    : Nil;

                if (!IsNilDoc(after))
                {
                    parts.Add(HardLine);
                    parts.Add(after);
                    if (!EndsWithHardLine(after)) parts.Add(HardLine);
                    parts.Add(BuildStatement(elseSpan.FirstToken, elseSpan.LastToken, elseSpan));
                }
                else if (elseSpan.Kind == "IfStatement")
                {
                    // ELSE IF 链式判断写成一行
                    parts.Add(Text(" "));
                    parts.Add(BuildStatement(elseSpan.FirstToken, elseSpan.LastToken, elseSpan));
                }
                else
                {
                    parts.Add(HardLine);
                    parts.Add(BuildStatement(elseSpan.FirstToken, elseSpan.LastToken, elseSpan));
                }
            }

            return Concat(parts);
        }

        /// <summary>在 [from, to] 里从后往前找关键字（跳过注释）。</summary>
        private int FindPrevKeyword(int from, int to, string keyword)
        {
            for (int i = to; i >= from && i >= 0 && i < _tokens.Count; i--)
            {
                var t = _tokens[i];
                if (t.Kind == SqlTokenKind.Keyword && t.Is(keyword)) return i;
            }
            return -1;
        }

        /// <summary>
        /// BEGIN...END 块：BEGIN / 内容缩进一级 / END。
        ///
        /// ⚠ 不能用"从后往前找最后一个 END"那套：块里每条语句都可能带自己的 END
        /// （CASE 的 END、嵌套 BEGIN 的 END、END TRY 的 END），
        /// 扫描必然抓错。这里用 AST：BeginEndBlockStatement 的区间首尾
        /// 正好就是 BEGIN 和 END 两个 token。
        /// </summary>
        private DocNode BuildBlock(StatementSplitter.Span span, int from, int to)
        {
            int beginIdx = from;
            int endIdx = to;

            var parts = new List<DocNode>
            {
                Text(Head(_tokens[beginIdx].Text)),
            };

            int innerFrom = beginIdx + 1;
            int innerTo = endIdx - 1;

            var body = BuildStatementsBlock(innerFrom, innerTo);
            if (!IsNilDoc(body))
                parts.Add(Indent(Concat(HardLine, body), _opt.IndentSize));

            parts.Add(HardLine);
            parts.Add(Text(Head(_tokens[endIdx].Text)));

            return Concat(parts);
        }

        /// <summary>
        /// BEGIN TRY ... END TRY / BEGIN CATCH ... END CATCH。
        ///
        /// 不用"扫 BEGIN/END 关键字配对"那套 —— BEGIN TRY 里的 BEGIN 和 TRY
        /// 是两个 token，扫关键字会把 TRY/CATCH 当成块内容再排一遍，
        /// 输出成 "BEGIN\n    TRY\n    SELECT..."。
        ///
        /// 改成：TRY 体取 AST 的 TryStatements 首尾，CATCH 体取 CatchStatements 首尾，
        /// 骨架文本（BEGIN TRY / END TRY / BEGIN CATCH / END CATCH）按固定格式出。
        /// </summary>
        private DocNode BuildTryCatch(StatementSplitter.Span span)
        {
            if (!(span.Node is TryCatchStatement tc)) return null;

            var trySpans = SpansOf(tc.TryStatements?.Statements);
            var catchSpans = SpansOf(tc.CatchStatements?.Statements);

            int beginTry = span.FirstToken;
            int tryKw = FindTopKeyword(beginTry, span.LastToken, "TRY");
            if (tryKw < 0) tryKw = beginTry + 1;

            int endTryEnd = -1, endTryKw = -1, beginCatch = -1, catchKw = -1;

            if (catchSpans.Count > 0)
            {
                catchKw = PrevKeywordIndex(catchSpans[0].FirstToken - 1, beginTry, "CATCH");
                beginCatch = catchKw > 0 ? PrevKeywordIndex(catchKw - 1, beginTry, "BEGIN") : -1;
                endTryKw = beginCatch > 0 ? PrevKeywordIndex(beginCatch - 1, beginTry, "TRY") : -1;
                endTryEnd = endTryKw > 0 ? PrevKeywordIndex(endTryKw - 1, beginTry, "END") : -1;
            }
            else
            {
                endTryKw = PrevKeywordIndex(span.LastToken, beginTry, "TRY");
                endTryEnd = endTryKw > 0 ? PrevKeywordIndex(endTryKw - 1, beginTry, "END") : -1;
            }

            var parts = new List<DocNode>();
            parts.Add(Text(Head("BEGIN TRY")));

            // 用 token 区间（而不是节点首尾）排版，才能把 try 体首尾的注释也带上
            int tryBodyFrom = tryKw + 1;
            int tryBodyTo = endTryEnd > 0 ? endTryEnd - 1 : span.LastToken;
            var tryBody = BuildStatementSequence(tryBodyFrom, tryBodyTo, trySpans, false);
            if (!IsNilDoc(tryBody))
                parts.Add(Indent(Concat(HardLine, tryBody), _opt.IndentSize));

            parts.Add(HardLine);
            parts.Add(Text(Head("END TRY")));

            if (beginCatch > 0 && catchKw > 0)
            {
                parts.Add(HardLine);

                // END TRY 与 BEGIN CATCH 之间的注释
                var between = BuildLooseRange(endTryKw + 1, beginCatch - 1);
                if (!IsNilDoc(between))
                {
                    parts.Add(between);
                    if (!EndsWithHardLine(between)) parts.Add(HardLine);
                }

                parts.Add(Text(Head("BEGIN CATCH")));

                int afterLastCatch = catchSpans.Count > 0
                    ? catchSpans[catchSpans.Count - 1].LastToken + 1
                    : catchKw + 1;
                int catchEnd = FindTopKeyword(afterLastCatch, span.LastToken, "END");

                int catchBodyFrom = catchKw + 1;
                int catchBodyTo = catchEnd > 0 ? catchEnd - 1 : span.LastToken;

                var catchBody = BuildStatementSequence(catchBodyFrom, catchBodyTo, catchSpans, false);
                if (!IsNilDoc(catchBody))
                    parts.Add(Indent(Concat(HardLine, catchBody), _opt.IndentSize));

                parts.Add(HardLine);
                parts.Add(Text(Head("END CATCH")));
            }

            return Concat(parts);
        }

        private List<StatementSplitter.Span> SpansOf(IList<TSqlStatement> nodes)
        {
            var spans = new List<StatementSplitter.Span>();
            if (nodes == null) return spans;

            foreach (var n in nodes)
            {
                var sp = _split?.FindByNode(n);
                if (sp != null) spans.Add(sp);
            }

            spans.Sort((a, b) => a.FirstToken.CompareTo(b.FirstToken));
            return spans;
        }

        /// <summary>
        /// 从 from 往前找最近的指定骨架词（跳过注释）。
        /// TRY / CATCH 会被 ScriptDom 标成 Identifier，所以按文本比较，不只看 Kind。
        /// </summary>
        private int PrevKeywordIndex(int from, int min, string keyword)
        {
            for (int i = from; i >= min && i >= 0 && i < _tokens.Count; i--)
            {
                var t = _tokens[i];
                if (t.Kind == SqlTokenKind.Comment) continue;
                if (string.Equals(t.Text, keyword, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>把一段范围当成"若干语句"排（有 AST 走 AST，没有就逐条切子句）。</summary>
        private DocNode BuildStatementsBlock(int from, int to)
        {
            if (from > to) return Nil;

            var stmts = _split != null ? _split.DirectChildren(from, to) : null;
            if (stmts == null || stmts.Count == 0)
            {
                return BuildStatement(from, to, null);
            }

            return BuildStatementSequence(from, to, stmts, false);
        }

        /// <summary>
        /// 递归排一个嵌套语句（IF body / ELSE body）。
        ///
        /// ⚠ 这里只能用节点在 AllStatements 里的已翻译区间，不能再读
        /// node.FirstTokenIndex —— 那是 ScriptDom 原始 token 索引（含空白），
        /// 跟排版 token 列表错位。
        /// </summary>
        private DocNode BuildNested(TSqlStatement node)
        {
            if (node == null) return Nil;

            var span = _split?.FindByNode(node);
            if (span == null) return Nil;

            return BuildStatement(span.FirstToken, span.LastToken, span);
        }

        /// <summary>不切子句，逐 token 压平（用于 IF/WHILE 的条件头部）。</summary>
        private DocNode BuildFlatRange(int from, int to)
        {
            if (from > to) return Nil;
            var body = new ClauseBodyBuilder(_tokens, _opt, this, _split);
            return body.Build(from, to, "");
        }

        /// <summary>整段压成一行（BEGIN TRANSACTION / FETCH 等定长语句）。</summary>
        private DocNode BuildSimpleRange(int from, int to)
        {
            if (from > to) return Nil;
            var body = new ClauseBodyBuilder(_tokens, _opt, this, _split);
            return body.BuildSingleLine(from, to);
        }

        // ── 子句切分 ──

        /// <summary>
        /// 按主子句关键字切分。括号内的主子句（子查询）不参与外层切分 ——
        /// 它们会在 BuildClause 内部由 EmitParenthesized 递归处理。
        /// </summary>
        private List<Clause> SplitClauses(int from, int to)
        {
            var result = new List<Clause>();
            int depth = 0;
            int caseDepth = 0;
            int segStart = from;
            int segHead = -1;
            string segKind = "";

            for (int i = from; i <= to; i++)
            {
                var t = _tokens[i];

                if (t.Kind == SqlTokenKind.Punctuation)
                {
                    if (t.IsText("(")) depth++;
                    else if (t.IsText(")")) { if (depth > 0) depth--; }
                    continue;
                }

                if (depth != 0) continue;
                if (t.Kind != SqlTokenKind.Keyword) continue;

                // CASE 内部的 WHEN/ELSE 不切子句
                if (t.IsAny("WHEN", "ELSE", "THEN")) continue;

                // CASE...END 内部不切子句（否则 CASE 里的 SELECT 会把语句切断）
                if (t.Is("CASE")) { caseDepth++; continue; }
                if (t.Is("END") && caseDepth > 0) { caseDepth--; continue; }
                if (caseDepth > 0) continue;

                if (!SqlKeywords.IsMajorClause(t.KeywordUpper)) continue;
                if (!IsRealClauseStart(i, to)) continue;
                if (IsSecondaryPosition(i, from)) continue;

                // 切一刀：把 [segStart, i-1] 收成一个 clause
                if (i > segStart || segHead >= 0)
                {
                    int bodyFrom = segHead >= 0 ? segHead + 1 : segStart;
                    if (bodyFrom <= i - 1 || segHead >= 0)
                    {
                        result.Add(new Clause
                        {
                            HeadIndex = segHead,
                            BodyFrom = bodyFrom,
                            BodyTo = i - 1,
                            Kind = segKind,
                        });
                    }
                }

                segStart = i;
                segHead = i;
                segKind = t.KeywordUpper;
            }

            // 收尾
            {
                int bodyFrom = segHead >= 0 ? segHead + 1 : segStart;
                if (segStart <= to)
                {
                    result.Add(new Clause
                    {
                        HeadIndex = segHead,
                        BodyFrom = bodyFrom,
                        BodyTo = to,
                        Kind = segKind,
                    });
                }
            }

            return result;
        }

        private bool IsSecondaryPosition(int i, int from)
        {
            for (int k = i - 1; k >= from; k--)
            {
                var p = _tokens[k];
                if (p.Kind == SqlTokenKind.Comment) continue;

                // SET NOCOUNT ON / SET XACT_ABORT ON：ON 是选项值，不是 JOIN 条件
                int setBefore = PrevMeaningful(k);
                if (setBefore >= from && _tokens[setBefore].Is("SET")) return true;

                if (p.Kind == SqlTokenKind.Identifier)
                {
                    return false;
                }

                if (p.Kind != SqlTokenKind.Keyword) return false;

                if (p.IsAny("GROUP", "ORDER", "PARTITION")) return true;
                if (p.IsAny("LEFT", "RIGHT", "FULL", "INNER", "CROSS", "OUTER")) return true;
                if (p.Is("NOT")) return true;
                if (p.Is("UNION")) return true;
                if (p.Is("INSERT") || p.Is("MERGE")) return true;
                if (p.Is("WITHIN")) return true;    // WITHIN GROUP (ORDER BY ...) 是一个整体

                // SET 选项：SET NOCOUNT ON / SET XACT_ABORT ON / SET ANSI_NULLS ON
                // 这里的 ON 是选项值，不是 JOIN 条件，不能切子句。
                // 判据：前一个 token 是 SET 后面的"选项名"（标识符或关键字），
                // 且再往前是 SET。
                if (p.Is("SET")) return true;

                return false;
            }
            return false;
        }

        /// <summary>
        /// JOIN 相关词只有真的构成 JOIN 语义时才切子句。
        /// 否则 `WHERE LEFT(@s, 1) = …` / `SELECT LEFT(name, 1)` 里的 LEFT
        /// 会被当成 LEFT JOIN，强制断行成两个子句。
        /// </summary>
        private bool IsRealClauseStart(int i, int to)
        {
            var t = _tokens[i];

            if (t.Is("JOIN")) return true;

            if (t.IsAny("LEFT", "RIGHT", "FULL"))
            {
                int next = NextMeaningful(i, to);
                return next >= 0 && IsWord(_tokens[next], "JOIN", "OUTER");
            }

            if (t.IsAny("INNER", "CROSS", "OUTER"))
            {
                int next = NextMeaningful(i, to);
                return next >= 0 && IsWord(_tokens[next], "JOIN", "APPLY");
            }

            if (t.Is("APPLY"))
            {
                int prev = PrevMeaningful(i);
                return prev >= 0 && IsWord(_tokens[prev], "CROSS", "OUTER");
            }

            return true;
        }

        /// <summary>
        /// 按文本匹配关键字。ScriptDom 会把 APPLY / TRY / CATCH 这类词标成
        /// Identifier，不能只依赖 KeywordUpper。
        /// </summary>
        private static bool IsWord(SqlToken token, params string[] words)
        {
            if (token == null) return false;
            if (token.Kind == SqlTokenKind.Comment) return false;

            foreach (var w in words)
            {
                if (string.Equals(token.Text, w, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private int NextMeaningful(int idx, int to)
        {
            for (int k = idx + 1; k <= to; k++)
            {
                if (_tokens[k].Kind != SqlTokenKind.Comment) return k;
            }
            return -1;
        }

        private bool ContainsSubqueryToken(int from, int to)
        {
            for (int k = from; k <= to && k < _tokens.Count; k++)
            {
                var t = _tokens[k];
                if (t.Kind == SqlTokenKind.Keyword && t.IsAny("SELECT", "WITH")) return true;
            }
            return false;
        }

        private bool ContainsCommentToken(int from, int to)
        {
            for (int k = from; k <= to && k < _tokens.Count; k++)
            {
                if (_tokens[k].Kind == SqlTokenKind.Comment) return true;
            }
            return false;
        }

        private int PrevMeaningful(int idx)
        {
            for (int k = idx - 1; k >= 0; k--)
            {
                if (_tokens[k].Kind != SqlTokenKind.Comment) return k;
            }
            return -1;
        }

        // ── 子句 → Doc ──

        private DocNode BuildClause(Clause c, bool isContinuation)
        {
            DocNode head = null;
            if (c.HeadIndex >= 0)
            {
                var ht = _tokens[c.HeadIndex];
                string htxt = Head(ht.KeywordUpper ?? ht.Text);

                // ON / USING 是 JOIN 的从属条件：行首直接补缩进。
                // 这里不能用 Indent 节点 —— clause 之间由 HardLine 分隔，
                // 而 HardLine 在 Indent 作用域之外，缩进加不上。
                if (c.Kind == "ON" || c.Kind == "USING")
                {
                    htxt = new string(' ', _opt.IndentSize) + htxt;
                }

                head = Text(htxt);
            }

            if (c.BodyFrom > c.BodyTo)
            {
                // 空子句体（比如独立的 GO / END）
                return head ?? Nil;
            }

            var body = new ClauseBodyBuilder(_tokens, _opt, this, _split);

            // 列表型子句 + 强制垂直 → 每项一行（与行宽无关）
            bool vertical = _opt.SelectListStyle == SelectListStyle.Vertical
                            && c.Kind == "SELECT";

            if (vertical)
            {
                var items = body.SplitByComma(c.BodyFrom, c.BodyTo);
                // 单列且含子查询/注释时也展开：否则函数调用的参数块会从
                // "SELECT ISNULL(" 中间开始，收括号无法和函数起点对齐。
                bool complexSingle = items.Count == 1
                                     && (ContainsSubqueryToken(c.BodyFrom, c.BodyTo)
                                         || ContainsCommentToken(c.BodyFrom, c.BodyTo));

                // 只有一列（或 SELECT *）不值得展开
                if (items.Count > 1 || complexSingle)
                {
                    var rendered = new List<DocNode>();
                    for (int k = 0; k < items.Count; k++)
                    {
                        if (k > 0)
                        {
                            // SplitByComma 已经把逗号切掉了，这里必须补回来。
                            // 用 Line 而不是 SoftLine：压平要留一个空格，
                            // 否则会拼出 "a b" 这种非法 SQL。
                            rendered.Add(Text(","));
                            rendered.Add(Line);
                        }
                        rendered.Add(body.Build(items[k].From, items[k].To, c.Kind));
                    }

                    var listDoc = GroupBreak(Concat(rendered));

                    if (head == null) return listDoc;

                    return Concat(head, Indent(Concat(HardLine, listDoc), _opt.IndentSize));
                }
            }

            var bodyDoc = body.Build(c.BodyFrom, c.BodyTo, c.Kind);

            if (head == null)
            {
                return bodyDoc;
            }

            // 子句体整体作为一个 Group：
            //   放得下 → "SELECT a, b, c"
            //   放不下 → "SELECT\n    a,\n    b"
            //
            // 头部与体之间用 Line（压平=一个空格），不是 SoftLine。
            // SoftLine 压平后是空串，会拼出 "SELECTa" —— 这是幂等失败的根源。
            //
            // 注意：所有带体的子句都要放进 Indent 里，否则子句体内的 HardLine
            // （AND / CASE WHEN）会重置到 0 缩进，缩进全丢。
            var clauseDoc = Group(Concat(
                head,
                Indent(Concat(Line, bodyDoc), _opt.IndentSize)));

            // ON / USING 是 JOIN 的从属条件，整体再缩进一级（挂在 JOIN 下面）
            if (c.Kind == "ON" || c.Kind == "USING")
            {
                return Indent(clauseDoc, _opt.IndentSize);
            }

            return clauseDoc;
        }

        private string Head(string text)
            => _opt.UppercaseKeywords ? text.ToUpperInvariant() : text.ToLowerInvariant();

        /// <summary>
        /// 语句是否"块状"（排版时应该独立成行）。
        /// BEGIN...END、IF、WHILE、TRY/CATCH 都是；PRINT、SET、SELECT 不是。
        /// </summary>
        private static bool IsBlockLike(StatementSplitter.Span span)
        {
            switch (span?.Kind)
            {
                case "BeginEndBlockStatement":
                case "IfStatement":
                case "WhileStatement":
                case "TryCatchStatement":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsNilDoc(DocNode d)
            => d == null || (d is ConcatNode c && c.Parts.Count == 0);
    }
}
