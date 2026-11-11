using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// 编辑器命令链过滤器。
    ///
    /// 承担两件事：
    ///   1) <b>快捷键</b>：拦截 Ctrl+K Ctrl+D（VS 传统的「设置文档格式」键序），
    ///      命中后执行 SQL 格式化并吞掉命令（不往下传，避免触发原本的格式化）。
    ///   2) <b>右键菜单保活</b>：编辑器右键菜单（"Code Window"）由 Shell
    ///      在每次弹出时重建，常驻添加经常被冲掉。这里在每次命令经过时
    ///      检查一次是否需要重建 —— 只在失联时补，不做无谓工作。
    ///
    /// ⚠ 这个类**不能**引用 Microsoft.VisualStudio.Text.Editor.ITextView。
    /// 已实测：ITextView 只要出现在包/其可见成员的签名里，SSMS 就会静默不加载
    /// 整个包（0.0.10 事故，日志零输出）。这里全程只用 TextManager.Interop。
    /// </summary>
    internal sealed class EditorCommandFilter : IOleCommandTarget
    {
        public IOleCommandTarget Next;

        private readonly IVsTextView _view;
        private readonly Func<IVsTextView, bool> _formatCallback;

        // ── 右键菜单保活节流 ──
        private DateTime _lastMenuCheckUtc = DateTime.MinValue;
        private static readonly TimeSpan MenuCheckInterval = TimeSpan.FromSeconds(2);

        private readonly Action _ensureContextMenu;

        public EditorCommandFilter(
            IVsTextView view,
            Func<IVsTextView, bool> formatCallback,
            Action ensureContextMenu)
        {
            _view = view;
            _formatCallback = formatCallback;
            _ensureContextMenu = ensureContextMenu;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            try
            {
                // 让 Ctrl+K / Ctrl+D 保持"可能被处理"的状态，
                // 否则 Shell 可能在键序走完前就把它交给别人。
                if (pguidCmdGroup == VsConstants.GUID_VSStandardCommandSet97 && prgCmds != null)
                {
                    for (int i = 0; i < cCmds && i < prgCmds.Length; i++)
                    {
                        uint id = prgCmds[i].cmdID;
                        if (id == CmdFormatDocument || id == CmdFormatSelection || id == CmdCompleteWord)
                        {
                            prgCmds[i].cmdf = OleCmdfSupported | OleCmdfEnabled;
                        }
                    }
                }
            }
            catch { }

            if (Next == null) return OleCmdErrNotSupported;
            return Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            try
            {
                if (TryHandleChord(pguidCmdGroup, nCmdID))
                {
                    return S_OK;    // 已处理，不再往下传
                }

                MaybeEnsureContextMenu();
            }
            catch (Exception ex)
            {
                Log.Error("命令过滤器异常", ex);
            }

            if (Next == null) return OleCmdErrNotSupported;
            return Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        /// <summary>
        /// 处理快捷键。
        ///
        /// 两条路径都留，因为 SSMS 里"Ctrl+K Ctrl+D"到底派发成什么命令
        /// 取决于用户的键绑定设置：
        ///
        ///   路径一（主）：看到 FormatDocument(0x0002) / FormatSelection(0x0003)
        ///       就直接替换成我们的格式化。
        ///       VS 默认的 Ctrl+K Ctrl+D 最终就是 FormatDocument，
        ///       所以要先用这个 —— 简单可靠。
        ///
        ///   路径二（兜底）：如果用户改过键绑定、或者 SSMS 没绑定，
        ///       上面那条命令根本不会来。这时靠 EditorKeyChord 的按键捕获。
        ///       见 InstallKeyChord / EditorKeyChord.cs。
        /// </summary>
        private bool TryHandleChord(Guid group, uint cmdId)
        {
            if (group != VsConstants.GUID_VSStandardCommandSet97) return false;

            // 主路径：Ctrl+K Ctrl+D 最终派发 FormatDocument
            if (cmdId == CmdFormatDocument || cmdId == CmdFormatSelection)
            {
                if (!EditorLocator.IsSqlView(_view))
                {
                    Log.Info("格式化快捷键命中，但不是 SQL 编辑器，交回默认处理");
                    return false;
                }


                Log.Info("快捷键命中: " + (cmdId == CmdFormatDocument ? "FormatDocument" : "FormatSelection"));
                _formatCallback?.Invoke(_view);
                return true;
            }

            return false;
        }

        /// <summary>编辑器右键菜单被 Shell 重建后需要重新注入，这里低频兜底检查。</summary>
        private void MaybeEnsureContextMenu()
        {
            if (_ensureContextMenu == null) return;

            var now = DateTime.UtcNow;
            if (now - _lastMenuCheckUtc < MenuCheckInterval) return;
            _lastMenuCheckUtc = now;

            try { _ensureContextMenu(); }
            catch (Exception ex) { Log.Warn("右键菜单保活检查失败: " + ex.Message); }
        }

        // ── 常量 ──

        private const int S_OK = 0;
        private const int OleCmdErrNotSupported = unchecked((int)0x80040100);
        private const uint OleCmdfSupported = 0x1;
        private const uint OleCmdfEnabled = 0x2;

        /// <summary>CMDSETID_StandardCommandSet97 / FormatDocument</summary>
        private const uint CmdFormatDocument = 0x0002;
        /// <summary>FormatSelection</summary>
        private const uint CmdFormatSelection = 0x0003;
        /// <summary>CompleteWord（占位，保持支持状态）</summary>
        private const uint CmdCompleteWord = 0x006B;

        /// <summary>当前视图（供外部取用）。</summary>
        public IVsTextView View => _view;
    }

    /// <summary>VS 的 GUID 常量（避免引 Microsoft.VisualStudio.Shell.Immutable 的额外依赖）。</summary>
    internal static class VsConstants
    {
        /// <summary>标准命令集 97（VSStandardCommandSet97）。</summary>
        public static readonly Guid GUID_VSStandardCommandSet97 =
            new Guid("5efc7975-14bc-11cf-9b2b-00aa00573819");
    }
}
