#if SSMS20
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsSqlPlugin.Core.Completion;
using SsmsSqlPlugin.Core.Metadata;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Localization;

namespace SsmsSqlPlugin.Completion.Legacy
{
    /// <summary>
    /// SSMS 20 的 IsolatedShell 不组合文件部署扩展的 MEF 导出，
    /// 因此绕开 ICompletionSource，直接把候选交给 IVsTextView 的旧补全 UI。
    /// </summary>
    internal static class LegacyCompletionHook
    {
        private static readonly HashSet<int> Attached = new HashSet<int>();
        private static readonly SqlCompletionEngine Engine = new SqlCompletionEngine();
        private static bool _metadataHintShown;

        public static void Attach(IVsTextView view)
        {
            if (view == null) return;

            int key = RuntimeHelpers.GetHashCode(view);
            lock (Attached)
            {
                if (!Attached.Add(key)) return;
            }

            try
            {
                var provider = ServiceProviderHolder.Provider;
                var componentModel = provider?.GetService(typeof(SComponentModel)) as IComponentModel;
                var adapter = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
                var wpf = adapter?.GetWpfTextView(view);
                if (wpf == null)
                {
                    Log.Warn("[补全] 旧 CompletionSet：拿不到 WPF 视图");
                    return;
                }

                new Hook(view, wpf);
                Log.Info("[补全] 旧 CompletionSet 钩子已挂到 SQL 编辑器");            }
            catch (Exception ex)
            {
                Log.Warn("[补全] 旧 CompletionSet 挂载失败: " + ex.Message);
            }
        }

        private sealed class Hook
        {
            private readonly IVsTextView _view;
            private readonly IWpfTextView _wpf;
            private LegacyCompletionSet _current;

            public Hook(IVsTextView view, IWpfTextView wpf)
            {
                _view = view;
                _wpf = wpf;
                _wpf.TextBuffer.Changed += OnBufferChanged;
                _wpf.Closed += OnClosed;
            }

            private void OnClosed(object sender, EventArgs e)
            {
                try { _wpf.TextBuffer.Changed -= OnBufferChanged; } catch { }
                try { _view.UpdateCompletionStatus(null, 0); } catch { }
            }

            private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
            {
                if (e == null || e.Changes == null) return;

                bool typed = false;
                foreach (var change in e.Changes)
                {
                    string text = change.NewText;
                    if (string.IsNullOrEmpty(text) || text.Length != 1) continue;
                    char ch = text[0];
                    if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
                    {
                        typed = true;
                        break;
                    }
                }
                if (!typed) return;

                try
                {
                    var snapshot = _wpf.TextBuffer.CurrentSnapshot;
                    int caret = _wpf.Caret.Position.BufferPosition.Position;
                    // Changed 触发时光标可能尚未前移，插入字符时以变更末端为准，
                    // 否则补全范围会落后一个字符。
                    if (e.Changes.Count > 0)
                    {
                        var last = e.Changes[e.Changes.Count - 1];
                        if (last.NewText != null && last.NewText.Length == 1)
                        {
                            int changeEnd = last.NewPosition + last.NewText.Length;
                            if (changeEnd != caret && changeEnd <= snapshot.Length)
                                caret = changeEnd;
                        }
                    }
                    var runtime = CompletionRuntime.Current;
                    MetadataSnapshot metadata = runtime != null
                        ? runtime.GetSnapshot()
                        : MetadataSnapshot.Empty();

                    var options = new CompletionOptions
                    {
                        UppercaseKeywords = FormatOptionsStore.Current.UppercaseKeywords,
                        MaxItems = 200,
                    };

                    var result = Engine.GetCompletions(snapshot.GetText(), caret, metadata, options);

                    var line = snapshot.GetLineFromPosition(result.PrefixStart);
                    int startCol = result.PrefixStart - line.Start.Position;
                    int endCol = startCol + result.PrefixLength;

                    uint flags = (uint)(UpdateCompletionFlags.UCS_NAMESCHANGED |
                                        UpdateCompletionFlags.UCS_EXTENTCHANGED |
                                        UpdateCompletionFlags.UCS_FLAGSCHANGED);

                    if (result.Items.Count == 0)
                    {
                        if (!_metadataHintShown &&
                            metadata.Objects.Count == 0 &&
                            string.Equals(result.Slot, "table", StringComparison.Ordinal))
                        {
                            _metadataHintShown = true;
                            Toast.Show(Loc.T(
                                "SSMS 20: select a database in Object Explorer to load table metadata."), true);
                        }

                        // 不能用 UpdateCompletionStatus(null, 0) 强制关闭：
                        // SSMS 20 的内建 CompletionSession 之后还会收到文本变更并 Match，
                        // 已 Dismiss 的会话会抛 "Cannot Match. The session is dismissed."。
                        // 用空候选集更新，让编辑器自己收起列表。
                        var emptySet = new LegacyCompletionSet(
                            new CompletionCandidate[0], line.LineNumber, startCol, endCol);
                        _current = emptySet;
                        int hrEmpty = _view.UpdateCompletionStatus(emptySet, flags);
                        Log.Info("[补全] 旧 CompletionSet 无候选 前缀=\"" + result.Prefix +
                                 "\" 槽位=" + result.Slot +
                                 " hr=0x" + hrEmpty.ToString("X8"));
                        return;
                    }

                    var set = new LegacyCompletionSet(
                        result.Items, line.LineNumber, startCol, endCol);
                    _current = set;

                    int hr = _view.UpdateCompletionStatus(set, flags);
                    Log.Info("[补全] 旧 CompletionSet 候选=" + result.Items.Count +
                             " 前缀=\"" + result.Prefix + "\"" +
                             " 槽位=" + result.Slot +
                             " hr=0x" + hr.ToString("X8"));
                }
                catch (Exception ex)
                {
                    Log.Error("[补全] 旧 CompletionSet 更新异常", ex);
                }
            }
        }
    }

