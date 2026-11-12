using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsSqlPlugin.Formatting;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// 格式化执行器：读编辑器 → 格式化 → 写回。
    ///
    /// 为什么全程用 <see cref="IVsTextView"/> 而不是 ITextView：
    /// 已实测（0.0.10 事故）—— 只要 <c>Microsoft.VisualStudio.Text.Editor.ITextView</c>
    /// 出现在包类型的方法签名 / 字段 / 属性里，SSMS 就会**静默不加载整个包**，
    /// 日志一个字都没有。所以这里只用 TextManager.Interop 这一层的接口。
    ///
    /// 三个关键行为：
    ///   1) 有选区 → 只格式化选区；无选区 → 格式化全文
    ///   2) 写回走 IVsTextLines.ReplaceLines，自动进入 Shell 的撤销栈（Ctrl+Z 可退）
    ///   3) 写回后按新长度重设选区，让用户看到的还是自己刚选中的那段
    /// </summary>
    internal static class FormatExecutor
    {
        public sealed class Outcome
        {
            /// <summary>是否真的改动了文档。</summary>
            public bool Changed;
            /// <summary>面向用户的提示（状态栏 / 弹窗）。</summary>
            public string Message;
            public bool IsError;
        }

        public static Outcome Run(IVsTextView view, FormatOptions options, bool requireSelection = false)
        {
            options = options ?? FormatOptions.Default;

            if (view == null)
            {
                return Fail("没有活动的 SQL 编辑器窗口。");
            }

            // ── 1. 取 buffer ──
            IVsTextLines buffer;
            int hr = view.GetBuffer(out buffer);
            if (hr != 0 || buffer == null)
            {
                return Fail("取不到编辑器缓冲区（hr=0x" + hr.ToString("X8") + "）。");
            }

            // ── 2. 确定范围：选区优先，否则全文 ──
            int startLine, startCol, endLine, endCol;

            bool hasSelection = TryGetSelection(view, out startLine, out startCol, out endLine, out endCol);
            if (requireSelection && !hasSelection)
            {
                return Fail("请先选择要格式化的 SQL。");
            }

            if (!hasSelection)
            {
                int lastLine = 0, lastIndex = 0;
                hr = buffer.GetLastLineIndex(out lastLine, out lastIndex);
                if (hr != 0)
                {
                    return Fail("读取文档末尾失败（hr=0x" + hr.ToString("X8") + "）。");
                }

                startLine = 0;
                startCol = 0;
                endLine = lastLine;
                endCol = lastIndex;
            }

            // ── 3. 取出这段文本 ──
            string original;
            hr = buffer.GetLineText(startLine, startCol, endLine, endCol, out original);
            if (hr != 0)
            {
                return Fail("读取文本失败（hr=0x" + hr.ToString("X8") + "）。");
            }

            if (string.IsNullOrWhiteSpace(original))
            {
                return Fail("没有可格式化的 SQL（范围为空或全是空白）。");
            }

            Log.Info("格式化范围 " + startLine + ":" + startCol + " → " + endLine + ":" + endCol +
                     "，长度 " + original.Length);

            // ── 4. 格式化 ──
            FormatResult result;
            try
            {
                result = new SqlFormatter().Format(original, options);
            }
            catch (Exception ex)
            {
                Log.Error("格式化抛异常", ex);
                return Fail("格式化失败：" + ex.Message);
            }

            if (result == null || result.Text == null)
            {
                return Fail("格式化没有返回结果。");
            }

            foreach (var e in result.Errors)
            {
                Log.Warn("解析告警 " + e.Line + ":" + e.Column + " " + e.Message);
            }

            if (string.Equals(result.Text, original, StringComparison.Ordinal))
            {
                return new Outcome { Changed = false, Message = "已是格式化状态，无需改动。" };
            }

            // ── 5. 写回 ──
            //    ReplaceLines 会走 Shell 的编辑事务，天然进撤销栈。
            //    这里刻意不做 SetSelectedText —— 它不保留撤销边界。
            int newEndLine, newEndCol;
            hr = ReplaceRange(buffer, startLine, startCol, endLine, endCol, result.Text,
                              out newEndLine, out newEndCol);

            if (hr != 0)
            {
                return Fail("写回失败（hr=0x" + hr.ToString("X8") + "）。");
            }

            // ── 6. 恢复选区 ──
            try
            {
                view.SetSelection(startLine, startCol, newEndLine, newEndCol);
            }
            catch (Exception ex)
            {
                Log.Warn("恢复选区失败（不影响结果）: " + ex.Message);
            }

            string note = result.SkippedBatches > 0
                ? "（" + result.SkippedBatches + " 个批次解析失败，已原样保留）"
                : string.Empty;

            return new Outcome
            {
                Changed = true,
                Message = "已格式化" + note + "。",
            };
        }

        /// <summary>
        /// 写回一段范围。
        ///
        /// 用 <c>ReplaceLines</c> 而不是删了再插：
        /// 一次调用 = 一个撤销单元，用户按一次 Ctrl+Z 就能整体回退。
        /// 分两次（先删后插）会产生两个撤销步骤，体验很差。
        ///
        /// ⚠ 第 5 个参数是 <c>IntPtr</c>（COM 的 LPCOLESTR），不是 string。
        /// 必须用 Marshal.StringToCoTaskMemAuto 分配再释放，
        /// 直接传 string 编译报 "无法从 string 转换为 IntPtr"。
        /// </summary>
        private static int ReplaceRange(
            IVsTextLines buffer,
            int startLine, int startCol, int endLine, int endCol,
            string text,
            out int newEndLine, out int newEndCol)
        {
            // 新文本可能改变行数，先算好末尾坐标
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            newEndLine = startLine + (lines.Length - 1);
            newEndCol = lines.Length == 1
                ? startCol + lines[0].Length
                : lines[lines.Length - 1].Length;

            // Auto = 有 BSTR 用 BSTR，否则 ANSI。COM 接口按 LPCOLESTR 解读 Unicode。
            IntPtr pszText = IntPtr.Zero;
            try
            {
                pszText = Marshal.StringToCoTaskMemAuto(text);

                return buffer.ReplaceLines(
                    startLine, startCol,
                    endLine, endCol,
                    pszText,
                    text.Length,
                    null);          // pChangedSpan 允许为 null
            }
            finally
            {
                if (pszText != IntPtr.Zero)
                {
                    try { Marshal.FreeCoTaskMem(pszText); } catch { }
                }
            }
        }

        /// <summary>取选区；没有选区（选区为空）时返回 false。</summary>
        private static bool TryGetSelection(
            IVsTextView view,
            out int startLine, out int startCol, out int endLine, out int endCol)
        {
            startLine = startCol = endLine = endCol = 0;

            try
            {
                int anchorLine, anchorCol, activeLine, activeCol;
                int hr = view.GetSelection(out anchorLine, out anchorCol, out activeLine, out activeCol);
                if (hr != 0) return false;

                // 规范化：GetSelection 返回 anchor→active，可能是反向选择
                bool forward = anchorLine < activeLine ||
                               (anchorLine == activeLine && anchorCol <= activeCol);

                startLine = forward ? anchorLine : activeLine;
                startCol = forward ? anchorCol : activeCol;
                endLine = forward ? activeLine : anchorLine;
                endCol = forward ? activeCol : anchorCol;

                // 空选区（光标没选东西）→ 当作"全文"
                return !(startLine == endLine && startCol == endCol);
            }
            catch (Exception ex)
            {
                Log.Warn("读取选区失败，退化为全文: " + ex.Message);
                return false;
            }
        }

        private static Outcome Fail(string message)
            => new Outcome { Changed = false, IsError = true, Message = message };
    }
}
