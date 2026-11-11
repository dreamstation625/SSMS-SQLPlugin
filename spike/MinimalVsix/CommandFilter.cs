using System;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace SsmsSqlPlugin.Probe
{
    /// <summary>
    /// M0-4 探针：挂在编辑器命令链上的记录器。
    ///
    /// 已实测：按 F5 会产生 GUID_VSStandardCommandSet2K (1496a755-...) 的 0x0067。
    /// 命中时用 IVsTextView 抓取待执行 SQL。
    ///
    /// 注意：这里刻意只用 IVsTextView（TextManager.Interop），
    /// 不引用 Microsoft.VisualStudio.Text.Editor.ITextView ——
    /// 之前把 ITextView 加到包的方法签名里，导致包直接加载失败。
    /// </summary>
    internal sealed class CommandFilter : IOleCommandTarget
    {
        public IOleCommandTarget Next;

        private readonly IVsTextView _vsView;

        private const int MaxRecords = 400;
        private static int _count;

        /// <summary>F5 实测命令：VSStandardCommandSet2K / 0x0067</summary>
        internal static readonly Guid VsStd2K = new Guid("1496a755-94de-11d0-8c3f-00c04fc2aae2");
        internal const uint CmdExecute = 0x0067;

        public CommandFilter(IVsTextView vsView)
        {
            _vsView = vsView;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (Next == null) return 0;
            return Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VsStd2K && nCmdID == CmdExecute)
            {
                DumpExecutingSql();
            }
            else if (_count < MaxRecords)
            {
                _count++;
                ProbeLog.Write("[CMD] group=" + pguidCmdGroup.ToString("D") +
                               " id=0x" + nCmdID.ToString("X4") +
                               " (" + nCmdID + ")");
            }

            if (Next == null) return 0;
            return Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        /// <summary>抓取「要执行的 SQL」：有选区取选区，无选区取全文。</summary>
        private void DumpExecutingSql()
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== [M0-4] 捕获到执行命令（VSStd2K / 0x0067）===");

            if (_vsView == null)
            {
                ProbeLog.Write("  IVsTextView 为 null");
                return;
            }

            try
            {
                string selected = null;
                try
                {
                    int hrSel = _vsView.GetSelectedText(out selected);
                    if (hrSel != 0) selected = null;
                }
                catch (Exception ex)
                {
                    ProbeLog.Write("  读取选区失败: " + ex.Message);
                }

                if (!string.IsNullOrEmpty(selected))
                {
                    ProbeLog.Write("  执行范围 = 选中部分（" + selected.Length + " 字符）");
                    ProbeLog.Write("  SQL: " + Clip(selected));
                    return;
                }

                IVsTextLines lines = null;
                int hrBuf = _vsView.GetBuffer(out lines);
                if (hrBuf != 0 || lines == null)
                {
                    ProbeLog.Write("  GetBuffer 失败 hr=0x" + hrBuf.ToString("X8"));
                    return;
                }

                int lastLine = 0, lastIndex = 0;
                lines.GetLastLineIndex(out lastLine, out lastIndex);

                string all = null;
                int hrText = lines.GetLineText(0, 0, lastLine, lastIndex, out all);
                if (hrText != 0)
                {
                    ProbeLog.Write("  GetLineText 失败 hr=0x" + hrText.ToString("X8"));
                    return;
                }

                ProbeLog.Write("  执行范围 = 全文（" + (all == null ? 0 : all.Length) + " 字符，共 " + (lastLine + 1) + " 行）");
                ProbeLog.Write("  SQL: " + Clip(all));
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  !! dump SQL 异常: " + ex);
            }
        }

        private static string Clip(string s)
        {
            if (s == null) return "null";
            s = s.Replace("\r", "").Replace("\n", " / ");
            return s.Length > 300 ? s.Substring(0, 300) + "...(截断)" : s;
        }
    }
}
