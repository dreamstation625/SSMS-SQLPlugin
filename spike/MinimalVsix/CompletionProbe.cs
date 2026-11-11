using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SsmsSqlPlugin.Probe
{
    /// <summary>
    /// M0-2 探针：验证补全管线能否挂到 SSMS 的 SQL 编辑器上。
    ///
    /// 全部回调只写日志、返回空结果，不改变任何编辑行为。
    /// 判定标准：在查询窗口里打字/按 Ctrl+J 后，日志里出现 [补全] 前缀的记录。
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [ContentType("SQL")]
    [Name("SsmsSqlPlugin.CompletionProbe")]
    internal sealed class ProbeCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public ProbeCompletionSourceProvider()
        {
            ProbeLog.Write("[MEF] ProbeCompletionSourceProvider 构造函数被调用");
        }

        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            string ct = "(unknown)";
            try
            {
                ct = textView.TextBuffer.ContentType.TypeName;
            }
            catch (Exception ex)
            {
                ct = "(读取失败: " + ex.Message + ")";
            }

            ProbeLog.Write("[MEF] GetOrCreate 被调用，ContentType = " + ct);

            // 只在第一次 dump，避免刷屏
            if (!_dumped)
            {
                _dumped = true;
                DumpTextViewProperties(textView);
            }

            return new ProbeCompletionSource();
        }

        private static bool _dumped;

        /// <summary>
        /// M0-3 探测：连接信息很可能挂在 ITextView / ITextBuffer 的属性集合里
        /// （VS 编辑器标准做法）。把 key 和值类型都打出来，就知道该从哪取。
        /// </summary>
        internal static void DumpTextViewProperties(ITextView textView)
        {
            try
            {
                ProbeLog.Write("[M0-3] textView 实现类型 = " + textView.GetType().FullName);
                ProbeLog.Write("[M0-3] 内容类型链:");
                var c = textView.TextBuffer.ContentType;
                int depth = 0;
                while (c != null && depth < 8)
                {
                    ProbeLog.Write("         - " + c.TypeName);
                    Microsoft.VisualStudio.Utilities.IContentType next = null;
                    try
                    {
                        foreach (var b in c.BaseTypes)
                        {
                            next = b;
                            break;
                        }
                    }
                    catch { }
                    c = next;
                    depth++;
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("[M0-3] 读内容类型失败: " + ex.Message);
            }

            DumpProps("[M0-3] textView.Properties", textView.Properties);
            DumpProps("[M0-3] textBuffer.Properties", textView.TextBuffer.Properties);
        }

        internal static void DumpProps(string tag, Microsoft.VisualStudio.Utilities.PropertyCollection props)
        {
            if (props == null)
            {
                ProbeLog.Write(tag + " = null");
                return;
            }

            try
            {
                int n = 0;
                foreach (var kv in props.PropertyList)
                {
                    string key = "";
                    try { key = kv.Key == null ? "null" : kv.Key.ToString(); } catch { key = "(key 读取失败)"; }
                    string vt = "";
                    try { vt = kv.Value == null ? "null" : kv.Value.GetType().FullName; } catch { vt = "(值读取失败)"; }
                    ProbeLog.Write(tag + " [" + key + "] = " + vt);
                    n++;
                    if (n >= 40)
                    {
                        ProbeLog.Write(tag + " ...（超过 40 条已截断）");
                        break;
                    }
                }
                ProbeLog.Write(tag + " 共 " + n + " 条");
            }
            catch (Exception ex)
            {
                ProbeLog.Write(tag + " 枚举失败: " + ex.Message);
            }
        }
    }

    internal sealed class ProbeCompletionSource : IAsyncCompletionSource
    {
        public CompletionStartData InitializeCompletion(
            CompletionTrigger trigger,
            SnapshotPoint location,
            CancellationToken token)
        {
            ProbeLog.Write("[补全] InitializeCompletion reason=" + trigger.Reason +
                           " 位置=" + location.Position);
            return CompletionStartData.ParticipatesInCompletionIfAny;
        }

        public Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            string text = "";
            try
            {
                text = applicableToSpan.GetText();
            }
            catch { }

            ProbeLog.Write("[补全] GetCompletionContextAsync reason=" + trigger.Reason +
                           " 前缀=\"" + text + "\"");
            return Task.FromResult(CompletionContext.Empty);
        }

        public Task<object> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
        {
            ProbeLog.Write("[补全] GetDescriptionAsync item=" + (item == null ? "null" : item.DisplayText));
            return Task.FromResult<object>(null);
        }
    }
}
