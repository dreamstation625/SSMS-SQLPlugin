#if SSMS20
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using SsmsSqlPlugin.Core.Completion;
using SsmsSqlPlugin.Core.Metadata;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.Infrastructure;
using VsCompletion = Microsoft.VisualStudio.Language.Intellisense.Completion;

namespace SsmsSqlPlugin.Completion.Legacy
{
    /// <summary>
    /// SSMS 20 使用 VS 2019 的同步补全管线；候选仍由 Core 生成。
    /// </summary>
    [Export(typeof(ICompletionSourceProvider))]
    [ContentType("SQL")]
    [Name("SsmsSqlPlugin.LegacyCompletion")]
    internal sealed class LegacyCompletionSourceProvider : ICompletionSourceProvider
    {
        private static readonly HashSet<int> LoggedBuffers = new HashSet<int>();

        public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer)
        {
            if (textBuffer == null) return null;

            int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(textBuffer);
            lock (LoggedBuffers)
            {
                if (LoggedBuffers.Add(key))
                {
                    Log.Info("[补全] 旧管线已创建 source ContentType=" +
                             textBuffer.ContentType.TypeName);
                }
            }

            try { KeywordAutoCaseHook.Attach(textBuffer); }
            catch (Exception ex) { Log.Warn("[编辑] 挂自动大写失败: " + ex.Message); }

            return new LegacyCompletionSource(textBuffer);
        }
    }

    internal sealed class LegacyCompletionSource : ICompletionSource
    {
        private static readonly SqlCompletionEngine Engine = new SqlCompletionEngine();

        private readonly ITextBuffer _buffer;

        public LegacyCompletionSource(ITextBuffer buffer)
        {
            _buffer = buffer;
        }

        public void AugmentCompletionSession(ICompletionSession session,
            IList<CompletionSet> completionSets)
        {
            if (session == null || completionSets == null || _buffer == null) return;

            try
            {
                var snapshot = _buffer.CurrentSnapshot;
                var triggerPoint = session.GetTriggerPoint(snapshot);
                if (!triggerPoint.HasValue) return;

                string sql = snapshot.GetText();
                int caret = triggerPoint.Value.Position;

                var runtime = CompletionRuntime.Current;
                MetadataSnapshot metadata = runtime != null
                    ? runtime.GetSnapshot()
                    : MetadataSnapshot.Empty();

                var options = new CompletionOptions
                {
                    UppercaseKeywords = FormatOptionsStore.Current.UppercaseKeywords,
                    MaxItems = 200,
                };

                var result = Engine.GetCompletions(sql, caret, metadata, options);
                if (result.Items.Count == 0) return;

                var applicable = snapshot.CreateTrackingSpan(
                    result.PrefixStart, result.PrefixLength, SpanTrackingMode.EdgeInclusive);

                var completions = new List<VsCompletion>(result.Items.Count);
                foreach (var candidate in result.Items)
                {
                    string insert = string.IsNullOrEmpty(candidate.InsertText)
                        ? candidate.DisplayText
                        : candidate.InsertText;

                    completions.Add(new VsCompletion(
                        candidate.DisplayText,
                        insert,
                        candidate.Description,
                        null,
                        null));
                }

                completionSets.Insert(0, new CompletionSet(
                    "SsmsSqlPlugin",
                    "SSMS-SQLPlugin",
                    applicable,
                    completions,
                    null));

                Log.Info("[补全] 旧管线候选=" + completions.Count +
                         " 前缀=\"" + result.Prefix + "\"" +
                         " 槽位=" + result.Slot +
                         (string.IsNullOrEmpty(result.Error) ? "" : " 错误=" + result.Error));
            }
            catch (Exception ex)
            {
                Log.Error("[补全] 旧管线生成候选异常", ex);
            }
        }

        public void Dispose()
        {
        }
    }
}
#endif
