using System;
using System.Collections.Generic;
using System.Text;

namespace SsmsSqlPlugin.Formatting.Doc
{
    /// <summary>
    /// Document IR —— 排版中间表示。
    ///
    /// 只用四种节点：
    ///   Text   叶子，一段不可再分的文本
    ///   Line   可能变成空格，也可能变成换行+缩进（由外层 Group 的放得下/放不下决定）
    ///   Concat 水平拼接
    ///   Indent 增加缩进层级
    ///   Group  分组：尝试压成一行；放不下则组内所有 Line 都换行
    ///
    /// 这是 Wadler 的 "A prettier printer" 的经典模型，改动点：
    /// - Line 支持 IsHard（强制换行，比如语句之间），Hard 不吃判定逻辑
    /// - 支持 LineKind 区分普通断点与"逗号后断点"（两者缩进语义不同）
    /// </summary>
    internal abstract class DocNode
    {
    }

    /// <summary>不可再分的文本。</summary>
    internal sealed class TextNode : DocNode
    {
        public TextNode(string text) { Text = text ?? string.Empty; }
        public string Text { get; }
    }

    /// <summary>断点：放得下 → 空格（或空串）；放不下 → 换行 + 缩进。</summary>
    internal sealed class LineNode : DocNode
    {
        public LineNode(string flattenText, bool isHard)
        {
            FlattenText = flattenText ?? string.Empty;
            IsHard = isHard;
        }

        /// <summary>压平后显示的文本，通常是 " " 或 ""。</summary>
        public string FlattenText { get; }

        /// <summary>true = 无条件换行（语句分隔、块注释前后、强制换行点）。</summary>
        public bool IsHard { get; }

        public static readonly LineNode Space = new LineNode(" ", false);
        public static readonly LineNode Soft = new LineNode("", false);
        public static readonly LineNode Hard = new LineNode("", true);
    }

    /// <summary>水平拼接。</summary>
    internal sealed class ConcatNode : DocNode
    {
        public ConcatNode(IReadOnlyList<DocNode> parts) { Parts = parts; }
        public IReadOnlyList<DocNode> Parts { get; }
    }

    /// <summary>缩进块：内部换行时额外增加 N 个缩进。</summary>
    internal sealed class IndentNode : DocNode
    {
        /// <summary>用 IndentSize 的默认值（而不是固定空格数）。</summary>
        public const int UseDefault = int.MinValue;

        public IndentNode(DocNode child, int extraIndent)
        {
            Child = child;
            ExtraIndent = extraIndent;
        }

        public DocNode Child { get; }

        /// <summary>
        /// 额外空格数。允许负数（往回缩），用于让子查询的收括号对齐到子句行首。
        /// 等于 <see cref="UseDefault"/> 时取 IndentSize。
        /// </summary>
        public int ExtraIndent { get; }
    }

    /// <summary>分组：整组尝试单行，放不下才断。</summary>
    internal sealed class GroupNode : DocNode
    {
        public GroupNode(DocNode child, bool forceBreak)
        {
            Child = child;
            ForceBreak = forceBreak;
        }

        public DocNode Child { get; }

        /// <summary>
        /// true = 不做宽度判定，直接展开。
        /// 用于"把 SELECT 列强制每行一个"这种与行宽无关的风格要求。
        /// </summary>
        public bool ForceBreak { get; }
    }

    /// <summary>构造 Doc 的便捷入口。</summary>
    internal static class DocFactory
    {
        public static DocNode Text(string s) => new TextNode(s);

        /// <summary>断点，压平为一个空格。</summary>
        public static readonly DocNode Line = LineNode.Space;

        /// <summary>断点，压平时不产生字符（用于括号内侧、逗号前）。</summary>
        public static readonly DocNode SoftLine = LineNode.Soft;

        /// <summary>强制换行。</summary>
        public static readonly DocNode HardLine = LineNode.Hard;

        public static DocNode Concat(params DocNode[] parts)
        {
            var list = new List<DocNode>(parts.Length);
            foreach (var p in parts)
            {
                if (p == null) continue;
                if (p is ConcatNode c) list.AddRange(c.Parts);
                else list.Add(p);
            }
            return new ConcatNode(list);
        }

        public static DocNode Concat(IEnumerable<DocNode> parts)
        {
            var list = new List<DocNode>();
            foreach (var p in parts)
            {
                if (p == null) continue;
                if (p is ConcatNode c) list.AddRange(c.Parts);
                else list.Add(p);
            }
            return new ConcatNode(list);
        }

        public static DocNode Indent(DocNode child, int extra = IndentNode.UseDefault)
            => new IndentNode(child, extra);

        public static DocNode Group(DocNode child) => new GroupNode(child, false);

        /// <summary>强制展开的组：组内软断点一律换行，不看行宽。</summary>
        public static DocNode GroupBreak(DocNode child) => new GroupNode(child, true);

        /// <summary>空文档。</summary>
        public static readonly DocNode Nil = new ConcatNode(Array.Empty<DocNode>());

        /// <summary>把若干文档用分隔符连接。</summary>
        public static DocNode Join(DocNode separator, IEnumerable<DocNode> items)
        {
            var list = new List<DocNode>();
            bool first = true;
            foreach (var it in items)
            {
                if (!first) list.Add(separator);
                list.Add(it);
                first = false;
            }
            return Concat(list);
        }
    }
}
