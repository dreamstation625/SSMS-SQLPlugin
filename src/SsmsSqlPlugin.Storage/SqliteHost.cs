using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SsmsSqlPlugin.Storage
{
    /// <summary>
    /// 宿主 SQLitePCLRaw + SQLite-net 的反射入口。
    /// 不引用、不打包任何 SQLite 程序集/native，全部使用 SSMS 自带那一套。
    /// </summary>
    internal sealed class SqliteHost
    {
        private static readonly object Gate = new object();
        private static bool _initialized;
        private static string _initError;
        private static Type _connectionType;
        private static ConstructorInfo _constructor;
        private static MethodInfo _execute;
        private static MethodInfo _executeScalar;
        private static MethodInfo _query;
        private static MethodInfo _close;
        private static bool _resolverRegistered;
        private static readonly List<string> ProbeDirs = new List<string>();

        public string Error { get; private set; }

        public bool IsReady
            => _connectionType != null && _constructor != null &&
               _execute != null && _executeScalar != null && _query != null;

        public SqliteHost(string sqliteDirectory)
        {
            try
            {
                EnsureInitialized(sqliteDirectory);
            }
            catch (Exception ex)
            {
                Error = Describe(ex);
            }

            if (!IsReady) Error = Error ?? _initError ?? "SQLite 宿主程序集未就绪";
        }

        private static void EnsureInitialized(string sqliteDirectory)
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;

                string dir = sqliteDirectory;
                if (string.IsNullOrEmpty(dir)) dir = FindDefaultDirectory();
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    _initError = "找不到宿主 SQLite 目录";
                    return;
                }

                try
                {
                    RegisterResolver(dir);

                    Assembly.LoadFrom(Path.Combine(dir, "SQLitePCLRaw.core.dll"));
                    Assembly.LoadFrom(Path.Combine(dir, "SQLitePCLRaw.provider.e_sqlite3.dll"));

                    var batteries = Assembly.LoadFrom(
                        Path.Combine(dir, "SQLitePCLRaw.batteries_v2.dll"));

                    var init = batteries.GetType("SQLitePCL.Batteries_V2")
                        ?.GetMethod("Init", Type.EmptyTypes);
                    if (init == null) throw new InvalidOperationException("找不到 Batteries_V2.Init");
                    init.Invoke(null, null);

                    var net = Assembly.LoadFrom(Path.Combine(dir, "SQLite-net.dll"));
                    _connectionType = net.GetType("SQLite.SQLiteConnection");
                    if (_connectionType == null)
                        throw new InvalidOperationException("找不到 SQLite.SQLiteConnection");

                    foreach (var ctor in _connectionType.GetConstructors())
                    {
                        var ps = ctor.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                        {
                            _constructor = ctor;
                            break;
                        }
                    }

                    if (_constructor == null)
                        throw new InvalidOperationException("SQLiteConnection 没有 (string, ...) 构造函数");

                    _execute = FindMethod("Execute", false);
                    _executeScalar = FindMethod("ExecuteScalar", true);
                    _query = FindMethod("Query", true);

                    _close = _connectionType.GetMethod("Dispose", Type.EmptyTypes)
                             ?? _connectionType.GetMethod("Close", Type.EmptyTypes);

                    if (_execute == null || _executeScalar == null || _query == null)
                        throw new InvalidOperationException("SQLiteConnection 方法签名不完整");
                }
                catch (Exception ex)
                {
                    _initError = Describe(ex);
                    _connectionType = null;
                    _constructor = null;
                }
            }
        }

        private static void RegisterResolver(string sqliteDir)
        {
            if (_resolverRegistered) return;
            _resolverRegistered = true;

            ProbeDirs.Add(sqliteDir);
            ProbeDirs.Add(Path.GetDirectoryName(sqliteDir));
            ProbeDirs.Add(Path.GetDirectoryName(Path.GetDirectoryName(sqliteDir)));

            AppDomain.CurrentDomain.AssemblyResolve += ResolveHostAssembly;
        }

        /// <summary>
        /// CLI 进程没有 SSMS.exe.config 的探测路径；System.Memory 等依赖
        /// 需要从宿主 PublicAssemblies 解析。SSMS 内这个 handler 也无害。
        /// </summary>
        private static Assembly ResolveHostAssembly(object sender, ResolveEventArgs e)
        {
            string simpleName;
            try { simpleName = new AssemblyName(e.Name).Name; }
            catch { return null; }

            foreach (string dir in ProbeDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;

                string candidate = Path.Combine(dir, simpleName + ".dll");
                if (!File.Exists(candidate)) continue;

                try { return Assembly.LoadFrom(candidate); }
                catch { }
            }

            return null;
        }

        private static MethodInfo FindMethod(string name, bool generic)
        {
            foreach (var method in _connectionType.GetMethods())
            {
                if (method.Name != name) continue;
                if (method.IsGenericMethodDefinition != generic) continue;

                var ps = method.GetParameters();
                if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                return method;
            }
            return null;
        }

        private static string FindDefaultDirectory()
        {
            try
            {
                string dir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "PublicAssemblies", "SQLite");
                if (Directory.Exists(dir)) return dir;
            }
            catch { }
            return null;
        }

        public object Open(string databasePath)
        {
            if (!IsReady) throw new InvalidOperationException(Error ?? "SQLite 未就绪");

            var ps = _constructor.GetParameters();
            object[] args = ps.Length == 1
                ? new object[] { databasePath }
                : new object[] { databasePath, true };

            return _constructor.Invoke(args);
        }

        public int Execute(object connection, string sql, params object[] args)
        {
            object result = Invoke(_execute, connection, sql, args);
            return result == null ? 0 : Convert.ToInt32(result);
        }

        public T ExecuteScalar<T>(object connection, string sql, params object[] args)
        {
            var method = _executeScalar.MakeGenericMethod(typeof(T));
            object result = Invoke(method, connection, sql, args);
            return result == null ? default(T) : (T)result;
        }

        public List<T> Query<T>(object connection, string sql, params object[] args)
        {
            var method = _query.MakeGenericMethod(typeof(T));
            object result = Invoke(method, connection, sql, args);
            return result as List<T> ?? new List<T>();
        }

        public void Close(object connection)
        {
            try { _close?.Invoke(connection, null); }
            catch { }
        }

        private static object Invoke(MethodInfo method, object connection, string sql, object[] args)
        {
            var ps = method.GetParameters();
            object[] invokeArgs = ps.Length == 1
                ? new object[] { sql }
                : new object[] { sql, args ?? new object[0] };

            return method.Invoke(connection, invokeArgs);
        }

        private static string Describe(Exception ex)
        {
            var cur = ex;
            while (cur.InnerException != null) cur = cur.InnerException;
            return cur.Message;
        }
    }
}
