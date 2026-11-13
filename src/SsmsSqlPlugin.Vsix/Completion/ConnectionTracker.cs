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

        private Type _objectExplorerServiceType;
        private Type _nodeContextType;
        private PropertyInfo _nodeContextConnection;
        private PropertyInfo _nodeContextUrnPath;
        private PropertyInfo _nodeInformationItem;
        private PropertyInfo _nodeInformationName;
        private DateTime _nextObjectExplorerProbeUtc;
        private bool _objectExplorerMissingLogged;
        private bool _objectExplorerNoSelectionLogged;
        private bool _objectExplorerMethodsLogged;
        private bool _objectExplorerNodeTypeLogged;
        private bool _objectExplorerNullConnectionLogged;
        private string _lastObjectExplorerSignature;

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

            object connectionObject = null;
            object service = ResolveService();
            if (service == null)
            {
                if (!_serviceNotRegisteredLogged)
                {
                    _serviceNotRegisteredLogged = true;
                    var serviceType = _serviceIdType ?? _connectionStateType;
                    Log.Warn("[补全] 连接服务未注册: " + (serviceType != null ? serviceType.FullName : "(unknown)"));
                }

                // SSMS 20：查询窗口不暴露连接，退而使用对象资源管理器当前选中的节点。
                var objectExplorerDescriptor = TryGetObjectExplorerDescriptor();
                if (objectExplorerDescriptor != null)
                {
                    Notify(objectExplorerDescriptor);
                    return;
                }

                // 仍读不到：至少用窗口标题里的数据库名给切库提示。
                TryNotifyTitleDatabase();
                return;
            }
            else
            {
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

        private ConnectionDescriptor TryGetObjectExplorerDescriptor()
        {
            if (_objectExplorerServiceType == null)
                return null;

            if (DateTime.UtcNow < _nextObjectExplorerProbeUtc) return null;
            _nextObjectExplorerProbeUtc = DateTime.UtcNow.AddSeconds(5);

            object service = ResolveServiceByType(_objectExplorerServiceType);
            if (service == null)
            {
                if (!_objectExplorerMissingLogged)
                {
                    _objectExplorerMissingLogged = true;
                    Log.Warn("[补全] Object Explorer 服务未注册: " + _objectExplorerServiceType.FullName);
                }
                return null;
            }

            try
            {
                var nodes = GetSelectedObjectExplorerNodes(service);
                if (nodes == null || nodes.Length == 0)
                {
                    if (!_objectExplorerNoSelectionLogged)
                    {
                        _objectExplorerNoSelectionLogged = true;
                        Log.Info("[补全] Object Explorer 未选中节点");
                    }
                    return null;
                }

                _objectExplorerNoSelectionLogged = false;

                foreach (object node in nodes)
                {
                    if (node == null) continue;

                    if (!_objectExplorerNodeTypeLogged)
                    {
                        _objectExplorerNodeTypeLogged = true;
                        int itemIndexParams = _nodeInformationItem != null
                            ? _nodeInformationItem.GetIndexParameters().Length
                            : -1;
                        Log.Info("[补全] OE 节点类型: " + node.GetType().FullName +
                                 " 节点是INodeContext=" +
                                 (_nodeContextType != null && _nodeContextType.IsInstanceOfType(node)) +
                                 " Item索引参数=" + itemIndexParams);
                    }

                    object item = node;
                    if (_nodeContextType == null || !_nodeContextType.IsInstanceOfType(item))
                    {
                        // INodeInformation.Item 可能是索引器；只有无参属性才直接取。
                        if (_nodeInformationItem == null ||
                            _nodeInformationItem.GetIndexParameters().Length != 0)
                            continue;

                        item = _nodeInformationItem.GetValue(node, null);
                    }

                    if (item == null) continue;

                    if (_nodeContextType != null && !_nodeContextType.IsInstanceOfType(item))
                        continue;

                    object connectionObject = _nodeContextConnection.GetValue(item, null);
                    if (connectionObject == null)
                    {
                        if (!_objectExplorerNullConnectionLogged)
                        {
                            _objectExplorerNullConnectionLogged = true;
                            string nodeUrn = _nodeContextUrnPath != null
                                ? _nodeContextUrnPath.GetValue(item, null) as string
                                : null;
                            Log.Info("[补全] OE 节点连接为空: urn=" + (nodeUrn ?? "(null)"));
                        }
                        continue;
                    }

                    string urn = _nodeContextUrnPath != null
                        ? _nodeContextUrnPath.GetValue(item, null) as string
                        : null;

                    string nodeName = null;
                    try { nodeName = _nodeInformationName?.GetValue(node, null) as string; } catch { }

                    // 节点可能直接给 IDbConnection，也可能是 SqlConnectionInfoWithConnection 包装。
                    var connection = connectionObject as IDbConnection;
                    bool created = false;

                    if (connection == null)
                    {
                        var factory = connectionObject.GetType().GetMethod(
                            "CreateConnectionObject", Type.EmptyTypes);
                        if (factory == null)
                        {
                            if (!_objectExplorerNullConnectionLogged)
                            {
                                _objectExplorerNullConnectionLogged = true;
                                Log.Info("[补全] OE 节点无可用连接: urn=" + (urn ?? "(null)") +
                                         " 对象=" + connectionObject.GetType().FullName);
                            }
                            continue;
                        }

                        try
                        {
                            connection = factory.Invoke(connectionObject, null) as IDbConnection;
                            created = connection != null;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("[补全] 创建 OE 连接失败: " + Describe(ex));
                            continue;
                        }
                    }

                    if (connection == null) continue;

                    string connectionString = null;
                    try { connectionString = connection.ConnectionString; } catch { }
                    if (string.IsNullOrEmpty(connectionString))
                    {
                        Log.Warn("[补全] OE 连接串为空: urn=" + (urn ?? "(null)"));
                        if (created) { try { connection.Dispose(); } catch { } }
                        continue;
                    }

                    // 连接信息对象通常还持有明文密码；连接串缺 Password 时补进去。
                    string password = GetStringProperty(connectionObject, "Password");
                    if (!string.IsNullOrEmpty(password) &&
                        connectionString.IndexOf("Password=", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        try
                        {
                            var builder = new System.Data.Common.DbConnectionStringBuilder();
                            builder.ConnectionString = connectionString;
                            builder["Password"] = password;
                            connectionString = builder.ConnectionString;
                            Log.Info("[补全] OE 连接串已补全密码字段");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("[补全] 补全 OE 密码失败: " + ex.Message);
                        }
                    }

                    string server = null, database = null;
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
                            server = GetStringProperty(connectionObject, "ServerName");
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(server))
                        server = GetStringProperty(connectionObject, "ServerName");
                    if (string.IsNullOrEmpty(database))
                        database = GetStringProperty(connectionObject, "DatabaseName");

                    // 数据库节点的 ConnectionInfo 可能仍指向 master，用节点名覆盖；
                    // 同时把库写进连接串，否则元数据还是查 master。
                    bool isDatabaseNode = urn != null &&
                        urn.StartsWith("Server/Database", StringComparison.OrdinalIgnoreCase) &&
                        urn.IndexOf("DatabasesFolder", StringComparison.OrdinalIgnoreCase) < 0;

                    if (isDatabaseNode && !string.IsNullOrEmpty(nodeName))
                    {
                        database = nodeName;
                    }

                    if (!string.IsNullOrEmpty(database))
                    {
                        try
                        {
                            var builder = new System.Data.Common.DbConnectionStringBuilder();
                            builder.ConnectionString = connectionString;
                            builder["Initial Catalog"] = database;
                            connectionString = builder.ConnectionString;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("[补全] 设置 OE 数据库失败: " + ex.Message);
                        }
                    }

                    bool hasPassword = connectionString.IndexOf(
                        "Password=", StringComparison.OrdinalIgnoreCase) >= 0;
                    string signature = (urn ?? string.Empty) + "\u0001" +
                                       (server ?? string.Empty) + "\u0001" +
                                       (database ?? string.Empty) + "\u0001" + hasPassword;

                    if (!string.Equals(signature, _lastObjectExplorerSignature, StringComparison.Ordinal))
                    {
                        _lastObjectExplorerSignature = signature;
                        Log.Info("[补全] OE 选中节点: " + (urn ?? "(no urn)") +
                                 " 名称=" + (nodeName ?? "?") +
                                 " 服务器=" + (server ?? "?") +
                                 " 库=" + (database ?? "?") +
                                 " 有密码=" + hasPassword);
                    }

                    var descriptor = new ConnectionDescriptor
                    {
                        Server = server,
                        Database = database,
                        ConnectionString = connectionString,
                        ConnectionType = connection.GetType(),
                        EditorMoniker = null,
                    };

                    // CreateConnectionObject 创建的是临时对象，元数据加载器会用连接串另开连接。
                    if (created)
                    {
                        try { connection.Dispose(); } catch { }
                    }

                    return descriptor;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[补全] 读取 Object Explorer 选中节点失败: " + Describe(ex));
            }

            return null;
        }

        private Array GetSelectedObjectExplorerNodes(object service)
        {
            var methods = service.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var method in methods)
            {
                if (method.Name != "GetSelectedNodes" &&
                    !method.Name.EndsWith(".GetSelectedNodes", StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (!_objectExplorerMethodsLogged)
                {
                    _objectExplorerMethodsLogged = true;
                    var signature = new StringBuilder();
                    foreach (var parameter in parameters)
                    {
                        signature.Append(parameter.ParameterType.Name)
                                 .Append(' ')
                                 .Append(parameter.Name)
                                 .Append("; ");
                    }

                    Log.Info("[补全] OE GetSelectedNodes 签名: " + method.DeclaringType?.FullName +
                             "." + method.Name + " 参数=" + parameters.Length + " " + signature);
                }

                try
                {
                    if (parameters.Length == 2)
                    {
                        object[] args = { 0, null };
                        method.Invoke(service, args);
                        return args[1] as Array;
                    }

                    if (parameters.Length == 0)
                    {
                        return method.Invoke(service, null) as Array;
                    }

                    if (parameters.Length == 1)
                    {
                        object[] args = { null };
                        method.Invoke(service, args);
                        return args[0] as Array;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("[补全] GetSelectedNodes(" + parameters.Length + " 参数) 调用失败: " +
                             Describe(ex));
                }
            }

            return null;
        }

        private object ResolveServiceByType(Type type)
        {
            if (type == null) return null;

            object service = null;
            try { service = _provider.GetService(type); } catch { }
            if (service != null) return service;

            try { service = Package.GetGlobalService(type); } catch { }
            if (service != null) return service;

            try { service = ServiceProvider.GlobalProvider.GetService(type); } catch { }
            return service;
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            if (instance == null) return null;

            try
            {
                var property = instance.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length != 0) return null;
                return property.GetValue(instance, null) as string;
            }
            catch
            {
                return null;
            }
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
                else if (type.Name == "IObjectExplorerService" && _objectExplorerServiceType == null)
                {
                    _objectExplorerServiceType = type;
                    Log.Info("[补全] 找到 Object Explorer 服务接口: " + type.FullName);
                }
                else if (type.Name == "INodeContext" && _nodeContextType == null)
                {
                    var connectionProperty = type.GetProperty("Connection");
                    if (connectionProperty != null)
                    {
                        _nodeContextType = type;
                        _nodeContextConnection = connectionProperty;
                        _nodeContextUrnPath = type.GetProperty("UrnPath");
                    }
                }
                else if (type.Name == "INodeInformation" && _nodeInformationItem == null)
                {
                    _nodeInformationItem = type.GetProperty("Item");
                    _nodeInformationName = type.GetProperty("Name");
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
