using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SsmsSqlPlugin.Probe
{
    /// <summary>
    /// M0 探针：验证 VSIX 能装进 SSMS 22.11，并且托管代码会被执行。
    ///
    /// 0.0.6 新增：CTC 路线已被证实在 SSMS 22.11 上不生效，改为
    /// 「运行时命令注入」——用 IVsProfferCommands.AddNamedCommand 注册命名命令，
    /// 再用 DTE CommandBars 把按钮挂到工具菜单。这条路径不依赖 .ctmenu 资源。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ProbePackage : AsyncPackage, IOleCommandTarget
    {
        public const string PackageGuidString = "0a88c9e6-4e5f-4d80-9f6a-f88e3f8270bb";

        public static readonly Guid CommandSet = new Guid("d7f23a60-94cf-4718-a2fa-0b5477ffe20e");
        public const uint CmdIdPing = 0x0100;

        /// <summary>运行时注册时 Shell 分配的真正命令 ID。</summary>
        private static uint _runtimeCmdId;
        private const string CanonicalName = "SsmsSqlPlugin.Ping";

        private const int S_OK = 0;
        private const int OleCmdErrNotSupported = unchecked((int)0x80040100);
        private const uint OleCmdfSupported = 0x1;
        private const uint OleCmdfEnabled = 0x2;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            ProbeLog.Write("=== InitializeAsync 进入（自动加载路径）===");
            LogEnvironment();

            await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);

            // 菜单/命令栏操作必须在 UI 线程
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                TryRegisterRuntimeMenu();
                DumpCompletionEnvironment();
                DumpEditorService();
                DumpActiveTextView();
                ProbeSqlite();
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!!! 运行时菜单注册异常: " + ex);
            }
        }

        /// <summary>
        /// M0-2 探测：列出 SQL 相关内容类型，以及 MEF 里注册了多少个补全 provider。
        /// 如果这里能看到 ProbeCompletionSourceProvider，说明 MEF 导出已生效。
        /// </summary>
        private void DumpCompletionEnvironment()
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== 补全环境探测 ===");

            try
            {
                Microsoft.VisualStudio.ComponentModelHost.IComponentModel compModel =
                    GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
                    as Microsoft.VisualStudio.ComponentModelHost.IComponentModel;

                if (compModel == null)
                {
                    ProbeLog.Write("!! SComponentModel 不可用，MEF 容器拿不到");
                    return;
                }
                ProbeLog.Write("IComponentModel OK");

                // ---- 1. SQL 相关的内容类型 ----
                try
                {
                    var ctRegistry = compModel.GetService<Microsoft.VisualStudio.Utilities.IContentTypeRegistryService>();
                    if (ctRegistry == null)
                    {
                        ProbeLog.Write("!! IContentTypeRegistryService 不可用");
                    }
                    else
                    {
                        int total = 0;
                        int hits = 0;
                        foreach (var ct in ctRegistry.ContentTypes)
                        {
                            total++;
                            string n = ct.TypeName ?? "";
                            if (n.IndexOf("sql", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("tsql", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                ProbeLog.Write("  [CT] " + n);
                                hits++;
                            }
                        }
                        ProbeLog.Write("内容类型总数=" + total + "，SQL 相关=" + hits);

                        // ---- 1.1 决定性判断：SQL 内容类型是否支持异步补全 ----
                        try
                        {
                            var broker = compModel.GetService<
                                Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.IAsyncCompletionBroker>();
                            if (broker == null)
                            {
                                ProbeLog.Write("!! IAsyncCompletionBroker 不可用");
                            }
                            else
                            {
                                ProbeLog.Write("IAsyncCompletionBroker OK: " + broker.GetType().FullName);
                                foreach (var name in new[] { "SQL", "sql", "code", "text" })
                                {
                                    var ct = ctRegistry.GetContentType(name);
                                    if (ct == null)
                                    {
                                        ProbeLog.Write("  IsCompletionSupported(" + name + ") = 内容类型不存在");
                                        continue;
                                    }
                                    bool ok = broker.IsCompletionSupported(ct);
                                    ProbeLog.Write("  IsCompletionSupported(" + ct.TypeName + ") = " + ok);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ProbeLog.Write("!! 检查补全支持失败: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ProbeLog.Write("!! 枚举内容类型失败: " + ex.Message);
                }

                // ---- 2. 已注册的补全 provider ----
                try
                {
                    var providers = compModel.GetExtensions<
                        Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.IAsyncCompletionSourceProvider>();
                    int pcount = 0;
                    foreach (var p in providers)
                    {
                        ProbeLog.Write("  [Provider] " + p.GetType().FullName);
                        pcount++;
                    }
                    ProbeLog.Write("IAsyncCompletionSourceProvider 实现数 = " + pcount);
                }
                catch (Exception ex)
                {
                    ProbeLog.Write("!! 枚举补全 provider 失败: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! 补全环境探测异常: " + ex);
            }
        }

        /// <summary>
        /// 运行时注入：注册命名命令 + 挂到工具菜单。
        /// 每一步都写日志，失败时能直接看出卡在哪一环。
        /// </summary>
        private void TryRegisterRuntimeMenu()
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== 开始运行时菜单注册 ===");

            // ---- 步骤 1：拿 IVsProfferCommands ----
            IVsProfferCommands3 proffer = null;
            try
            {
                proffer = GetService(typeof(SVsProfferCommands)) as IVsProfferCommands3;
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! GetService(SVsProfferCommands) 抛异常: " + ex.Message);
            }

            if (proffer == null)
            {
                ProbeLog.Write("!! SVsProfferCommands 不可用，运行时注入无法继续");
                return;
            }
            ProbeLog.Write("步骤1 OK  IVsProfferCommands = " + proffer.GetType().FullName);

            // ---- 步骤 2：注册命名命令 ----
            uint cmdId = 0;
            try
            {
                Guid pkgGuid = new Guid(PackageGuidString);
                Guid cmdSet = CommandSet;
                uint flags = 0;
                uint uiContexts = 0;
                uint uiElementType = 1;   // msoControlButton
                uint cmdFlags = 0;
                Guid[] contextGuids = new Guid[0];

                int hr = proffer.AddNamedCommand(
                    ref pkgGuid,
                    ref cmdSet,
                    CanonicalName,
                    out cmdId,
                    "SSMS-SQLPlugin: Ping",
                    "SSMS-SQLPlugin: Ping",
                    "M0 probe: runtime command injection",
                    null,             // pszBmpFile
                    flags,            // dwCmdFlagsDefault
                    uiContexts,       // cUIContexts
                    uiElementType,    // dwUIElementType
                    cmdFlags,         // dwCmdFlags
                    contextGuids);    // rgguidUIContexts

                ProbeLog.Write("步骤2     AddNamedCommand hr=0x" + hr.ToString("X8") + "  cmdId=0x" + cmdId.ToString("X4"));
                // S_OK(0) = 新注册；S_FALSE(1) = 已存在（跨会话记住）。两者都算成功。
                if (hr < 0)
                {
                    ProbeLog.Write("!! AddNamedCommand 失败（hr<0），停止");
                    return;
                }
                _runtimeCmdId = cmdId;
                ProbeLog.Write("步骤2 OK  命令已注册（" + (hr == 0 ? "新注册" : "已存在") + "）");
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! AddNamedCommand 抛异常: " + ex);
                return;
            }

            // ---- 步骤 3：拿 DTE / CommandBars ----
            object menuBar = null;
            try
            {
                EnvDTE.DTE dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null)
                {
                    ProbeLog.Write("!! DTE 不可用");
                    return;
                }
                ProbeLog.Write("步骤3 OK  DTE = " + dte.Name + " / " + dte.Version);

                // 注意：SSMS 22.11 的 CommandBars 是 VS 自己的 Automation 包装
                // (Microsoft.VisualStudio.PlatformUI.Automation.*)，不支持 IDispatch 晚绑定，
                // dynamic 访问 Caption/Name 会抛 RuntimeBinderException。必须用强类型接口调 vtable。
                Microsoft.VisualStudio.CommandBars.CommandBars cmdBars =
                    dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
                if (cmdBars == null)
                {
                    ProbeLog.Write("!! dte.CommandBars 无法转换为强类型 CommandBars（实际类型 " +
                                   (dte.CommandBars == null ? "null" : dte.CommandBars.GetType().FullName) + "）");
                    return;
                }

                Microsoft.VisualStudio.CommandBars.CommandBar bar = cmdBars["MenuBar"];
                menuBar = bar;
                ProbeLog.Write("步骤3     强类型 MenuBar OK，Controls 数 = " + bar.Controls.Count);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! DTE/CommandBars 阶段异常: " + ex);
                return;
            }

            if (menuBar == null)
            {
                return;
            }

            // ---- 步骤 4：把命令挂到工具菜单 ----
            // AddCommandBarControl 在 SSMS 22.11 返回 E_NOINTERFACE(0x80004002)，
            // 改为直接在 CommandBarPopup.Controls 上创建按钮并订阅 Click —— 不依赖命令系统。
            try
            {
                Microsoft.VisualStudio.CommandBars.CommandBar bar =
                    (Microsoft.VisualStudio.CommandBars.CommandBar)menuBar;
                var controls = bar.Controls;
                int count = controls.Count;
                Microsoft.VisualStudio.CommandBars.CommandBarPopup toolsPopup = null;

                for (int i = 1; i <= count; i++)
                {
                    Microsoft.VisualStudio.CommandBars.CommandBarControl c = controls[i];
                    string cap = "";
                    try { cap = c.Caption; } catch { }
                    ProbeLog.Write("            [" + i + "] type=" + c.Type + " Caption=" + cap);

                    string norm = (cap ?? "").Replace("&", "").Replace("(", "").Replace(")", "").Trim();
                    if (toolsPopup == null && (norm.StartsWith("工具") || norm.StartsWith("Tools")))
                    {
                        toolsPopup = c as Microsoft.VisualStudio.CommandBars.CommandBarPopup;
                        ProbeLog.Write("步骤4     命中工具菜单: 索引 " + i + " Caption=" + cap +
                                       " cast=" + (toolsPopup != null));
                    }
                }

                if (toolsPopup == null)
                {
                    ProbeLog.Write("!! 没找到工具菜单 popup，放弃挂载");
                    return;
                }

                Microsoft.VisualStudio.CommandBars.CommandBarButton btn =
                    (Microsoft.VisualStudio.CommandBars.CommandBarButton)toolsPopup.Controls.Add(
                        Microsoft.VisualStudio.CommandBars.MsoControlType.msoControlButton,
                        Type.Missing, Type.Missing, 1, true);

                btn.Caption = "SSMS-SQLPlugin: Ping";
                btn.Tag = "SsmsSqlPluginProbe";
                btn.TooltipText = "M0 probe: runtime menu injection";
                btn.Visible = true;
                btn.Click += OnRuntimeMenuClick;

                ProbeLog.Write("步骤4 OK  按钮已挂到工具菜单，Caption=" + btn.Caption);
                ProbeLog.Write("步骤4     工具菜单现有子项数 = " + toolsPopup.Controls.Count);
                int n = toolsPopup.Controls.Count;
                for (int k = Math.Max(1, n - 2); k <= n; k++)
                {
                    try
                    {
                        Microsoft.VisualStudio.CommandBars.CommandBarControl kc = toolsPopup.Controls[k];
                        ProbeLog.Write("步骤4       子项[" + k + "] type=" + kc.Type +
                                       " Caption=" + kc.Caption + " Visible=" + kc.Visible);
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("步骤4       子项[" + k + "] 读取失败: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! 挂载菜单阶段异常: " + ex);
            }
        }

        /// <summary>
        /// M0-3 探测：SSMS 的连接契约。
        /// ISqlEditorService 是宿主内部接口，不能用编译期引用，因此全程反射：
        /// 先在已加载程序集里按简单名找类型，dump 出方法签名，再尝试取服务实例。
        /// </summary>
        private void DumpEditorService()
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== 连接契约探测（ISqlEditorService）===");

            try
            {
                // ---- 1. 在所有已加载程序集里按简单名找 ----
                Type svcType = null;
                string foundIn = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try
                    {
                        t = asm.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ISqlEditorService", false);
                    }
                    catch { }

                    if (t != null)
                    {
                        svcType = t;
                        foundIn = asm.GetName().Name;
                        break;
                    }
                }

                if (svcType == null)
                {
                    ProbeLog.Write("定点查找未命中，改为遍历已加载程序集...");
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try
                        {
                            types = asm.GetTypes();
                        }
                        catch (ReflectionTypeLoadException ex)
                        {
                            types = ex.Types;
                        }
                        catch { continue; }

                        if (types == null) continue;
                        foreach (var t in types)
                        {
                            if (t == null) continue;
                            if (t.Name == "ISqlEditorService")
                            {
                                svcType = t;
                                foundIn = asm.GetName().Name;
                                break;
                            }
                        }
                        if (svcType != null) break;
                    }
                }

                if (svcType == null)
                {
                    ProbeLog.Write("!! 未找到 ISqlEditorService 类型");
                    DumpSqlWorkbenchAssemblies();
                    return;
                }

                ProbeLog.Write("找到类型: " + svcType.FullName);
                ProbeLog.Write("所在程序集: " + foundIn + " / " + svcType.Assembly.GetName().Version);

                // ---- 2. dump 方法签名 ----
                var methods = svcType.GetMethods();
                ProbeLog.Write("方法数 = " + methods.Length);
                foreach (var m in methods)
                {
                    string ps = "";
                    foreach (var p in m.GetParameters())
                    {
                        if (ps.Length > 0) ps += ", ";
                        ps += p.ParameterType.Name + " " + p.Name;
                    }
                    ProbeLog.Write("  M: " + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
                }

                // ---- 3. 尝试取服务实例 ----
                object svc = null;
                try
                {
                    svc = GetService(svcType);
                }
                catch (Exception ex)
                {
                    ProbeLog.Write("GetService 抛异常: " + ex.Message);
                }

                if (svc == null)
                {
                    ProbeLog.Write("GetService 返回 null，尝试其他入口...");

                    // 入口 2：全局服务表
                    try
                    {
                        svc = Package.GetGlobalService(svcType);
                        ProbeLog.Write("  Package.GetGlobalService -> " + (svc == null ? "null" : svc.GetType().FullName));
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("  Package.GetGlobalService 抛异常: " + ex.Message);
                    }
                }

                if (svc == null)
                {
                    // 入口 3：MEF 导出（SSMS 可能把自己的服务放在 MEF 容器里）
                    ProbeLog.Write("  尝试 MEF 导出...");
                    try
                    {
                        var compModel = GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
                            as Microsoft.VisualStudio.ComponentModelHost.IComponentModel;
                        if (compModel == null)
                        {
                            ProbeLog.Write("    SComponentModel 不可用");
                        }
                        else
                        {
                            var exports = compModel.DefaultExportProvider.GetExports(svcType, null, null);
                            int n = 0;
                            foreach (var e in exports)
                            {
                                n++;
                                if (e.Value != null && svcType.IsAssignableFrom(e.Value.GetType()))
                                {
                                    svc = e.Value;
                                    ProbeLog.Write("    >>> MEF 导出命中: " + svc.GetType().FullName);
                                    break;
                                }
                            }
                            ProbeLog.Write("    MEF 导出数 = " + n + "，命中=" + (svc != null));
                        }
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("    MEF 查询失败: " + ex.Message);
                    }
                }

                if (svc == null)
                {
                    // 入口 4：在所有已加载程序集里找实现类 + 静态入口
                    ProbeLog.Write("  扫描全部已加载程序集找实现类...");
                    try
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            Type[] types;
                            try { types = asm.GetTypes(); }
                            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                            catch { continue; }
                            if (types == null) continue;

                            foreach (var t in types)
                            {
                                if (t == null || t.IsInterface || t.IsAbstract) continue;
                                if (!svcType.IsAssignableFrom(t)) continue;

                                ProbeLog.Write("  实现类: " + t.FullName + " @ " + asm.GetName().Name);

                                foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                                {
                                    try
                                    {
                                        object pv = p.GetValue(null, null);
                                        string nm = pv == null ? "null" : pv.GetType().Name;
                                        ProbeLog.Write("    静态属性 " + p.Name + " : " + p.PropertyType.Name + " = " + nm);
                                        if (pv != null && svcType.IsAssignableFrom(pv.GetType()))
                                        {
                                            svc = pv;
                                            ProbeLog.Write("    >>> 静态属性 " + p.Name + " 就是要的服务实例");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        ProbeLog.Write("    静态属性 " + p.Name + " 读取失败: " + ex.Message);
                                    }
                                }

                                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                                {
                                    try
                                    {
                                        object fv = f.GetValue(null);
                                        string nm = fv == null ? "null" : fv.GetType().Name;
                                        ProbeLog.Write("    静态字段 " + f.Name + " : " + f.FieldType.Name + " = " + nm);
                                        if (fv != null && svcType.IsAssignableFrom(fv.GetType()))
                                        {
                                            svc = fv;
                                            ProbeLog.Write("    >>> 静态字段 " + f.Name + " 就是要的服务实例");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        ProbeLog.Write("    静态字段 " + f.Name + " 读取失败: " + ex.Message);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("  扫描实现类异常: " + ex.Message);
                    }
                }

                if (svc == null)
                {
                    // 入口 5：用服务 ID 类 SSqlEditorService 直接查（VS 允许用 SXxx 类型当服务键）
                    ProbeLog.Write("  用 SSqlEditorService 服务 ID 类查...");
                    try
                    {
                        Type svcIdType = null;
                        foreach (var t in svcType.Assembly.GetTypes())
                        {
                            if (t != null && t.Name == "SSqlEditorService") { svcIdType = t; break; }
                        }

                        if (svcIdType == null)
                        {
                            ProbeLog.Write("    没有 SSqlEditorService 类型");
                        }
                        else
                        {
                            var gattr = svcIdType.GetCustomAttributes(typeof(GuidAttribute), false);
                            if (gattr != null && gattr.Length > 0)
                            {
                                ProbeLog.Write("    SSqlEditorService Guid = " + ((GuidAttribute)gattr[0]).Value);
                            }

                            try
                            {
                                object o = GetService(svcIdType);
                                ProbeLog.Write("    GetService(SSqlEditorService) -> " + (o == null ? "null" : o.GetType().FullName));
                                if (o != null && svcType.IsAssignableFrom(o.GetType())) svc = o;
                            }
                            catch (Exception ex) { ProbeLog.Write("    GetService(S类) 失败: " + ex.Message); }

                            if (svc == null)
                            {
                                try
                                {
                                    object o2 = Package.GetGlobalService(svcIdType);
                                    ProbeLog.Write("    GetGlobalService(SSqlEditorService) -> " + (o2 == null ? "null" : o2.GetType().FullName));
                                    if (o2 != null && svcType.IsAssignableFrom(o2.GetType())) svc = o2;
                                }
                                catch (Exception ex) { ProbeLog.Write("    GetGlobalService(S类) 失败: " + ex.Message); }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("    入口5 异常: " + ex.Message);
                    }
                }

                if (svc == null)
                {
                    ProbeLog.Write("!! 仍未取得服务实例");
                    return;
                }

                ProbeLog.Write("服务实例 OK: " + svc.GetType().FullName);

                // ---- 3.1 如果拿到的是 DbConnection，把连接信息打出来 ----
                try
                {
                    var dbConn = svc.GetType().GetMethod("GetCurrentConnection").Invoke(svc, null);
                    if (dbConn != null)
                    {
                        ProbeLog.Write("  [连接对象] " + dbConn.GetType().FullName);
                        foreach (var pn in new[] { "DataSource", "ServerVersion", "Database", "State",
                                                   "ConnectionTimeout", "WorkstationId", "PacketSize" })
                        {
                            try
                            {
                                var pi = dbConn.GetType().GetProperty(pn);
                                if (pi == null) continue;
                                object v = pi.GetValue(dbConn, null);
                                ProbeLog.Write("    " + pn + " = " + (v == null ? "null" : v.ToString()));
                            }
                            catch (Exception ex)
                            {
                                ProbeLog.Write("    " + pn + " 读取失败: " + (ex.InnerException ?? ex).Message);
                            }
                        }

                        // 连接字符串（去掉密码）
                        try
                        {
                            var csProp = dbConn.GetType().GetProperty("ConnectionString");
                            if (csProp != null)
                            {
                                string cs = (string)csProp.GetValue(dbConn, null);
                                if (!string.IsNullOrEmpty(cs))
                                {
                                    var b = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };
                                    if (b.ContainsKey("Password")) b["Password"] = "***";
                                    if (b.ContainsKey("Pwd")) b["Pwd"] = "***";
                                    ProbeLog.Write("    ConnectionString = " + b.ConnectionString);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ProbeLog.Write("    ConnectionString 读取失败: " + ex.Message);
                        }

                        // 实测一次往返耗时
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            var cmd = dbConn.GetType().GetProperty("CreateCommand");
                            object dbCmd = dbConn.GetType().GetMethod("CreateCommand", Type.EmptyTypes).Invoke(dbConn, null);
                            dbCmd.GetType().GetProperty("CommandText").SetValue(dbCmd, "SELECT DB_NAME()", null);
                            object r = dbCmd.GetType().GetMethod("ExecuteScalar", Type.EmptyTypes).Invoke(dbCmd, null);
                            sw.Stop();
                            ProbeLog.Write("    往返 SELECT DB_NAME() -> " + r + "（耗时 " + sw.ElapsedMilliseconds + " ms）");

                            var disposeCmd = dbCmd as IDisposable;
                            if (disposeCmd != null) disposeCmd.Dispose();
                        }
                        catch (Exception ex)
                        {
                            ProbeLog.Write("    往返查询失败: " + (ex.InnerException ?? ex).Message);
                        }
                    }
                    else
                    {
                        ProbeLog.Write("  [连接对象] null（当前窗口未连接数据库）");
                    }
                }
                catch (Exception ex)
                {
                    ProbeLog.Write("  连接信息 dump 异常: " + (ex.InnerException ?? ex).Message);
                }

                // ---- 4. 对无参方法逐个试调，只打印结果类型 ----
                foreach (var m in methods)
                {
                    if (m.GetParameters().Length != 0) continue;
                    if (m.IsSpecialName) continue;   // 跳过属性 getter，单独处理
                    try
                    {
                        object r = m.Invoke(svc, null);
                        string desc = r == null ? "null" : r.GetType().Name + " = " + SafeToString(r);
                        ProbeLog.Write("  调用 " + m.Name + "() -> " + desc);
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("  调用 " + m.Name + "() 失败: " + (ex.InnerException ?? ex).Message);
                    }
                }

                // ---- 5. 属性 ----
                foreach (var p in svcType.GetProperties())
                {
                    try
                    {
                        object v = p.GetValue(svc, null);
                        ProbeLog.Write("  属性 " + p.Name + " = " + (v == null ? "null" : SafeToString(v)));
                    }
                    catch (Exception ex)
                    {
                        ProbeLog.Write("  属性 " + p.Name + " 读取失败: " + (ex.InnerException ?? ex).Message);
                    }
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! 连接契约探测异常: " + ex);
            }
        }

        /// <summary>
        /// M0-3 探测（不依赖键盘焦点）：通过 IVsTextManager 拿当前活动视图，
        /// 转成 ITextView 后 dump 属性集合，看连接信息挂在哪里。
        /// </summary>
        private void DumpActiveTextView()
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== 活动视图探测（IVsTextManager 路径）===");

            try
            {
                var tm = GetService(typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager))
                         as Microsoft.VisualStudio.TextManager.Interop.IVsTextManager;
                if (tm == null)
                {
                    ProbeLog.Write("!! SVsTextManager 不可用");
                    return;
                }
                ProbeLog.Write("SVsTextManager OK");

                Microsoft.VisualStudio.TextManager.Interop.IVsTextView vsView = null;
                int hr = tm.GetActiveView(1, null, out vsView);
                ProbeLog.Write("GetActiveView hr=0x" + hr.ToString("X8") +
                               " view=" + (vsView == null ? "null（可能没有活动文档）" : "OK"));
                if (vsView == null) return;

                var compModel = GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
                                as Microsoft.VisualStudio.ComponentModelHost.IComponentModel;
                if (compModel == null)
                {
                    ProbeLog.Write("!! SComponentModel 不可用");
                    return;
                }

                Microsoft.VisualStudio.Text.Editor.ITextView tv = null;
                try
                {
                    var adapter = compModel.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>();
                    if (adapter == null)
                    {
                        ProbeLog.Write("!! IVsEditorAdaptersFactoryService 为 null");
                        return;
                    }
                    ProbeLog.Write("适配器服务 OK");
                    tv = adapter.GetWpfTextView(vsView);
                }
                catch (Exception ex)
                {
                    ProbeLog.Write("!! 取 WpfTextView 失败: " + ex.Message);
                    return;
                }

                if (tv == null)
                {
                    ProbeLog.Write("!! GetWpfTextView 返回 null");
                    return;
                }

                ProbeLog.Write("WpfTextView OK: " + tv.GetType().FullName);

                // M0-4：挂命令过滤器（需要 ITextView 才能在命中执行命令时抓 SQL）
                InstallCommandFilter(vsView);

                ProbeCompletionSourceProvider.DumpTextViewProperties(tv);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! 活动视图探测异常: " + ex);
            }
        }

        private static bool _filterInstalled;

        /// <summary>
        /// M0-4：把记录器挂到编辑器的命令链上。
        /// 之后按 F5 / 点执行按钮，命令都会先经过这里。
        /// </summary>
        private void InstallCommandFilter(Microsoft.VisualStudio.TextManager.Interop.IVsTextView vsView)
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== 命令过滤器安装（M0-4）===");

            if (_filterInstalled)
            {
                ProbeLog.Write("已经装过了，跳过");
                return;
            }

            try
            {
                var filter = new CommandFilter(vsView);
                IOleCommandTarget next;
                int hr = vsView.AddCommandFilter(filter, out next);
                filter.Next = next;

                ProbeLog.Write("AddCommandFilter hr=0x" + hr.ToString("X8") +
                               " next=" + (next == null ? "null" : next.GetType().Name));
                _filterInstalled = hr == 0;
                if (_filterInstalled)
                {
                    ProbeLog.Write("过滤器已生效，接下来按 F5 或点执行按钮，命令会记录到上面的 [CMD] 行");
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!! 安装命令过滤器失败: " + ex.Message);
            }
        }

        /// <summary>
        /// M0-5 探测：SQLite 在 SSMS 进程里能不能用。
        /// SSMS 自带 SQLitePCLRaw + e_sqlite3.dll（PublicAssemblies\SQLite）。
        /// 这里先试「复用宿主那一套」，全程反射，不引入编译期依赖。
        /// </summary>
        private void ProbeSqlite()
        {
            ProbeLog.Write("");
            ProbeLog.Write("=== SQLite 探测（M0-5）===");

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PublicAssemblies", "SQLite");
            ProbeLog.Write("SQLite 目录: " + dir);
            if (!Directory.Exists(dir))
            {
                ProbeLog.Write("!! 目录不存在");
                return;
            }

            // ---- 1. 进程里是否已有 sqlite native 模块 ----
            try
            {
                int n = 0;
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    if (m.ModuleName.IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ProbeLog.Write("  [已加载 native] " + m.ModuleName + " @ " + m.FileName);
                        n++;
                    }
                }
                ProbeLog.Write("  已加载 sqlite native 模块数 = " + n);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  枚举模块失败: " + ex.Message);
            }

            // ---- 2. Batteries_V2.Init() ----
            try
            {
                Assembly.LoadFrom(Path.Combine(dir, "SQLitePCLRaw.core.dll"));
                Assembly.LoadFrom(Path.Combine(dir, "SQLitePCLRaw.provider.e_sqlite3.dll"));
                var batAsm = Assembly.LoadFrom(Path.Combine(dir, "SQLitePCLRaw.batteries_v2.dll"));
                Type batType = batAsm.GetType("SQLitePCL.Batteries_V2");
                if (batType == null)
                {
                    ProbeLog.Write("!! 找不到 SQLitePCL.Batteries_V2");
                    return;
                }
                var init = batType.GetMethod("Init", Type.EmptyTypes);
                init.Invoke(null, null);
                ProbeLog.Write("  Batteries_V2.Init() OK");

                // Init 之后再查一次 native 模块（此时 e_sqlite3 才被加载）
                try
                {
                    int n2 = 0;
                    foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                    {
                        if (m.ModuleName.IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ProbeLog.Write("  [Init 后 native] " + m.ModuleName + " @ " + m.FileName);
                            n2++;
                        }
                    }
                    ProbeLog.Write("  Init 后 sqlite native 模块数 = " + n2);
                }
                catch { }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  !! Init 失败: " + (ex.InnerException ?? ex));
                return;
            }

            // ---- 3. 打开内存库并查询 ----
            object conn = null;
            Type connType = null;
            try
            {
                var netAsm = Assembly.LoadFrom(Path.Combine(dir, "SQLite-net.dll"));
                connType = netAsm.GetType("SQLite.SQLiteConnection");
                if (connType == null)
                {
                    ProbeLog.Write("!! 找不到 SQLite.SQLiteConnection");
                    return;
                }

                System.Reflection.ConstructorInfo ctor = null;
                foreach (var c in connType.GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                    {
                        ctor = c;
                        break;
                    }
                }
                if (ctor == null)
                {
                    ProbeLog.Write("!! 没有 (string, ...) 构造函数");
                    return;
                }

                int pc = ctor.GetParameters().Length;
                object[] args = pc == 1 ? new object[] { ":memory:" } : new object[] { ":memory:", true };
                conn = ctor.Invoke(args);
                ProbeLog.Write("  SQLiteConnection(:memory:) OK  ctor 参数数=" + pc);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  !! 打开内存库失败: " + (ex.InnerException ?? ex));
                return;
            }

            // ---- 4. 执行 SQL ----
            try
            {
                object ver = InvokeExecuteScalar(connType, conn, "SELECT sqlite_version()");
                ProbeLog.Write("  SELECT sqlite_version() -> " + ver);

                object n1 = InvokeExecuteScalar(connType, conn, "SELECT 1 + 1");
                ProbeLog.Write("  SELECT 1 + 1 -> " + n1);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  !! 查询失败: " + (ex.InnerException ?? ex));
            }

            // ---- 5. 建表 / 写入 / 读回 ----
            try
            {
                InvokeExecute(connType, conn, "CREATE TABLE t (id INTEGER PRIMARY KEY, sql_text TEXT)");
                InvokeExecute(connType, conn, "INSERT INTO t (sql_text) VALUES ('SELECT 1')");
                InvokeExecute(connType, conn, "INSERT INTO t (sql_text) VALUES ('SELECT 2')");
                object cnt = InvokeExecuteScalar(connType, conn, "SELECT COUNT(*) FROM t");
                object first = InvokeExecuteScalar(connType, conn, "SELECT sql_text FROM t ORDER BY id LIMIT 1");
                ProbeLog.Write("  建表/写入/读回 OK：count=" + cnt + " first=" + first);
                ProbeLog.Write("  >>> SQLite 在 SSMS 进程内可用（复用宿主 native）");
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  !! 建表写入失败: " + (ex.InnerException ?? ex));
            }

            // ---- 6. 收尾 ----
            try
            {
                var dispose = connType.GetMethod("Dispose", Type.EmptyTypes);
                if (dispose != null) dispose.Invoke(conn, null);
                else connType.GetMethod("Close", Type.EmptyTypes)?.Invoke(conn, null);
                ProbeLog.Write("  已关闭连接");
            }
            catch (Exception ex)
            {
                ProbeLog.Write("  关闭失败: " + ex.Message);
            }
        }

        /// <summary>反射调用 SQLiteConnection.ExecuteScalar&lt;T&gt;(string, params object[])。</summary>
        private static object InvokeExecuteScalar(Type connType, object conn, string sql)
        {
            foreach (var m in connType.GetMethods())
            {
                if (m.Name != "ExecuteScalar" || !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;

                var g = m.MakeGenericMethod(typeof(string));
                object[] args = ps.Length == 1
                    ? new object[] { sql }
                    : new object[] { sql, new object[0] };   // params object[] 传空数组
                return g.Invoke(conn, args);
            }
            throw new MissingMethodException("找不到 ExecuteScalar<T>(string, params object[])");
        }

        /// <summary>反射调用 SQLiteConnection.Execute(string, params object[])。</summary>
        private static object InvokeExecute(Type connType, object conn, string sql)
        {
            foreach (var m in connType.GetMethods())
            {
                if (m.Name != "Execute" || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;

                object[] args = ps.Length == 1
                    ? new object[] { sql }
                    : new object[] { sql, new object[0] };
                return m.Invoke(conn, args);
            }
            throw new MissingMethodException("找不到 Execute(string, params object[])");
        }

        /// <summary>把对象转成可读字符串，COM 对象只取类型名避免触发副作用。</summary>
        private static string SafeToString(object o)
        {
            try
            {
                string s = o.ToString();
                if (s != null && s.Length > 160) s = s.Substring(0, 160) + "...";
                return s;
            }
            catch
            {
                return "(ToString 失败)";
            }
        }

        /// <summary>诊断用：列出与 SqlWorkbench 相关的已加载程序集。</summary>
        private static void DumpSqlWorkbenchAssemblies()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string n = asm.GetName().Name;
                    if (n.IndexOf("SqlWorkbench", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("VSIntegration", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ProbeLog.Write("  [ASM] " + n + " @ " + asm.Location);
                    }
                }
                catch { }
            }
        }

        /// <summary>运行时按钮的点击回调。</summary>
        private void OnRuntimeMenuClick(
            Microsoft.VisualStudio.CommandBars.CommandBarButton ctrl,
            ref bool cancelDefault)
        {
            try
            {
                ProbeLog.Write("=== 运行时菜单按钮被点击 ===");
                LogEnvironment();
                // 点击时文档肯定已打开，适合做活动视图/连接探测
                DumpActiveTextView();
                DumpEditorService();
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!!! 点击回调异常: " + ex.Message);
            }
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == CommandSet && cCmds > 0 &&
                (prgCmds[0].cmdID == CmdIdPing || prgCmds[0].cmdID == _runtimeCmdId))
            {
                prgCmds[0].cmdf = OleCmdfSupported | OleCmdfEnabled;
                return S_OK;
            }

            return OleCmdErrNotSupported;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == CommandSet && (nCmdID == CmdIdPing || nCmdID == _runtimeCmdId))
            {
                ProbeLog.Write("=== 菜单命令被点击（nCmdID=0x" + nCmdID.ToString("X4") + "）===");
                LogEnvironment();
                return S_OK;
            }

            return OleCmdErrNotSupported;
        }

        private static void LogEnvironment()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                ProbeLog.Write("扩展程序集 : " + asm.Location);
                ProbeLog.Write("程序集版本 : " + asm.GetName().Version);
                ProbeLog.Write("宿主进程   : " + Process.GetCurrentProcess().ProcessName);
                ProbeLog.Write("位数       : " + (Environment.Is64BitProcess ? "x64" : "x86"));
                ProbeLog.Write("CLR 版本   : " + Environment.Version);

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                ProbeLog.Write("宿主目录   : " + baseDir);
                ProbeLog.Write("SSMS.exe   : " + FileVersion(Path.Combine(baseDir, "SSMS.exe")));

                var shellAsm = typeof(AsyncPackage).Assembly;
                ProbeLog.Write("Shell 15.0 : " + shellAsm.GetName().Version + " @ " + shellAsm.Location);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("!!! 探针异常: " + ex);
            }
        }

        private static string FileVersion(string path)
        {
            try
            {
                return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : "(不存在)";
            }
            catch (Exception ex)
            {
                return "(读取失败: " + ex.Message + ")";
            }
        }
    }
}
