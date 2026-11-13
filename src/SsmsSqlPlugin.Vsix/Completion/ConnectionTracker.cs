using System;
using System.Data;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
        private DateTime _nextTypeScanUtc;
        private bool _missingServiceLogged;
        private bool _serviceNotRegisteredLogged;
        private Type _serviceIdType;
        private Type _serviceInterfaceType;
        private MethodInfo _getCurrentConnection;
        private MethodInfo _getActiveEditorMoniker;

        // SSMS 20 没有 SSqlEditorService，编辑器窗口连接走
        // Microsoft.SqlServer.Management.UI.VSIntegration.ISqlToolsWindowWithConnectionState。
        private Type _connectionStateType;
        private Type _editorWindowConnectionType;
        private PropertyInfo _connectionStateIsConnected;
        private PropertyInfo _connectionStateConnection;

        private string _lastSignature;
        private string _lastMoniker;
        private string _lastTitleDatabase;
        private bool _titleDatabaseInitialized;

        /// <summary>标题兜底检测到数据库切换（没有连接服务时）。</summary>
        public event Action<string> TitleDatabaseChanged;

        public string LastObservedDatabase => _lastTitleDatabase;

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

            object service = ResolveService();
            if (service == null)
            {
                if (!_serviceNotRegisteredLogged)
                {
                    _serviceNotRegisteredLogged = true;
                    var serviceType = _serviceIdType ?? _connectionStateType;
                    Log.Warn("[补全] 连接服务未注册: " + (serviceType != null ? serviceType.FullName : "(unknown)"));
                }

                // SSMS 20 读不到编辑器连接；退化为窗口标题里的数据库名，至少给切库提示。
                TryNotifyTitleDatabase();
                return;
            }

            object connectionObject = null;
            try
            {
                if (_connectionStateType != null && _connectionStateType.IsInstanceOfType(service))
                {
                    object connectedValue = _connectionStateIsConnected != null
                        ? _connectionStateIsConnected.GetValue(service, null)
                        : null;
                    if (connectedValue is bool connected && !connected)
                    {
                        Notify(null);
                        return;
                    }

                    connectionObject = _connectionStateConnection.GetValue(service, null);
                }
                else if (_getCurrentConnection != null)
                {
                    connectionObject = _getCurrentConnection.Invoke(service, null);
                }
                else
                {
                    Notify(null);
                    return;
                }
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

        private void TryNotifyTitleDatabase()
        {
            string title = null;
            EnvDTE.DTE dte = null;

            try
            {
                dte = _provider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.MainWindow != null)
                {
                    var hwnd = (IntPtr)(long)dte.MainWindow.HWnd;
                    if (hwnd != IntPtr.Zero)
                    {
                        var buffer = new StringBuilder(512);
                        if (GetWindowText(hwnd, buffer, buffer.Capacity) > 0)
                            title = buffer.ToString();
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(title))
            {
                try { title = dte?.ActiveWindow?.Caption; } catch { }
            }

            if (string.IsNullOrEmpty(title))
            {
                try { title = System.Diagnostics.Process.GetCurrentProcess().MainWindowTitle; }
                catch { }
            }

            string database = ParseDatabaseFromTitle(title);
            if (string.IsNullOrEmpty(database)) return;

            if (!_titleDatabaseInitialized)
            {
                _titleDatabaseInitialized = true;
                _lastTitleDatabase = database;
                Log.Info("[补全] 标题数据库: " + database);
                return;
            }

            if (string.Equals(database, _lastTitleDatabase, StringComparison.OrdinalIgnoreCase)) return;

            _lastTitleDatabase = database;
            Log.Info("[补全] 标题数据库切换: " + database);
            try { TitleDatabaseChanged?.Invoke(database); }
            catch (Exception ex) { Log.Warn("[补全] 通知标题数据库失败: " + ex.Message); }
        }

        private static string ParseDatabaseFromTitle(string title)
        {
            const string suffix = " - Microsoft SQL Server Management Studio";
            if (string.IsNullOrEmpty(title)) return null;

            string body = title;
            if (body.EndsWith(suffix, StringComparison.Ordinal))
                body = body.Substring(0, body.Length - suffix.Length);

            int dash = body.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0) return null;

            string connectionPart = body.Substring(dash + 3);
            int paren = connectionPart.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) connectionPart = connectionPart.Substring(0, paren);

            connectionPart = connectionPart.TrimEnd('*').Trim();
            int dot = connectionPart.LastIndexOf('.');
            if (dot < 0 || dot + 1 >= connectionPart.Length) return null;

            return connectionPart.Substring(dot + 1).Trim();
        }

        private bool EnsureServiceTypes()
        {
            if (_typesResolved) return _serviceIdType != null && _getCurrentConnection != null;

            // SSMS 21 的编辑器程序集要等查询窗口出现才加载；失败不能永久缓存。
            if (DateTime.UtcNow < _nextTypeScanUtc) return false;
            _nextTypeScanUtc = DateTime.UtcNow.AddSeconds(10);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (TryResolve(assembly)) return true;
            }

            if (!HasConnectionPath())
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates =
                {
                    Path.Combine(baseDir, "SqlWorkbench.Interfaces.dll"),
                    Path.Combine(baseDir, "PublicAssemblies", "SqlWorkbench.Interfaces.dll"),
                    Path.Combine(baseDir, "PrivateAssemblies", "SqlWorkbench.Interfaces.dll"),
                    Path.Combine(baseDir, "Extensions", "Application", "SQLEditors.dll"),
                    Path.Combine(baseDir, "Extensions", "Application",
                                 "Microsoft.SqlServer.Management.SqlStudio.Explorer.dll"),
                };

                foreach (string path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var assembly = Assembly.LoadFrom(path);
                        if (TryResolve(assembly)) return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("[补全] 加载宿主程序集失败(" + Path.GetFileName(path) + "): " + ex.Message);
                    }
                }
            }

            if (!HasConnectionPath())
            {
                if (!_missingServiceLogged)
                {
                    _missingServiceLogged = true;
                    Log.Warn("[补全] 暂未找到 SSqlEditorService，将在打开查询窗口后重试");
                }
                return false;
            }

            _typesResolved = true;
            return true;
        }

        private bool HasConnectionPath()
        {
            bool modern = _serviceIdType != null && _getCurrentConnection != null;
            bool legacy = _connectionStateType != null && _connectionStateConnection != null;
            return modern || legacy;
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
                else if (type.Name == "ISqlToolsWindowWithConnectionState" && _connectionStateType == null)
                {
                    var connectionProperty = type.GetProperty("Connection");
                    if (connectionProperty != null)
                    {
                        _connectionStateType = type;
                        _connectionStateConnection = connectionProperty;
                        _connectionStateIsConnected = type.GetProperty("IsConnected");
                        Log.Info("[补全] 找到 SSMS 20 连接状态接口: " + type.FullName);
                    }
                }
                else if (type.Name == "ISqlScriptWindowWithConnection")
                {
                    _editorWindowConnectionType = type;
                }
            }

            if (_serviceIdType == null && _serviceInterfaceType == null)
            {
                // SSMS 21 没有同名类型时，按方法签名找连接服务。
                foreach (var type in types)
                {
                    if (type == null || type.IsInterface) continue;

                    var getConnection = type.GetMethod(
                        "GetCurrentConnection", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (getConnection == null) continue;

                    var getMoniker = type.GetMethod(
                        "GetActiveEditorMoniker", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);

                    _serviceIdType = type;
                    _serviceInterfaceType = type;
                    _getCurrentConnection = getConnection;
                    _getActiveEditorMoniker = getMoniker;

                    foreach (var iface in type.GetInterfaces())
                    {
                        if (iface.GetMethod("GetCurrentConnection", BindingFlags.Public | BindingFlags.Instance,
                                null, Type.EmptyTypes, null) != null)
                        {
                            _serviceInterfaceType = iface;
                            break;
                        }
                    }

                    Log.Info("[补全] 连接服务候选: " + type.FullName +
                             " GUID=" + type.GUID +
                             " 接口=" + _serviceInterfaceType.FullName);
                    return true;
                }
            }

            var methodHost = _serviceInterfaceType ?? _serviceIdType;
            if (methodHost != null)
            {
                _getCurrentConnection = methodHost.GetMethod(
                    "GetCurrentConnection", BindingFlags.Public | BindingFlags.Instance, null,
                    Type.EmptyTypes, null);

                _getActiveEditorMoniker = methodHost.GetMethod(
                    "GetActiveEditorMoniker", BindingFlags.Public | BindingFlags.Instance, null,
                    Type.EmptyTypes, null);
            }

            bool modern = _serviceIdType != null && _getCurrentConnection != null;
            bool legacy = _connectionStateType != null && _connectionStateConnection != null;
            return modern || legacy;
        }

        private object ResolveService()
        {
            foreach (var type in ServiceTypes())
            {
                object service = null;

                try { service = _provider.GetService(type); } catch { }
                if (service != null) return service;

                try { service = Package.GetGlobalService(type); } catch { }
                if (service != null) return service;

                try { service = ServiceProvider.GlobalProvider.GetService(type); } catch { }
                if (service != null) return service;
            }

            return null;
        }

        private System.Collections.Generic.IEnumerable<Type> ServiceTypes()
        {
            if (_serviceIdType != null) yield return _serviceIdType;

            if (_serviceInterfaceType != null && _serviceInterfaceType != _serviceIdType)
                yield return _serviceInterfaceType;

            if (_connectionStateType != null &&
                _connectionStateType != _serviceIdType &&
                _connectionStateType != _serviceInterfaceType)
                yield return _connectionStateType;

            if (_editorWindowConnectionType != null &&
                _editorWindowConnectionType != _serviceIdType &&
                _editorWindowConnectionType != _serviceInterfaceType &&
                _editorWindowConnectionType != _connectionStateType)
                yield return _editorWindowConnectionType;
        }

        private static string Describe(Exception ex)
        {
            return Log.Describe(ex);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    }
}
