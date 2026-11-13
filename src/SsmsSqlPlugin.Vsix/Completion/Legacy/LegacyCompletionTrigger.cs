#if SSMS20
using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion.Legacy
{
    /// <summary>
    /// SSMS 20 不会自动为 MEF 补全源启动会话；输入时主动调用旧 completion broker。
    /// </summary>
    [Export(typeof(ITextViewCreationListener))]
    [Name("SsmsSqlPlugin.LegacyCompletionTrigger")]
    internal sealed class LegacyCompletionTrigger : ITextViewCreationListener
    {
        [Import]
        internal ICompletionBroker CompletionBroker { get; set; }

        public void TextViewCreated(ITextView textView)
        {
            if (textView == null || CompletionBroker == null) return;

            string contentType = "(unknown)";
            try { contentType = textView.TextBuffer.ContentType.TypeName; } catch { }
            Log.Info("[补全] TextViewCreated ContentType=" + contentType);

            // SSMS 20 的 SQL 编辑器内容类型以实际探测为准，兼容 SQL / T-SQL 命名。
            if (contentType.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) < 0) return;

            EventHandler<TextContentChangedEventArgs> handler = null;
            handler = (sender, args) =>
            {
                try
                {
                    if (args == null || args.Changes == null) return;

                    bool typed = false;
                    foreach (var change in args.Changes)
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

                    if (!typed || CompletionBroker.IsCompletionActive(textView)) return;
                    CompletionBroker.TriggerCompletion(textView);
                }
                catch (Exception ex)
                {
                    Log.Warn("[补全] 旧管线触发失败: " + ex.Message);
                }
            };

            textView.TextBuffer.Changed += handler;
            textView.Closed += (sender, args) =>
            {
                try { textView.TextBuffer.Changed -= handler; } catch { }
            };

            Log.Info("[补全] 旧管线监听器已挂到 SQL 编辑器");
        }
    }
}
#endif
