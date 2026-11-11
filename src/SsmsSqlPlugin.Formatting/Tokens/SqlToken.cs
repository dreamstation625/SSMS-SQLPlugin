using System;
using System.Collections.Generic;

namespace SsmsSqlPlugin.Formatting.Tokens
{
    /// <summary>
    /// 排版用的 token。由 ScriptDom 的 ISqlToken 转来，做了三件事：
    /// 1. 丢掉纯空白（位置改由排版器决定）
    /// 2. 把注释单独标记，排版时原样保留
    /// 3. 补上层级信息：当前在几层括号内
    /// </summary>
    internal enum SqlTokenKind
    {
        Keyword,
        Identifier,
        Literal,
        Operator,
        Punctuation,
        /// <summary>分号，单独一类，因为它标识语句结束。</summary>
        Semicolon,
        Comment,
        /// <summary>GO 批次分隔符（不是 token 流的一部分，由切批逻辑插入）</summary>
        BatchSeparator,
    }

    internal sealed class SqlToken
    {
        public SqlTokenKind Kind;
        /// <summary>原始文本，排版时除关键字大小写外不改动。</summary>
        public string Text;
        /// <summary>规范化后的关键字大写形式，用于规则匹配（非关键字为 null）。</summary>
        public string KeywordUpper;
        public int Line;
        public int Column;
        /// <summary>原始偏移，用于出错时的位置提示。</summary>
        public int Offset;

        /// <summary>
        /// 在 ScriptDom **原始 token 流**（含空白）里的索引。
        ///
        /// 必须保留：AST 节点的 FirstTokenIndex / LastTokenIndex 就是按这个流编号的，
        /// 一旦我们丢了空白 token，索引就和排版用的 token 列表错位，
        /// 所有语句边界都会指错位置。
        /// </summary>
        public int RawIndex;

        /// <summary>是否为单行注释（--）。</summary>
        public bool IsLineComment;
        /// <summary>是否为块注释（/* */）。</summary>
        public bool IsBlockComment;

        public override string ToString()
            => Kind + "[" + (KeywordUpper ?? Text) + "]";

        /// <summary>是否匹配给定关键字（忽略大小写）。</summary>
        public bool Is(string keyword)
            => KeywordUpper != null && string.Equals(KeywordUpper, keyword, StringComparison.OrdinalIgnoreCase);

        /// <summary>是否匹配任意一个关键字。</summary>
        public bool IsAny(params string[] keywords)
        {
            if (KeywordUpper == null) return false;
            foreach (var k in keywords)
            {
                if (string.Equals(KeywordUpper, k, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>是否为给定文本（用于标点，如 "," "(" ")"）。</summary>
        public bool IsText(string text)
            => string.Equals(Text, text, StringComparison.Ordinal);
    }
}
