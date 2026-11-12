using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SsmsSqlPlugin.Core.Metadata
{
    /// <summary>
    /// 内存元数据缓存。按键路径只读 Snapshot；加载永远在后台线程。
    ///
    /// 连接变化会作废旧快照；旧加载的结果用 generation 丢弃，避免
    /// 上一个库的慢查询把新库的快照覆盖掉。
    /// </summary>
    public sealed class MetadataCache
    {
        private readonly object _gate = new object();
        private MetadataSnapshot _snapshot = MetadataSnapshot.Empty();
        private ConnectionDescriptor _connection;
        private DateTime _loadedUtc = DateTime.MinValue;
        private bool _loading;
        private int _generation;
        private string _lastError;
        private long _lastLoadMs;

        public TimeSpan Ttl = TimeSpan.FromMinutes(5);

        /// <summary>加载开始回调（调用线程）：连接描述。</summary>
        public Action<ConnectionDescriptor> OnLoadStarted;

        /// <summary>加载完成回调（后台线程）：snapshot, 用时, 错误。</summary>
        public Action<MetadataSnapshot, TimeSpan, string> OnLoaded;

        public MetadataSnapshot Snapshot
        {
            get { lock (_gate) { return _snapshot; } }
        }

        public ConnectionDescriptor Connection
        {
            get { lock (_gate) { return _connection; } }
        }

        public bool IsLoading
        {
            get { lock (_gate) { return _loading; } }
        }

        public string LastError
        {
            get { lock (_gate) { return _lastError; } }
        }

        public long LastLoadMs
        {
            get { lock (_gate) { return _lastLoadMs; } }
        }

        public bool IsStale
        {
            get
            {
                lock (_gate)
                {
                    return _loadedUtc == DateTime.MinValue ||
                           DateTime.UtcNow - _loadedUtc > Ttl;
                }
            }
        }

        public void SetConnection(ConnectionDescriptor descriptor)
        {
            bool changed;

            lock (_gate)
            {
                var old = _connection;
                changed = descriptor == null
                    ? old != null
                    : old == null ||
                      !string.Equals(old.Key, descriptor.Key, StringComparison.Ordinal) ||
                      !string.Equals(old.ConnectionString, descriptor.ConnectionString, StringComparison.Ordinal);

                _connection = descriptor;

                if (changed)
                {
                    _generation++;
                    _snapshot = MetadataSnapshot.Empty(descriptor?.Server, descriptor?.Database);
                    _loadedUtc = DateTime.MinValue;
                    _loading = false;
                    _lastError = null;
                }
            }

            if (changed) RequestRefresh(true);
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                _loadedUtc = DateTime.MinValue;
                _generation++;
            }
            RequestRefresh(true);
        }

        /// <summary>需要时启动一次后台加载；返回是否真的启动了。</summary>
        public bool RequestRefresh(bool force)
        {
            ConnectionDescriptor descriptor;
            int generation;

            lock (_gate)
            {
                if (_loading) return false;
                if (_connection == null || !_connection.IsUsable) return false;

                if (!force && _loadedUtc != DateTime.MinValue &&
                    DateTime.UtcNow - _loadedUtc <= Ttl)
                    return false;

                descriptor = _connection;
                generation = _generation;
                _loading = true;
            }

            try { OnLoadStarted?.Invoke(descriptor); }
            catch { }

            Task.Run(() => LoadAsync(descriptor, generation));
            return true;
        }

        private void LoadAsync(ConnectionDescriptor descriptor, int generation)
        {
            var stopwatch = Stopwatch.StartNew();
            string error = null;
            MetadataSnapshot snapshot;

            try
            {
                snapshot = MetadataLoader.Load(descriptor, out error);
            }
            catch (Exception ex)
            {
                snapshot = MetadataSnapshot.Empty(descriptor.Server, descriptor.Database);
                error = ex.Message;
            }

            stopwatch.Stop();

            bool applied = false;
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _snapshot = snapshot;
                    _loadedUtc = DateTime.UtcNow;
                    _lastError = error;
                    _lastLoadMs = stopwatch.ElapsedMilliseconds;
                    _loading = false;
                    applied = true;
                }
            }

            // 旧连接/旧库的慢加载结果已被丢弃，不能再触发 OnLoaded 去关新加载的进度窗
            if (!applied) return;

            try { OnLoaded?.Invoke(snapshot, stopwatch.Elapsed, error); }
            catch { }
        }
    }
}
