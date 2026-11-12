using System;
using System.Data;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using SsmsSqlPlugin.Core.Metadata;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// 后台每 1.5 秒读一次「当前查询窗口的连接」，供补全缓存使用。
    ///
    /// 宿主服务（SSqlEditorService）只在 UI 线程调用；按键路径完全不碰它。
    /// 连接串可能含明文密码，只留在内存，日志只打 Server/Database。
    /// </summary>
    internal sealed class ConnectionTracker
    {
        private const int PollMilliseconds = 1500;

        private readonly IServiceProvider _provider;
        private readonly Action<ConnectionDescriptor> _onChanged;

        private Timer _timer;
        private int _polling;
        private bool _typesResolved;
        private Type _serviceIdType;
        private Type _serviceInterfaceType;
        private MethodInfo _getCurrentConnection;
        private MethodInfo _getActiveEditorMoniker;

        private string _lastSignature;
        private string _lastMoniker;

        public ConnectionTracker(IServiceProvider provider, Action<ConnectionDescriptor> onChanged)
        {
            _provider = provider;
            _onChanged = onChanged;
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(OnTimer, null, 800, PollMilliseconds);
        }

        public void Stop()
        {
            var timer = Interlocked.Exchange(ref _timer, null);
            if (timer != null) timer.Dispose();
        }

        private void OnTimer(object state)
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0) return;

            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Poll();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[补全] 连接探测失败: " + ex.Message);
                    }
                    finally
                    {
                        Volatile.Write(ref _polling, 0);
                    }
                });
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _polling, 0);
                Log.Warn("[补全] 连接探测调度失败: " + ex.Message);
            }
        }

        private void Poll()
        {
            if (_provider == null) return;
            if (!EnsureServiceTypes()) return;

            object service = _provider.GetService(_serviceIdType);
            if (service == null) return;

            object connectionObject = null;
            try
            {
                connectionObject = _getCurrentConnection.Invoke(service, null);
            }
            catch (Exception ex)
            {
                Log.Warn("[补全] GetCurrentConnection 失败: " + Describe(ex));
                Notify(null);
                return;
            }

            var connection = connectionObject as IDbConnection;
            if (connection == null)
            {
                Notify(null);
                return;
            }

            string server = null, database = null, connectionString = null;
            try
            {
                var dbConnection = connection as System.Data.Common.DbConnection;
                if (dbConnection != null)
                {
                    server = dbConnection.DataSource;
                    database = dbConnection.Database;
                }
                else
                {
                    database = connection.Database;
                    var dataSource = connection.GetType().GetProperty("DataSource");
                    server = dataSource != null
                        ? dataSource.GetValue(connection, null) as string
                        : null;
                }

                connectionString = connection.ConnectionString;
            }
            catch (Exception ex)
            {
                Log.Warn("[补全] 读取连接信息失败: " + Describe(ex));
                return;
            }

            string moniker = null;
            if (_getActiveEditorMoniker != null)
            {
                try { moniker = _getActiveEditorMoniker.Invoke(service, null) as string; }
                catch { }
            }

            if (moniker != null && !string.Equals(moniker, _lastMoniker, StringComparison.Ordinal))
            {
                _lastMoniker = moniker;
                Log.Info("[补全] 当前编辑器: " + moniker);
            }

            var descriptor = new ConnectionDescriptor
            {
                Server = server,
                Database = database,
                ConnectionString = connectionString,
                EditorMoniker = moniker,
                ConnectionType = connection.GetType(),
            };

            string signature = descriptor.Key + "\u0001" + connectionString;
            if (!string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _lastSignature = signature;
                Log.Info("[补全] 当前连接: " + server + " / " + database);
            }

            Notify(descriptor);
        }

        private void Notify(ConnectionDescriptor descriptor)
        {
            if (descriptor == null)
            {
                if (_lastSignature == null) return;
                _lastSignature = null;
                _lastMoniker = null;
                Log.Info("[补全] 连接已断开，清空补全元数据");
            }

            try { _onChanged?.Invoke(descriptor); }
            catch (Exception ex) { Log.Warn("[补全] 通知连接变化失败: " + ex.Message); }
        }

        private bool EnsureServiceTypes()
        {
            if (_typesResolved) return _serviceIdType != null && _getCurrentConnection != null;
            _typesResolved = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (TryResolve(assembly)) break;
            }

            if (_serviceIdType == null || _getCurrentConnection == null)
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates =
                {
                    Path.Combine(baseDir, "SqlWorkbench.Interfaces.dll"),
                    Path.Combine(baseDir, "PublicAssemblies", "SqlWorkbench.Interfaces.dll"),
                    Path.Combine(baseDir, "PrivateAssemblies", "SqlWorkbench.Interfaces.dll"),
                };

                foreach (string path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var assembly = Assembly.LoadFrom(path);
                        if (TryResolve(assembly)) break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[补全] 加载 SqlWorkbench.Interfaces 失败: " + ex.Message);
                    }
                }
            }

            if (_serviceIdType == null || _getCurrentConnection == null)
            {
                Log.Warn("[补全] 找不到 SSqlEditorService，连接感知不可用");
                return false;
            }

            return true;
        }

        private bool TryResolve(Assembly assembly)
        {
            if (assembly == null) return false;

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { return false; }

            foreach (var type in types)
            {
                if (type == null) continue;

                if (type.Name == "SSqlEditorService") _serviceIdType = type;
                else if (type.Name == "ISqlEditorService") _serviceInterfaceType = type;
            }

            if (_serviceIdType == null && _serviceInterfaceType == null) return false;

            var methodHost = _serviceInterfaceType ?? _serviceIdType;
            _getCurrentConnection = methodHost.GetMethod(
                "GetCurrentConnection", BindingFlags.Public | BindingFlags.Instance, null,
                Type.EmptyTypes, null);

            _getActiveEditorMoniker = methodHost.GetMethod(
                "GetActiveEditorMoniker", BindingFlags.Public | BindingFlags.Instance, null,
                Type.EmptyTypes, null);

            return _serviceIdType != null && _getCurrentConnection != null;
        }

        private static string Describe(Exception ex)
        {
            return Log.Describe(ex);
        }
    }
}
