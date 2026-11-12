using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SsmsSqlPlugin.Core.Completion;
using SsmsSqlPlugin.Core.Metadata;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// Tab 提交时只替换光标前的当前单词，不依赖会话的历史 applicable span。
    /// 这是"补全 FROM 把前面的 * 一起吞掉"的兜底。
    /// </summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [ContentType("SQL")]
    [Name("SsmsSqlPlugin.CommitManager")]
    internal sealed class SqlCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView)
        {
            return new SqlCommitManager();
        }
    }

    internal sealed class SqlCommitManager : IAsyncCompletionCommitManager
    {
        private static readonly char[] CommitCharacters = { '\t' };

        public IEnumerable<char> PotentialCommitCharacters => CommitCharacters;

        public bool ShouldCommitCompletion(char typedChar, SnapshotPoint location, CancellationToken token)
        {
            return typedChar == '\t';
        }

        public bool ShouldCommitCompletion(
            IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token)
        {
            return typedChar == '\t';
        }

        public CommitResult TryCommit(
            IAsyncCompletionSession session,
            ITextBuffer buffer,
            CompletionItem item,
            char typedChar,
            CancellationToken token)
        {
            if (typedChar != '\t') return CommitResult.Unhandled;
            return Commit(session.TextView, buffer, item);
        }

        public CommitResult TryCommit(
            ITextView textView,
            ITextBuffer buffer,
            CompletionItem item,
            ITrackingSpan applicableToSpan,
            char typedChar,
            CancellationToken token)
        {
            if (typedChar != '\t') return CommitResult.Unhandled;
            return Commit(textView, buffer, item);
        }

        /// <summary>
        /// 列表可能是旧上下文留下的（例如 SELECT 还停在 select 的列表里）。
        /// 选中项不匹配当前单词时，用引擎按当前光标重新算一次，避免提交成 SELECT。
        /// </summary>
        private static string ResolveInsertText(
            CompletionItem item,
            ITextSnapshot snapshot,
            int caret,
            string prefix)
        {
            string selected = item.InsertText ?? item.DisplayText;
            if (string.IsNullOrEmpty(selected) || string.IsNullOrEmpty(prefix))
                return selected;

            try
            {
                if (CompletionScorer.Matches(item.FilterText ?? item.DisplayText, prefix))
                    return selected;

                var runtime = CompletionRuntime.Current;
                var metadata = runtime != null ? runtime.GetSnapshot() : MetadataSnapshot.Empty();

                var options = new CompletionOptions
                {
                    UppercaseKeywords = FormatOptionsStore.Current.UppercaseKeywords,
                    MaxItems = 200,
                };

                var result = new SqlCompletionEngine().GetCompletions(
                    snapshot.GetText(), caret, metadata, options);

                if (result.Items.Count > 0)
                {
                    var best = result.Items[0];
                    Log.Info("[补全] 提交时重算: 选中 " + item.DisplayText +
                             " 不匹配前缀 \"" + prefix + "\"，改用 " + best.DisplayText);
                    return best.InsertText ?? best.DisplayText;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[补全] 提交时重算失败: " + ex.Message);
            }

            return selected;
        }

        private static CommitResult Commit(ITextView view, ITextBuffer buffer, CompletionItem item)
        {
            try
            {
                if (view == null || buffer == null || item == null) return CommitResult.Unhandled;

                var caret = view.Caret.Position.BufferPosition;
                var snapshot = caret.Snapshot;

                int start = caret.Position;
                while (start > 0)
                {
                    char c = snapshot[start - 1];
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$') start--;
                    else break;
                }

                int length = caret.Position - start;
                string replaced = length > 0 ? snapshot.GetText(start, length) : string.Empty;

                string text = ResolveInsertText(item, snapshot, caret.Position, replaced);
                if (string.IsNullOrEmpty(text)) return CommitResult.Unhandled;

                if (length > 0) buffer.Replace(new Span(start, length), text);
                else buffer.Insert(caret.Position, text);

                var current = buffer.CurrentSnapshot;
                int target = Math.Min(start + text.Length, current.Length);
                view.Caret.MoveTo(new SnapshotPoint(current, target));

                Log.Info("[补全] 自定义提交: \"" + replaced + "\" -> \"" + text + "\"");
                return CommitResult.Handled;
            }
            catch (Exception ex)
            {
                Log.Error("[补全] 自定义提交失败", ex);
                return CommitResult.Unhandled;
            }
        }
    }
}
