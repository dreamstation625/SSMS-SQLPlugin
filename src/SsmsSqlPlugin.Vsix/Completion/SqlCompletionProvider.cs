using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SsmsSqlPlugin.Core.Completion;
using SsmsSqlPlugin.Core.Metadata;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// SSMS 22 异步补全接线。
    /// 元数据来自 CompletionRuntime 的内存快照；按键路径不查库、不调宿主服务。
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [ContentType("SQL")]
    [Name("SsmsSqlPlugin.Completion")]
    internal sealed class SqlCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        private static readonly HashSet<int> LoggedViews = new HashSet<int>();

        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            int key = RuntimeHelpers.GetHashCode(textView);
            bool first;
            lock (LoggedViews) first = LoggedViews.Add(key);

            if (first)
            {
                string contentType = "(unknown)";
                try { contentType = textView.TextBuffer.ContentType.TypeName; }
                catch { }
                Log.Info("[补全] GetOrCreate ContentType=" + contentType);
            }

            return new SqlCompletionSource();
        }
    }

    internal sealed class SqlCompletionSource : IAsyncCompletionSource
    {
        private static readonly SqlCompletionEngine Engine = new SqlCompletionEngine();
        private static readonly ConditionalWeakTable<CompletionItem, CompletionCandidate> Descriptions
            = new ConditionalWeakTable<CompletionItem, CompletionCandidate>();

        internal static bool Enabled
            => Environment.GetEnvironmentVariable("SQLKIT_COMPLETION_DISABLE") != "1";

        public CompletionStartData InitializeCompletion(
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            CancellationToken token)
        {
            if (!Enabled) return CompletionStartData.DoesNotParticipateInCompletion;

            try
            {
                var span = ComputePrefixSpan(triggerLocation);
                Log.Info("[补全] InitializeCompletion reason=" + trigger.Reason +
                         " 前缀=\"" + span.GetText() + "\"");

                return new CompletionStartData(
                    CompletionParticipation.ProvidesItems, span);
            }
            catch (Exception ex)
            {
                Log.Error("[补全] InitializeCompletion 异常", ex);
                return CompletionStartData.DoesNotParticipateInCompletion;
            }
        }

        public Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            if (!Enabled) return Task.FromResult(CompletionContext.Empty);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var snapshot = triggerLocation.Snapshot;
                string sql = snapshot.GetText();
                int caret = triggerLocation.Position;

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

                var items = ImmutableArray.CreateBuilder<CompletionItem>(result.Items.Count);
                foreach (var candidate in result.Items)
                {
                    var item = CreateItem(candidate);
                    Descriptions.Add(item, candidate);
                    items.Add(item);
                }

                stopwatch.Stop();
                Log.Info("[补全] 候选=" + result.Items.Count +
                         " 槽位=" + result.Slot +
                         " 前缀=\"" + result.Prefix + "\"" +
                         " 元数据=" + Describe(metadata) +
                         " 用时=" + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0") + "ms" +
                         (string.IsNullOrEmpty(result.Error) ? "" : " 错误=" + result.Error));

                if (items.Count == 0) return Task.FromResult(CompletionContext.Empty);
                return Task.FromResult(new CompletionContext(items.ToImmutable()));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Error("[补全] 生成候选异常（" +
                          stopwatch.Elapsed.TotalMilliseconds.ToString("0.0") + "ms）", ex);
                return Task.FromResult(CompletionContext.Empty);
            }
        }

        public Task<object> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
        {
            CompletionCandidate candidate;
            if (item != null && Descriptions.TryGetValue(item, out candidate) &&
                !string.IsNullOrEmpty(candidate.Description))
                return Task.FromResult<object>(candidate.Description);

            return Task.FromResult<object>(null);
        }

        private CompletionItem CreateItem(CompletionCandidate candidate)
        {
            return new CompletionItem(
                candidate.DisplayText,
                this,
                null,
                ImmutableArray<CompletionFilter>.Empty,
                candidate.Suffix,
                candidate.InsertText,
                SortText(candidate),
                candidate.FilterText,
                ImmutableArray<ImageElement>.Empty);
        }

        private static string SortText(CompletionCandidate candidate)
        {
            int rank = int.MaxValue - Math.Min(candidate.Rank, int.MaxValue - 1);
            return rank.ToString("D10") + "|" + candidate.DisplayText;
        }

        private static SnapshotSpan ComputePrefixSpan(SnapshotPoint location)
        {
            var snapshot = location.Snapshot;
            int start = location.Position;

            while (start > 0)
            {
                char c = snapshot[start - 1];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$')
                    start--;
                else
                    break;
            }

            return new SnapshotSpan(snapshot, start, location.Position - start);
        }

        private static string Describe(MetadataSnapshot metadata)
        {
            if (metadata == null) return "无";

            int tables = 0, views = 0, procedures = 0, columns = 0;
            foreach (var obj in metadata.Objects)
            {
                if (obj.IsProcedure) procedures++;
                else if (obj.IsView) views++;
                else tables++;
                columns += obj.Columns.Count;
            }

            return metadata.Database + " 表" + tables + "/视图" + views +
                   "/过程" + procedures + "/列" + columns;
        }
    }
}
