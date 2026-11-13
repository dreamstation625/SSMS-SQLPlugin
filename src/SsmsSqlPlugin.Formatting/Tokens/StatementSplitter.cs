using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SsmsSqlPlugin.Formatting.Tokens
{
    /// <summary>
    /// 语句切分器。
    ///
    /// 只做一件事：用 AST 找出每条语句覆盖的 token 索引区间。
    /// 文本内容不从这里出 —— 那是 token 流的事，这样注释不会被丢掉。
    ///
    /// 为什么不用 AST 直接生成文本：ScriptDom 的 SqlScriptGenerator 会丢注释，
    /// 而注释就在 token 流里，位置信息完整（已实测）。
    ///
    /// 保留**全部**（含嵌套）语句节点，是为了让排版层能结构化处理控制流：
    ///   IF / WHILE / BEGIN...END / TRY...CATCH 不能靠扫描关键字猜，
    ///   必须拿到 AST 的子节点 token 区间，否则会得到
    ///   "SET @i = @i + 1 IF @i = 5 BREAK" 这种糊在一起的输出。
    /// </summary>
    internal static class StatementSplitter
    {
        public sealed class Span
        {
            /// <summary>起始 token 索引（含）。</summary>
            public int FirstToken;
            /// <summary>结束 token 索引（含）。</summary>
            public int LastToken;
            /// <summary>语句类型名（TSqlStatement 的运行时类型名），用于选择排版策略。</summary>
            public string Kind;
            /// <summary>原始 AST 节点，控制流排版需要读它的子节点区间。</summary>
            public TSqlStatement Node;
        }

        public sealed class Result
        {
            /// <summary>全部语句（含嵌套），按 FirstToken 升序、同起点时外层在前。</summary>
            public List<Span> AllStatements = new List<Span>();

            /// <summary>AST 节点 → span（引用相等）。控制流递归靠它拿子节点区间。</summary>
            public Dictionary<TSqlStatement, Span> ByNode =
                new Dictionary<TSqlStatement, Span>(ReferenceEqualityComparer.Instance);

            /// <summary>批次最外层语句（互不重叠）。</summary>
            public List<Span> Statements = new List<Span>();

            /// <summary>未被任何语句覆盖的 token 索引集合（GO、游离注释等）。</summary>
            public HashSet<int> OutsideTokens = new HashSet<int>();

            /// <summary>
            /// 取 [from, to] 范围内的**最外层**语句（互不重叠）。
            ///
            /// 依赖 AllStatements 的排序：同一起点时外层（LastToken 更大）在前，
            /// 所以贪心取第一个可用语句，天然就是最外层。
            /// 子查询不是 TSqlStatement，不会被返回 —— 调用方需要自己做子句切分兜底。
            /// </summary>
            public List<Span> DirectChildren(int from, int to)
            {
                var picked = new List<Span>();
                int cursor = from;
                foreach (var s in AllStatements)
                {
                    if (s.FirstToken < cursor) continue;   // 已被前一个语句覆盖（或起点在范围之前）
                    if (s.LastToken > to) continue;        // 越界
                    if (s.LastToken < s.FirstToken) continue;
                    picked.Add(s);
                    cursor = s.LastToken + 1;
                }
                return picked;
            }

            /// <summary>按 AST 节点反查已经翻译好的 span（控制流递归用）。</summary>
            public Span FindByNode(TSqlStatement node)
            {
                if (node == null) return null;
                if (ByNode.TryGetValue(node, out var s)) return s;

                // 兜底：引用比较失败时按类型 + 顺序找（理论不会走到）
                foreach (var x in AllStatements)
                {
                    if (ReferenceEquals(x.Node, node)) return x;
                }
                return null;
            }
        }

        /// <summary>ScriptDom 节点没重写 Equals/GetHashCode，按引用比较即可（也是默认行为）。</summary>
        private sealed class ReferenceEqualityComparer : IEqualityComparer<TSqlStatement>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public bool Equals(TSqlStatement x, TSqlStatement y) => ReferenceEquals(x, y);
            public int GetHashCode(TSqlStatement obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// 切分语句。
        ///
        /// map 是"原始 token 索引 → 排版 token 索引"的映射（不需要时传 null）。
        /// ⚠ 这个参数不能省：AST 的 FirstTokenIndex/LastTokenIndex 是按
        /// ScriptDom 原始 token 流（**含空白**）编号的，而我们的 token 列表丢了空白。
        /// 少了映射，所有语句区间都会错位到别的 token 上，
        /// 输出会变成 "PRINT\nBEGIN" 这种把关键字挪错位的怪样子。
        /// </summary>
        public static Result Split(string sql, int tokenCount, int[] map = null)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(sql) || tokenCount == 0) return result;

            TSqlFragment tree;
            IList<ParseError> errors;
            try
            {
                var parser = ScriptDomCompat.CreateParser();
                using (var reader = new StringReader(sql))
                {
                    tree = parser.Parse(reader, out errors);
                }
            }
            catch
            {
                return result;
            }

            if (tree == null) return result;

            var collector = new StatementCollector();
            try
            {
                tree.Accept(collector);
            }
            catch
            {
                // 收集失败就退化成"整段一条语句"
                return result;
            }

            var spans = new List<Span>();
            foreach (var st in collector.Statements)
            {
                if (st.FirstTokenIndex < 0 || st.LastTokenIndex < 0) continue;
                if (st.LastTokenIndex < st.FirstTokenIndex) continue;

                int first, last;
                if (map != null && map.Length > 0)
                {
                    (first, last) = MapRange(map, st.FirstTokenIndex, st.LastTokenIndex);
                }
                else
                {
                    first = st.FirstTokenIndex;
                    last = st.LastTokenIndex;
                }

                if (first < 0 || last < 0) continue;
                if (first >= tokenCount) continue;

                spans.Add(new Span
                {
                    FirstToken = first,
                    LastToken = Math.Min(last, tokenCount - 1),
                    Kind = st.GetType().Name,
                    Node = st,
                });
            }

            // 同一起点：外层（末尾更大）在前，这样 DirectChildren 的贪心能先吃到外层
            spans.Sort((a, b) =>
            {
                int c = a.FirstToken.CompareTo(b.FirstToken);
                if (c != 0) return c;
                return b.LastToken.CompareTo(a.LastToken);
            });

            result.AllStatements = spans;

            // 节点 → span，供控制流（IF/WHILE/TRY）按 AST 子节点直接取区间
            foreach (var s in spans) result.ByNode[s.Node] = s;

            result.Statements = result.DirectChildren(0, tokenCount - 1);

            // 标记没有被任何（最外层）语句覆盖的 token
            var covered = new bool[tokenCount];
            foreach (var s in result.Statements)
            {
                for (int i = s.FirstToken; i <= s.LastToken && i < tokenCount; i++) covered[i] = true;
            }
            for (int i = 0; i < tokenCount; i++)
            {
                if (!covered[i]) result.OutsideTokens.Add(i);
            }

            return result;
        }

        /// <summary>把原始 token 区间翻译成排版 token 区间（跳过空白）。</summary>
        private static (int From, int To) MapRange(int[] map, int rawFirst, int rawLast)
        {
            if (rawFirst < 0 || rawLast < 0) return (-1, -1);
            if (rawFirst >= map.Length) return (-1, -1);

            int limit = Math.Min(rawLast, map.Length - 1);

            int from = -1;
            for (int i = rawFirst; i <= limit; i++)
            {
                if (map[i] >= 0) { from = map[i]; break; }
            }
            if (from < 0) return (-1, -1);

            int to = from;
            for (int i = limit; i >= rawFirst; i--)
            {
                if (map[i] >= 0) { to = map[i]; break; }
            }

            return (from, Math.Max(from, to));
        }

        /// <summary>
        /// 收集全部语句节点（含嵌套）。
        ///
        /// ⚠ 必须重写 Visit 而不是 ExplicitVisit：
        /// 实测（ScriptDom 161/170 都一样）`ExplicitVisit(TSqlStatement)` **永远不会被调用**
        /// —— 访问器按节点的**具体类型**分发，抽象基类 `TSqlStatement` 上没有对应的
        /// ExplicitVisit 目标。结果是收集器一条都收不到，语句切分静默退化成"整批一条"。
        /// 这个 bug 极难察觉：格式化仍然有输出，只是所有语句边界都丢了。
        ///
        /// 用 Visit 的代价是**后序**（先子后父），所以排序时必须自己保证
        /// "同一起点外层在前"，见 Split 里的比较器。
        /// </summary>
        private sealed class StatementCollector : TSqlFragmentVisitor
        {
            public List<TSqlStatement> Statements = new List<TSqlStatement>();

            public override void Visit(TSqlStatement node)
            {
                if (node != null) Statements.Add(node);
                // 后序：框架已遍历完子节点才进来，不要再调 base（会重复遍历）
            }
        }
    }
}
