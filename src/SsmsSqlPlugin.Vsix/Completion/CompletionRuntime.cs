using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SsmsSqlPlugin.Core.Metadata;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// 补全运行时单例：连接跟踪 + 元数据缓存。
    /// 包初始化时 Start；MEF provider 只读这里的快照。
    /// </summary>
    internal sealed class CompletionRuntime
    {
        private static CompletionRuntime _current;
        private readonly MetadataCache _metadata = new MetadataCache();
        private readonly MetadataLoadProgress _progress = new MetadataLoadProgress();
        private ConnectionTracker _tracker;
        private bool _started;
        private int _version;

        public static CompletionRuntime Current => _current;

        public int Version => System.Threading.Volatile.Read(ref _version);

        public bool IsLoading => _metadata.IsLoading;

        /// <summary>元数据加载完成（后台线程）。</summary>
        public event Action MetadataLoaded;

        public static void Start(IServiceProvider provider)
        {
            if (_current == null) _current = new CompletionRuntime();
            _current.StartCore(provider);
        }

        public static void Stop()
        {
            _current?._tracker?.Stop();
            _current?._progress?.Close();
        }

        private CompletionRuntime()
        {
            _metadata.OnLoadStarted = OnMetadataLoadStarted;
            _metadata.OnLoaded = OnMetadataLoaded;
        }

        public MetadataSnapshot GetSnapshot()
        {
            var snapshot = _metadata.Snapshot;
            if (_metadata.IsStale) _metadata.RequestRefresh(false);
            return snapshot ?? MetadataSnapshot.Empty();
        }

        /// <summary>
        /// 大库首次加载时，补全请求直接等加载完成（有超时），
        /// 避免“加载期间输入先得到一个空列表，加载完还要重打”。
        /// </summary>
        public async Task<MetadataSnapshot> WaitForSnapshotAsync(TimeSpan timeout, CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();

            while (_metadata.IsLoading && stopwatch.Elapsed < timeout)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }

            return _metadata.Snapshot ?? MetadataSnapshot.Empty();
        }

        public void RefreshMetadata()
        {
            _metadata.Invalidate();
            Log.Info("[补全] 已请求刷新元数据缓存");
        }

        public ConnectionDescriptor Connection => _metadata.Connection;

        private void StartCore(IServiceProvider provider)
        {
            if (_started) return;
            _started = true;

            _tracker = new ConnectionTracker(provider, OnConnectionChanged);
            _tracker.Start();
        }

        private void OnConnectionChanged(ConnectionDescriptor descriptor)
        {
            _metadata.SetConnection(descriptor);
        }

        private void OnMetadataLoadStarted(ConnectionDescriptor descriptor)
        {
            if (descriptor != null) _progress.Begin(descriptor.Database);
        }

        private void OnMetadataLoaded(MetadataSnapshot snapshot, TimeSpan elapsed, string error)
        {
            _progress.End();
            System.Threading.Interlocked.Increment(ref _version);

            try { MetadataLoaded?.Invoke(); }
            catch { }

            int tables = 0, views = 0, procedures = 0, columns = 0;
            foreach (var obj in snapshot.Objects)
            {
                if (obj.IsProcedure) procedures++;
                else if (obj.IsView) views++;
                else tables++;
                columns += obj.Columns.Count;
            }

            Log.Info("[补全] 元数据加载完成: " + snapshot.Database +
                     " 表" + tables + "/视图" + views + "/过程" + procedures +
                     "/列" + columns +
                     " 用时=" + elapsed.TotalMilliseconds.ToString("0") + "ms" +
                     (string.IsNullOrEmpty(error) ? "" : " 警告=" + error));
        }
    }
}
