using System;
using System.Collections.Generic;
using System.Text;

namespace SsmsSqlPlugin.Formatting.Doc
{
    /// <summary>
    /// Wadler pretty printer。
    ///
    /// 核心算法：对每个 Group，先判断"压平成一行"后
    ///   从当前位置到该组内**下一个强制换行**为止，是否还放得下。
    /// 放得下 → 组内所有软断点按空格输出；否则 → 全部换行。
    ///
    /// 判定用的是"到下一个 hard line 的距离"，不是整组宽度。
    /// 这样一段很长的语句里，短的小组（比如列清单里的一项）仍然能保持单行。
    /// </summary>
    internal static class PrettyPrinter
    {
        public static string Print(DocNode doc, int maxWidth, int indentSize, string newLine = "\r\n")
        {
            var sb = new StringBuilder();
            var state = new PrintState(sb, maxWidth, indentSize, newLine);
            Render(doc, 0, true, state);
            return sb.ToString();
        }

        private sealed class PrintState
        {
            public PrintState(StringBuilder sb, int maxWidth, int indentSize, string newLine)
            {
                Sb = sb;
                MaxWidth = maxWidth;
                IndentSize = indentSize;
                NewLine = newLine;
            }

            public StringBuilder Sb;
            public int MaxWidth;
            public int IndentSize;
            public string NewLine;
            /// <summary>当前行已经输出的字符数（用于宽度判定）。</summary>
            public int Column;
        }

        /// <summary>
        /// flatten=true 表示当前组被判为"放得下"，组内软断点输出为空格。
        /// </summary>
        private static void Render(DocNode node, int indent, bool flatten, PrintState st)
        {
            switch (node)
            {
                case TextNode t:
                    if (t.Text.Length == 0) return;
                    st.Sb.Append(t.Text);
                    st.Column += VisualWidth(t.Text);
                    return;

                case LineNode l:
                    if (l.IsHard || !flatten)
                    {
                        st.Sb.Append(st.NewLine);
                        st.Sb.Append(' ', indent);
                        st.Column = indent;
                    }
                    else
                    {
                        st.Sb.Append(l.FlattenText);
                        st.Column += l.FlattenText.Length;
                    }
                    return;

                case ConcatNode c:
                    foreach (var p in c.Parts) Render(p, indent, flatten, st);
                    return;

                case IndentNode ind:
                    Render(ind.Child,
                           Math.Max(0, indent + (ind.ExtraIndent == IndentNode.UseDefault ? st.IndentSize : ind.ExtraIndent)),
                           flatten, st);
                    return;

                case GroupNode g:
                    // 决定这一组是否压平
                    bool fits = !g.ForceBreak && Fits(g.Child, st.MaxWidth - st.Column, st.IndentSize);
                    Render(g.Child, indent, fits, st);
                    return;
            }
        }

        /// <summary>
        /// 判断从当前位置起、到本组内第一个强制换行为止，剩余宽度够不够。
        /// 遇到嵌套 Group 时递归其子节点（嵌套组不提前截断，按最坏情况算）。
        /// </summary>
        private static bool Fits(DocNode node, int remaining, int indentSize)
        {
            int used = 0;
            bool done = false;
            return Measure(node, ref used, remaining, ref done);
        }

        /// <summary>
        /// done 一旦为 true 表示"已经遇到硬换行，当前行的宽度结算完毕"，
        /// 后续节点**必须**全部忽略。
        ///
        /// 这是之前的严重 bug：原来遇到 HardLine 只让当前 Measure 返回 true，
        /// 但 ConcatNode 的循环会继续测后面的节点，把硬换行之后的文本也累加进
        /// used，于是：
        ///     WHERE a = 1
        ///         AND b = 2
        ///         AND c = 3
        /// 被判成"WHERE a = 1 AND b = 2 AND c = 3"整行超宽 → WHERE 后白白换行。
        /// </summary>
        private static bool Measure(DocNode node, ref int used, int budget, ref bool done)
        {
            if (done) return true;

            switch (node)
            {
                case TextNode t:
                    used += VisualWidth(t.Text);
                    return used <= budget;

                case LineNode l:
                    if (l.IsHard)
                    {
                        // 硬换行：当前行到此结束，后面不参与判定
                        done = true;
                        return true;
                    }
                    used += l.FlattenText.Length;
                    return used <= budget;

                case ConcatNode c:
                    foreach (var p in c.Parts)
                    {
                        if (!Measure(p, ref used, budget, ref done)) return false;
                        if (done) return true;
                    }
                    return true;

                case IndentNode ind:
                    return Measure(ind.Child, ref used, budget, ref done);

                case GroupNode g:
                    if (g.ForceBreak)
                    {
                        // 强制展开的嵌套组等价于一个硬换行：外层到这里宽度结算完毕
                        done = true;
                        return true;
                    }
                    return Measure(g.Child, ref used, budget, ref done);
            }
            return true;
        }

        /// <summary>
        /// 视觉宽度：CJK 字符算 2 列，其余算 1。
        /// 中文注释、中文标识符不对齐会很难看。
        /// </summary>
        internal static int VisualWidth(string s)
        {
            int w = 0;
            foreach (char ch in s)
            {
                if (ch == '\t') { w += 4; continue; }
                if (IsWide(ch)) w += 2;
                else w += 1;
            }
            return w;
        }

        internal static bool IsWide(char ch)
        {
            // CJK 统一表意文字、全角标点、韩文、日文假名等
            return (ch >= 0x1100 && ch <= 0x115F)      // Hangul Jamo
                || (ch >= 0x2E80 && ch <= 0xA4CF)      // CJK 部首 ~ 彝文
                || (ch >= 0xAC00 && ch <= 0xD7A3)      // Hangul 音节
                || (ch >= 0xF900 && ch <= 0xFAFF)      // CJK 兼容表意
                || (ch >= 0xFE30 && ch <= 0xFE6F)      // CJK 兼容形式
                || (ch >= 0xFF00 && ch <= 0xFF60)      // 全角形式
                || (ch >= 0xFFE0 && ch <= 0xFFE6);
        }
    }
}
