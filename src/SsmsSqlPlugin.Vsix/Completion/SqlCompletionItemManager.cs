using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using SsmsSqlPlugin.Core.Completion;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// 接管 SQL 补全列表的过滤/排序。
    ///
    /// 平台默认匹配器把 `_` 当分隔符：输入 cbo_ 时 CBO_* 会被过滤掉，
    /// 只剩别的表。这里用 CompletionScorer 按 FilterText 原样匹配，
    /// 并保证候选按我们的词首评分排序。
    /// </summary>
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [ContentType("SQL")]
    [Name("SsmsSqlPlugin.ItemManager")]
    internal sealed class SqlCompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
    {
        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            return new SqlCompletionItemManager();
        }
    }

    internal sealed class SqlCompletionItemManager : IAsyncCompletionItemManager
    {
        public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(
            IAsyncCompletionSession session,
            AsyncCompletionSessionInitialDataSnapshot data,
            CancellationToken token)
        {
            var items = Sort(data.InitialList, string.Empty);
            return Task.FromResult(items.ToImmutableArray());
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(
            IAsyncCompletionSession session,
            AsyncCompletionSessionDataSnapshot data,
            CancellationToken token)
        {
            string prefix = string.Empty;

            try
            {
                if (session?.ApplicableToSpan != null && data?.Snapshot != null)
                    prefix = session.ApplicableToSpan.GetText(data.Snapshot) ?? string.Empty;
            }
            catch { }

            var items = Sort(data.InitialSortedList, prefix);

            if (prefix.IndexOf('_') >= 0)
            {
                string top = string.Empty;
                for (int i = 0; i < items.Count && i < 3; i++)
                {
                    if (i > 0) top += " | ";
                    top += items[i].DisplayText;
                }

                Log.Info("[补全] ItemManager 过滤: 前缀=\"" + prefix + "\" 保留=" +
                         items.Count + " 前几个=" + top);
            }

            return Task.FromResult(BuildModel(items));
        }

        private static List<CompletionItem> Sort(ImmutableArray<CompletionItem> items, string prefix)
        {
            var result = new List<CompletionItem>();

            foreach (var item in items)
            {
                if (item == null) continue;

                string filter = item.FilterText ?? item.DisplayText ?? string.Empty;
                if (!CompletionScorer.Matches(filter, prefix)) continue;

                result.Add(item);
            }

            result.Sort((a, b) =>
            {
                int sa = CompletionScorer.Score(a.FilterText ?? a.DisplayText ?? string.Empty, prefix);
                int sb = CompletionScorer.Score(b.FilterText ?? b.DisplayText ?? string.Empty, prefix);

                int c = sb.CompareTo(sa);
                if (c != 0) return c;

                return string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        private static FilteredCompletionModel BuildModel(List<CompletionItem> items)
        {
            var highlighted = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(items.Count);

            foreach (var item in items)
            {
                highlighted.Add(new CompletionItemWithHighlight(item));
            }

            return new FilteredCompletionModel(
                highlighted.ToImmutable(),
                0,
                ImmutableArray<CompletionFilterWithState>.Empty);
        }
    }
}