    [ComVisible(true)]
    internal sealed class LegacyCompletionSet : IVsCompletionSet
    {
        private readonly IList<CompletionCandidate> _items;
        private readonly int _line;
        private readonly int _startColumn;
        private readonly int _endColumn;

        public LegacyCompletionSet(IList<CompletionCandidate> items,
            int line, int startColumn, int endColumn)
        {
            _items = items;
            _line = line;
            _startColumn = startColumn;
            _endColumn = endColumn;
        }

        private static string InsertText(CompletionCandidate item)
        {
            return string.IsNullOrEmpty(item.InsertText) ? item.DisplayText : item.InsertText;
        }

        public uint GetFlags()
        {
            return (uint)(UpdateCompletionFlags.CSF_CUSTOMMATCHING |
                          UpdateCompletionFlags.CSF_HAVEDESCRIPTIONS |
                          UpdateCompletionFlags.CSF_INITIALEXTENTKNOWN |
                          UpdateCompletionFlags.CSF_NOCASEMATCHING);
        }

        public int GetCount() => _items.Count;

        public int GetDisplayText(int iIndex, out string ppszText, int[] piGlyph)
        {
            ppszText = iIndex >= 0 && iIndex < _items.Count
                ? InsertText(_items[iIndex])
                : string.Empty;
            return 0;
        }

        public int GetImageList(out IntPtr phImages)
        {
            phImages = IntPtr.Zero;
            return 0;
        }

        public int GetDescriptionText(int iIndex, out string pbstrDescription)
        {
            pbstrDescription = iIndex >= 0 && iIndex < _items.Count
                ? _items[iIndex].Description
                : null;
            return 0;
        }

        public int GetInitialExtent(out int piLine, out int piStartCol, out int piEndCol)
        {
            piLine = _line;
            piStartCol = _startColumn;
            piEndCol = _endColumn;
            return 0;
        }

        public int GetBestMatch(string pszSoFar, int iLength, out int piIndex, out uint pdwFlags)
        {
            piIndex = 0;
            pdwFlags = 0;
            if (string.IsNullOrEmpty(pszSoFar) || _items.Count == 0) return 0;

            string prefix = pszSoFar;
            if (iLength > 0 && iLength <= prefix.Length)
                prefix = prefix.Substring(0, iLength);

            for (int i = 0; i < _items.Count; i++)
            {
                if (InsertText(_items[i]).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    piIndex = i;
                    pdwFlags = (uint)UpdateCompletionFlags.GBM_SELECT;
                    Log.Info("[补全] 旧 CompletionSet GetBestMatch 前缀=\"" + prefix + "\" -> " + piIndex);
                    return 0;
                }
            }
            Log.Info("[补全] 旧 CompletionSet GetBestMatch 前缀=\"" + prefix + "\" -> 未匹配");
            return 0;
        }

        public int OnCommit(string pszSoFar, int iIndex, int fSelected,
            ushort cCommit, out string pbstrCompleteWord)
        {
            pbstrCompleteWord = iIndex >= 0 && iIndex < _items.Count
                ? InsertText(_items[iIndex])
                : null;
            Log.Info("[补全] 旧 CompletionSet OnCommit soFar=\"" + pszSoFar + "\" idx=" + iIndex +
                     " selected=" + fSelected + " word=\"" + pbstrCompleteWord + "\"");
            return 0;
        }

        public void Dismiss()
        {
        }
    }
}
#endif
