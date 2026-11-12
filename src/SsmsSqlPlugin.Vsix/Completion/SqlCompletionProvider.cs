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

        // 每个编辑器视图复用同一个 source：会话、光标/文本事件必须跟同一个实例，
        // 否则 GetOrCreate 每次新建实例，旧列表在空格后无法被关闭。
        private readonly ConditionalWeakTable<ITextView, SqlCompletionSource> Sources
            = new ConditionalWeakTable<ITextView, SqlCompletionSource>();

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

            return Sources.GetValue(textView, view => new SqlCompletionSource(view));
        }
    }

    internal sealed class SqlCompletionSource : IAsyncCompletionSource
    {
        private static readonly SqlCompletionEngine Engine = new SqlCompletionEngine();
        private static readonly ConditionalWeakTable<CompletionItem, CompletionCandidate> Descriptions
            = new ConditionalWeakTable<CompletionItem, CompletionCandidate>();

        private readonly ITextView _view;

        private IAsyncCompletionSession _lastSession;
        private int _lastTriggerLine = -1;

        public SqlCompletionSource(ITextView view)
        {
            _view = view;

            try
            {
                _view.Caret.PositionChanged += OnCaretPositionChanged;
                _view.TextBuffer.Changed += OnBufferChanged;
                _view.Closed += OnViewClosed;
            }
            catch (Exception ex)
            {
                Log.Warn("[补全] 订阅编辑器事件失败: " + ex.Message);
            }
        }

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

                // 空白字符（空格/换行/Tab）插入时不参与补全。显式 Ctrl+J/点击
                // （Invoke）仍参与；点号后的空前缀（u.|）仍弹出字段。
                //
                // 这里必须用 trigger.Character：Insertion 的 triggerLocation 有时
                // 指向空白字符之前，看 snapshot[position-1] 会漏判，导致空格处
                // 建出一个“空前缀 + 全表”的列表，后续 cbo_ 都被它过滤。
                if (trigger.Reason != CompletionTriggerReason.Invoke &&
                    char.IsWhiteSpace(trigger.Character))
                {
                    Log.Info("[补全] 跳过空白字符触发: 0x" +
                             ((int)trigger.Character).ToString("X2"));
                    return CompletionStartData.DoesNotParticipateInCompletion;
                }

                if (trigger.Reason != CompletionTriggerReason.Invoke &&
                    span.Length == 0 &&
                    triggerLocation.Position > 0)
                {
                    char before = triggerLocation.Snapshot[triggerLocation.Position - 1];
                    if (char.IsWhiteSpace(before))
                    {
                        Log.Info("[补全] 跳过空前缀空白触发");
                        return CompletionStartData.DoesNotParticipateInCompletion;
                    }
                }

                Log.Info("[补全] InitializeCompletion reason=" + trigger.Reason +
                         " 前缀=\"" + span.GetText() + "\"");

                // 独占：SSMS 自带的 SQL IntelliSense 会同时给全表/关键字候选，
                // 和我们的列表混在一起时，cbo_ 会被它带偏。v1 由我们完全接管。
                return new CompletionStartData(
                    CompletionParticipation.ExclusivelyProvidesItems, span);
            }
            catch (Exception ex)
            {
                Log.Error("[补全] InitializeCompletion 异常", ex);
                return CompletionStartData.DoesNotParticipateInCompletion;
            }
        }

        public async Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            if (!Enabled) return CompletionContext.Empty;

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

                _lastSession = session;
                _lastTriggerLine = triggerLocation.GetContainingLine().LineNumber;

                var options = new CompletionOptions
                {
                    UppercaseKeywords = FormatOptionsStore.Current.UppercaseKeywords,
                    MaxItems = 200,
                };

                var result = Engine.GetCompletions(sql, caret, metadata, options);
                var prefixSpan = ComputePrefixSpan(triggerLocation);

                // 空前缀不自动弹表/字段/过程列表（点号后、显式 Ctrl+J 除外）。
                // 否则 FROM 后会在空格处建一个“全表”会话，用户再输入 cbo_
                // 只是过滤这份全表列表，CBO_* 可能根本不在前 200 条里。
                bool afterDot = triggerLocation.Position > 0 &&
                                snapshot[triggerLocation.Position - 1] == '.';

                if (trigger.Reason != CompletionTriggerReason.Invoke &&
                    !afterDot &&
                    string.IsNullOrEmpty(result.Prefix) &&
                    (result.Slot == "table" || result.Slot == "column" || result.Slot == "procedure"))
                {
                    Log.Info("[补全] 空前缀不自动出列表: 槽位=" + result.Slot);
                    return CompletionContext.Empty;
                }

                // 大库切库后元数据还在加载：这次请求直接等加载完成（最多 6 秒），
                // 避免先返回空列表、加载完还要用户重打一遍。
                if (runtime != null && runtime.IsLoading &&
                    (metadata == null || metadata.Objects.Count == 0) &&
                    (result.Slot == "table" || result.Slot == "column" || result.Slot == "procedure"))
                {
                    Log.Info("[补全] 等待元数据加载: 槽位=" + result.Slot);
                    metadata = await runtime.WaitForSnapshotAsync(
                        TimeSpan.FromSeconds(6), token).ConfigureAwait(false);

                    if (token.IsCancellationRequested ||
                        metadata == null || metadata.Objects.Count == 0)
                        return CompletionContext.Empty;

                    result = Engine.GetCompletions(sql, caret, metadata, options);
                    Log.Info("[补全] 元数据等待完成: 槽位=" + result.Slot +
                             " 元数据=" + Describe(metadata));
                }

                var items = ImmutableArray.CreateBuilder<CompletionItem>(result.Items.Count);
                foreach (var candidate in result.Items)
                {
                    var item = CreateItem(candidate, prefixSpan);
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

                if (items.Count == 0) return CompletionContext.Empty;

                // SoftSelection：候选只由 Tab / 鼠标点击提交，输入空格会关闭列表，
                // 不再出现"空格提交关键字、把 * from 吞掉/重排"。
                // IsIncomplete=true：后续输入必须重新取上下文，避免切库/换词后
                // 一直拿第一次的候选做过滤。
                return new CompletionContext(
                    items.ToImmutable(),
                    null,
                    InitialSelectionHint.SoftSelection,
                    ImmutableArray<CompletionFilterWithState>.Empty,
                    true);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return CompletionContext.Empty;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Error("[补全] 生成候选异常（" +
                          stopwatch.Elapsed.TotalMilliseconds.ToString("0.0") + "ms）", ex);
                return CompletionContext.Empty;
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

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            try
            {
                var caret = e.NewPosition.BufferPosition;
                int line = caret.GetContainingLine().LineNumber;

                var session = _lastSession;
                if (session == null) return;

                // 空格/换行后旧列表必须关闭；否则后续单词会继续过滤旧候选，
                // 出现 select * fr 时列表还显示 SELECT、Tab 提交成 SELECT。
                if (caret.Position > 0 && char.IsWhiteSpace(caret.Snapshot[caret.Position - 1]))
                {
                    session.Dismiss();
                    _lastSession = null;
                    Log.Info("[补全] 输入空白，关闭旧补全列表");
                    return;
                }

                if (line != _lastTriggerLine)
                {
                    // 光标换行/换语句：旧列表的候选和 span 已不适用，必须关掉，
                    // 否则多行脚本里会拿上一行的表列表过滤下一行的 select。
                    session.Dismiss();
                    _lastSession = null;
                    Log.Info("[补全] 光标换行，关闭旧补全列表");
                }
            }
            catch { }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_lastSession == null) return;

            try
            {
                bool invalidated = false;

                foreach (var change in e.Changes)
                {
                    string inserted = change.NewText;

                    // 删除字符后旧的 applicable span/候选通常已经不对了
                    if (string.IsNullOrEmpty(inserted))
                    {
                        invalidated = true;
                        break;
                    }

                    // 输入空格/换行：当前字段补全结束，关窗口
                    if (char.IsWhiteSpace(inserted[inserted.Length - 1]))
                    {
                        invalidated = true;
                        break;
                    }
                }

                if (invalidated)
                {
                    _lastSession.Dismiss();
                    _lastSession = null;
                    Log.Info("[补全] 输入空白或删除，关闭当前补全窗口");
                }
            }
            catch { }
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            try
            {
                _view.Caret.PositionChanged -= OnCaretPositionChanged;
                _view.TextBuffer.Changed -= OnBufferChanged;
                _view.Closed -= OnViewClosed;
            }
            catch { }
            _lastSession = null;
        }

        private CompletionItem CreateItem(CompletionCandidate candidate, SnapshotSpan applicableToSpan)
        {
            // ApplicableToSpan 必须限定为当前单词：默认提交器据此替换文本。
            // 不传的话它会退回会话级 span，可能把前面的 "* " 一起替换掉。
            return new CompletionItem(
                candidate.DisplayText,
                this,
                null,
                ImmutableArray<CompletionFilter>.Empty,
                candidate.Suffix,
                candidate.InsertText,
                SortText(candidate),
                candidate.FilterText,
                candidate.DisplayText,
                ImmutableArray<ImageElement>.Empty,
                ImmutableArray<char>.Empty,
                applicableToSpan,
                false,
                false);
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
