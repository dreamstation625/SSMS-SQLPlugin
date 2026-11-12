using System;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Text;
using SsmsSqlPlugin.Formatting.Tokens;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// 输入 SQL 关键字后打空格时自动转大写。
    /// 由补全 provider 在拿到 ITextView 时挂到缓冲区，避免依赖额外的 MEF 监听器。
    /// 只处理真正的关键字；字符串、注释、方括号/双引号标识符里不改。
    /// 可在设置窗口关闭。
    /// </summary>
    internal static class KeywordAutoCaseHook
    {
        private static readonly ConditionalWeakTable<ITextBuffer, BufferHook> Hooks =
            new ConditionalWeakTable<ITextBuffer, BufferHook>();

        public static void Attach(ITextBuffer buffer)
        {
            if (buffer == null) return;

            try
            {
                Hooks.GetValue(buffer, b =>
                {
                    string contentType = "(unknown)";
                    try { contentType = b.ContentType.TypeName; } catch { }
                    Log.Info("[编辑] 关键字自动大写已挂: " + contentType);
                    return new BufferHook(b);
                });
            }
            catch (Exception ex)
            {
                Log.Warn("[编辑] 挂关键字自动大写失败: " + ex.Message);
            }
        }

        private sealed class BufferHook
        {
            private readonly ITextBuffer _buffer;
            private bool _applying;

            public BufferHook(ITextBuffer buffer)
            {
                _buffer = buffer;
                _buffer.Changed += OnChanged;
            }

            private void OnChanged(object sender, TextContentChangedEventArgs e)
            {
                if (_applying) return;
                if (e.Changes.Count != 1) return;

                var change = e.Changes[0];
                if (change.OldLength != 0 || change.NewText != " ") return;

                if (!FormatOptionsStore.Current.AutoUppercaseKeywordOnSpace) return;

                var snapshot = e.After;
                int space = change.NewPosition;
                if (space <= 0) return;

                int end = space;
                int start = end;
                while (start > 0 && IsWordChar(snapshot[start - 1])) start--;
                if (start == end) return;

                string word = snapshot.GetText(start, end - start);
                string upper = word.ToUpperInvariant();
                if (word == upper) return;
                if (!SqlKeywords.IsKeyword(upper)) return;

                // .select 是限定名的一部分，不是关键字。
                if (start > 0 && snapshot[start - 1] == '.') return;

                if (IsInLiteralOrComment(snapshot, start)) return;

                try
                {
                    _applying = true;
                    using (var edit = _buffer.CreateEdit())
                    {
                        edit.Replace(new Span(start, end - start), upper);
                        edit.Apply();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[编辑] 关键字自动大写失败: " + ex.Message);
                }
                finally
                {
                    _applying = false;
                }
            }
        }

        private static bool IsWordChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        /// <summary>
        /// 从缓冲区开头扫描到 position，判断是否处在字符串/注释/括号标识符中。
        /// 超过 2M 字符直接跳过，避免超大脚本输入卡顿或误判。
        /// </summary>
        private static bool IsInLiteralOrComment(ITextSnapshot snapshot, int position)
        {
            const int MaxScan = 2_000_000;
            if (position > MaxScan) return true;

            bool inString = false;
            bool inBracket = false;
            bool inDoubleQuote = false;
            bool inBlockComment = false;
            bool inLineComment = false;

            for (int i = 0; i < position; i++)
            {
                char c = snapshot[i];
                char next = i + 1 < position ? snapshot[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }

                if (inString)
                {
                    if (c == '\'')
                    {
                        if (next == '\'') i++;
                        else inString = false;
                    }
                    continue;
                }

                if (inBracket)
                {
                    if (c == ']')
                    {
                        if (next == ']') i++;
                        else inBracket = false;
                    }
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (c == '"')
                    {
                        if (next == '"') i++;
                        else inDoubleQuote = false;
                    }
                    continue;
                }

                if (c == '-' && next == '-') { inLineComment = true; i++; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c == '\'') { inString = true; continue; }
                if (c == '[') { inBracket = true; continue; }
                if (c == '"') { inDoubleQuote = true; continue; }
            }

            return inString || inBracket || inDoubleQuote || inBlockComment || inLineComment;
        }
    }
}
