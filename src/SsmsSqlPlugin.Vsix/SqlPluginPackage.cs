using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using SsmsSqlPlugin.Completion;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.History;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Localization;
using SsmsSqlPlugin.Menus;
using SsmsSqlPlugin.Settings;

namespace SsmsSqlPlugin
{
    /// <summary>
    /// SSMS-SQLPlugin 主包。
    ///
    /// 职责边界（刻意保持很小）：
    ///   · 初始化时注入 UI 入口（顶部菜单 + 右键菜单 + 快捷键）
    ///   · 提供"格式化当前编辑器"这一个动作
    ///   · 其余能力（历史、补全、AI）后续按模块挂进来
    ///
    /// ⚠ 三条架构硬性规则（前两条来自 M0 实测，违反会静默失败）：
    ///   1) 这个类的**任何**可见成员签名 / 字段 / 属性里都不能出现
    ///      Microsoft.VisualStudio.Text.Editor.ITextView。
    ///      一旦出现，SSMS 会静默不加载整个包，日志一个字都没有（0.0.10 事故）。
    ///   2) pkgdef 的 InprocServer32 必须指 $WinDir$\SYSTEM32\MSCOREE.DLL，
    ///      后台加载键名是 AllowsBackgroundLoad（不是 AllowsBackgroundLoading）。
    ///   3) 菜单不用 CTC/VSCT —— SSMS 22 上这条路不通，改运行时注入（见 Menus/）。
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class SqlPluginPackage : AsyncPackage
    {
        public const string PackageGuidString = "0a88c9e6-4e5f-4d80-9f6a-f88e3f8270bb";

        /// <summary>命令集 GUID（运行时注入时用，与 pkgdef 无关）。</summary>
        public static readonly Guid CommandSet = new Guid("d7f23a60-94cf-4718-a2fa-0b5477ffe20e");

        private TopLevelMenu _topMenu;
        private EditorContextMenu _contextMenu;
        private IVsRunningDocumentTable _rdt;

        /// <summary>已经装过过滤器的视图（用对象身份哈希去重）。</summary>
        private readonly System.Collections.Generic.HashSet<int> _filteredViews
            = new System.Collections.Generic.HashSet<int>();

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            // 语言必须在创建任何窗口/菜单之前定下来；改语言后重启 SSMS 生效。
            Loc.Configure(FormatOptionsStore.Current.UiLanguage);

            Log.Info("================ 包初始化开始 ================");
            Log.Info("程序集: " + typeof(SqlPluginPackage).Assembly.Location);
            Log.Info("版本  : " + typeof(SqlPluginPackage).Assembly.GetName().Version);
            Log.Info("[补全] 探针开关 SQLKIT_COMPLETION_PROBE=" +
                     (Environment.GetEnvironmentVariable("SQLKIT_COMPLETION_PROBE") ?? "(null)") +
                     " EXCLUSIVE=" +
                     (Environment.GetEnvironmentVariable("SQLKIT_COMPLETION_EXCLUSIVE") ?? "(null)"));
            Log.Info("进程  : " + Process.GetCurrentProcess().ProcessName +
                     " / " + (Environment.Is64BitProcess ? "x64" : "x86"));

            await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);

            // 全局 ServiceProvider，供静态工具类取服务
            ServiceProviderHolder.Provider = this;

            // 补全运行时：后台跟踪当前连接 + 元数据缓存
            try
            {
                CompletionRuntime.Start(this);
                Log.Info("[补全] 运行时已启动");
            }
            catch (Exception ex)
            {
                Log.Error("[补全] 运行时启动失败", ex);
            }

            // 历史存储：后台打开 SQLite，不阻塞包初始化
            try
            {
                HistoryRuntime.Start();
                AutoSnapshotTracker.Start(this);
            }
            catch (Exception ex)
            {
                Log.Error("[历史] 运行时启动失败", ex);
            }

            // DTE 执行事件必须在 UI 线程挂
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                ThemeManager.Initialize(GetService);
                ExecutionCapture.Start(GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE);
            }
            catch (Exception ex)
            {
                Log.Error("[历史] 执行捕获启动失败", ex);
            }

            // 快捷键钩子：不依赖 VS 键绑定，只在 SSMS 前台时处理
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                FormatKeyHandler.Start(selectionOnly =>
                {
                    var view = EditorLocator.GetActiveView(this, mustBeSql: true);
                    if (view == null) return;
                    DoFormat(view, selectionOnly);
                });
            }
            catch (Exception ex)
            {
                Log.Error("[格式化] 快捷键钩子启动失败", ex);
            }

            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                InstallUi();
            }
            catch (Exception ex)
            {
                Log.Error("UI 安装失败", ex);
            }

            // 编辑器是后来才创建的，这里挂一个"文档打开"监听来装命令过滤器
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                HookDocumentEvents();
            }
            catch (Exception ex)
            {
                Log.Error("文档事件监听安装失败", ex);
            }

            Log.Info("================ 包初始化结束 ================");

            // ── 自检模式（诊断用，默认关闭）──
            //
            // 环境变量 SQLKIT_SELFTEST=1 时，把"选项读写 → 引擎格式化 → 编辑器格式化"
            // 整条链路跑一遍并写日志。
            //
            // 留着它是因为：UI 自动点击（UIA / 模拟鼠标）受窗口层级、焦点、前台锁影响，
            // 在不同机器上极不稳定；而日志判定是确定性的。
            // 以后改菜单/回调，只要跑一次 SQLKIT_SELFTEST=1 就能确认链路没断。
            if (Environment.GetEnvironmentVariable("SQLKIT_SELFTEST") == "1")
            {
                try { SelfTest(); }
                catch (Exception ex) { Log.Error("自检异常", ex); }
            }
        }

        /// <summary>
        /// 自检：把菜单回调链路上的每个动作都执行一遍，验证不是"只画了个菜单壳"。
        /// 结果全部写日志，从文件即可判定。
        /// </summary>
        private void SelfTest()
        {
            Log.Info("--------- 自检开始 ---------");

            try
            {
                // ① 选项读写
                Log.Info("[自检] 选项初始: " + FormatOptionsStore.Current.IndentSize + " 空格, " +
                         "大写=" + FormatOptionsStore.Current.UppercaseKeywords + ", " +
                         "每列一行=" + FormatOptionsStore.Current.VerticalSelectList);

                FormatOptionsStore.Update(s => s.IndentSize = 2);
                var after = FormatOptionsStore.Current.IndentSize;
                Log.Info("[自检] 改成缩进 2 后回读 = " + after + " -> " + (after == 2 ? "PASS" : "FAIL"));

                if (System.IO.File.Exists(FormatOptionsStore.ConfigPath))
                    Log.Info("[自检] 配置文件已落盘: " + FormatOptionsStore.ConfigPath + " -> PASS");
                else
                    Log.Info("[自检] 配置文件未生成 -> FAIL");

                // 还原
                FormatOptionsStore.Update(s => s.IndentSize = 4);

                // ② 排版引擎直调（不经过编辑器）
                var sql = "select a,b,c from t where x=1 and y=2";
                var eng = new Formatting.SqlFormatter().Format(sql, FormatOptionsStore.Current.ToFormatOptions());
                Log.Info("[自检] 引擎格式化结果:\n" + eng.Text);
                Log.Info("[自检] 引擎调用 -> " + (eng.Text != null && eng.Text.Length > 0 ? "PASS" : "FAIL"));

                // ③ 编辑器链路（有没有活动 SQL 视图）
                var view = EditorLocator.GetActiveView(this, mustBeSql: true);
                Log.Info("[自检] 活动 SQL 视图 = " + (view == null ? "无（没打开 .sql 文件）" : "有"));

                if (view != null)
                {
                    var outcome = FormatExecutor.Run(view, FormatOptionsStore.Current.ToFormatOptions());
                    Log.Info("[自检] FormatExecutor: changed=" + outcome.Changed +
                             " error=" + outcome.IsError + " msg=" + outcome.Message);
                    Log.Info("[自检] 编辑器格式化 -> " + (!outcome.IsError ? "PASS" : "FAIL"));
                }
            }
            catch (Exception ex)
            {
                Log.Error("[自检] 异常", ex);
            }

            Log.Info("--------- 自检结束 ---------");
        }

        // ── UI 安装 ──

        private void InstallUi()
        {
            // 这里所有操作都碰 COM / UI 对象，必须在主线程。
            // 分析器 VSTHRD010 就是提醒这个 —— 显式断言比事后偶发崩溃好查。
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Environment.GetEnvironmentVariable("SQLKIT_NO_UI") == "1")
            {
                // 诊断开关：只加载包、不注入任何 UI。
                // 用于判断"某个异常行为是插件引起还是 SSMS 自身"。
                Log.Warn("SQLKIT_NO_UI=1，跳过全部 UI 注入");
                return;
            }

            EnvDTE.DTE dte = null;
            try
            {
                dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) Log.Warn("DTE 不可用，菜单将无法注入");
                else Log.Info("DTE = " + dte.Name + " / " + dte.Version);
            }
            catch (Exception ex)
            {
                Log.Error("取 DTE 失败", ex);
            }

            // ① 顶部菜单（一级 + 二级弹层）—— 用户明确要求的入口
            if (dte != null)
            {
                try
                {
                    _topMenu = new TopLevelMenu(
                        FormatActiveEditor,
                        FormatSelectionActiveEditor,
                        OpenHistory,
                        OpenSettings,
                        RefreshCompletionCache,
                        OpenLogFile,
                        AboutWindow.OpenRepository,
                        ShowAbout,
                        ChangeLanguage);

                    _topMenu.Install(dte);
                }
                catch (Exception ex)
                {
                    Log.Error("安装顶部菜单失败", ex);
                }
            }

            // ② 编辑器右键菜单
            if (dte != null)
            {
                try
                {
                    _contextMenu = new EditorContextMenu(FormatActiveEditor, FormatSelectionActiveEditor);
                    _contextMenu.TryInstall(dte);
                }
                catch (Exception ex)
                {
                    Log.Error("安装右键菜单失败", ex);
                }
            }

            // ③ 给当前已打开的编辑器挂命令过滤器（快捷键）
            try
            {
                var view = EditorLocator.GetActiveView(this, mustBeSql: false);
                if (view != null) EnsureFilter(view);
            }
            catch (Exception ex)
            {
                Log.Warn("初始化时挂过滤器失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 监听文档窗口创建，给每个 SQL 编辑器挂命令过滤器（快捷键支持）。
        ///
        /// 用 RunningDocumentTable（RDT）事件而不是 DTE.Events.WindowEvents ——
        /// RDT 在 SSMS 里更可靠，且不依赖窗口激活顺序。
        /// </summary>
        private void HookDocumentEvents()
        {
            try
            {
                var rdt = GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                if (rdt == null)
                {
                    Log.Warn("SVsRunningDocumentTable 不可用，快捷键可能只在已打开的编辑器上生效");
                    return;
                }

                uint cookie;
                int hr = rdt.AdviseRunningDocTableEvents(new DocTableEvents(this), out cookie);
                _rdt = rdt;
                Log.Info("RDT 事件已挂: hr=0x" + hr.ToString("X8") + " cookie=" + cookie);
            }
            catch (Exception ex)
            {
                Log.Error("挂 RDT 事件失败", ex);
            }
        }

        private sealed class DocTableEvents : IVsRunningDocTableEvents
        {
            private readonly SqlPluginPackage _pkg;

            public DocTableEvents(SqlPluginPackage pkg) { _pkg = pkg; }

            public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType,
                                                uint dwReadLocksRemaining, uint dwEditLocksRemaining)
            {
                try { _pkg.OnHistoryFileOpened(docCookie); }
                catch (Exception ex) { Log.Warn("记录文件打开失败: " + ex.Message); }
                return 0;
            }

            public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType,
                                                  uint dwReadLocksRemaining, uint dwEditLocksRemaining)
            {
                if (dwReadLocksRemaining == 0 && dwEditLocksRemaining == 0)
                {
                    // 文档关闭：清掉对应的视图记录（用不上精确匹配，整体清理更安全）
                    try { _pkg._filteredViews.Clear(); } catch { }
                }
                return 0;
            }

            public int OnAfterSave(uint docCookie)
            {
                try { _pkg.OnHistoryFileSaved(docCookie); }
                catch (Exception ex) { Log.Warn("记录文件快照失败: " + ex.Message); }
                return 0;
            }
            public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => 0;

            public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
            {
                // 只在**第一次显示**时装过滤器。后续显示（切标签页）不用重复做，
                // 也减少碰 COM frame 的机会。
                if (fFirstShow == 0) return 0;

                try
                {
                    _pkg.EnsureFilterForFrame(pFrame);
                }
                catch (Exception ex)
                {
                    Log.Warn("文档窗口显示时挂过滤器失败: " + ex.Message);
                }
                return 0;
            }

            public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => 0;
        }

        /// <summary>RDT cookie → 完整文件路径（未保存文档通常为空或临时路径）。</summary>
        private string GetDocumentPathFromCookie(uint docCookie)
        {
            var rdt = _rdt;
            if (rdt == null) return null;

            try
            {
                uint flags, readLocks, editLocks, itemId;
                string moniker;
                IVsHierarchy hierarchy;
                IntPtr docData;

                int hr = rdt.GetDocumentInfo(docCookie, out flags, out readLocks, out editLocks,
                    out moniker, out hierarchy, out itemId, out docData);

                return hr == 0 ? moniker : null;
            }
            catch
            {
                return null;
            }
        }

        private void OnHistoryFileOpened(uint docCookie)
        {
            string path = GetDocumentPathFromCookie(docCookie);
            var connection = CompletionRuntime.Current?.Connection;
            HistoryRuntime.RecordFileOpened(path, connection?.Server, connection?.Database);
        }

        private void OnHistoryFileSaved(uint docCookie)
        {
            string path = GetDocumentPathFromCookie(docCookie);
            HistoryRuntime.CaptureFileSnapshot(path, "save");
        }

        /// <summary>从窗口 frame 拿到 IVsTextView 并挂过滤器。</summary>
        private void EnsureFilterForFrame(IVsWindowFrame frame)
        {
            if (frame == null) return;

            try
            {
                object docView;
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView) != 0) return;
                if (docView == null) return;

                var vsView = docView as IVsTextView;
                if (vsView == null)
                {
                    // 有些文档视图是 IVsCodeWindow，要再取 PrimaryView
                    var codeWindow = docView as IVsCodeWindow;
                    if (codeWindow != null) codeWindow.GetPrimaryView(out vsView);
                }

                if (vsView != null) EnsureFilter(vsView);
            }
            catch (Exception ex)
            {
                Log.Warn("从 frame 取视图失败: " + ex.Message);
            }
        }

        /// <summary>给指定视图挂命令过滤器（幂等）。</summary>
        private void EnsureFilter(IVsTextView view)
        {
            if (view == null) return;

            // 去重键：用视图对象的 RuntimeHelpers 哈希，**不要**用
            // Marshal.GetIUnknownForObject + Release。
            //
            // ⚠ 这里踩过坑：GetIUnknownForObject 每调一次就 AddRef，
            // Release 一次只减一次；如果哪条异常路径漏掉 Release，
            // COM 引用计数会失衡。更糟的是不同 RCW 实例哈希可能重复/冲突。
            // 用对象身份哈希最省事：同一个 RCW 实例才判重，SSMS 每个编辑器
            // 只有一个 IVsTextView 实例，够用。
            int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view);

            lock (_filteredViews)
            {
                if (_filteredViews.Contains(key)) return;    // 已挂过
                _filteredViews.Add(key);
            }

            try
            {
                var filter = new EditorCommandFilter(
                    view, OnFilterFormat, OnFilterFormatSelection,
                    EnsureContextMenuAlive, ExecutionCapture.OnEditorCommand);

                IOleCommandTarget next;
                int hr = view.AddCommandFilter(filter, out next);
                filter.Next = next;

                Log.Info("命令过滤器已挂 hr=0x" + hr.ToString("X8") +
                         " next=" + (next == null ? "null" : next.GetType().Name));

                if (hr != 0)
                {
                    lock (_filteredViews) { _filteredViews.Remove(key); }
                }
            }
            catch (Exception ex)
            {
                Log.Error("挂命令过滤器失败", ex);
                lock (_filteredViews) { _filteredViews.Remove(key); }
            }
        }

        /// <summary>过滤器回调：格式化指定视图。</summary>
        private bool OnFilterFormat(IVsTextView view)
        {
            return DoFormat(view);
        }

        private bool OnFilterFormatSelection(IVsTextView view)
        {
            return DoFormat(view, requireSelection: true);
        }

        /// <summary>右键菜单保活：Shell 会重建编辑器上下文菜单，这里补回来。</summary>
        private void EnsureContextMenuAlive()
        {
            if (_contextMenu == null) return;

            try
            {
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null) _contextMenu.TryInstall(dte);
            }
            catch { }
        }

        // ── 动作 ──

        /// <summary>菜单/工具栏入口：格式化当前活动编辑器。</summary>
        private void FormatActiveEditor()
        {
            var view = EditorLocator.GetActiveView(this, mustBeSql: true);
            if (view == null)
            {
                CommandBarInjector.ShowError(
                    "SSMS-SQLPlugin",
                    Loc.T("There is no active SQL query window.") + "\r\n" +
                    Loc.T("Open or switch to a .sql file and try again."));
                return;
            }

            DoFormat(view);
        }

        /// <summary>只格式化选中内容；没有选区时提示用户。</summary>
        private void FormatSelectionActiveEditor()
        {
            var view = EditorLocator.GetActiveView(this, mustBeSql: true);
            if (view == null)
            {
                CommandBarInjector.ShowError(
                    "SSMS-SQLPlugin",
                    Loc.T("There is no active SQL query window.") + "\r\n" +
                    Loc.T("Open or switch to a .sql file and try again."));
                return;
            }

            DoFormat(view, requireSelection: true);
        }

        /// <summary>真正的格式化执行，返回是否成功改动。</summary>
        private bool DoFormat(IVsTextView view, bool requireSelection = false)
        {
            try
            {
                var options = FormatOptionsStore.Current.ToFormatOptions();
                var outcome = FormatExecutor.Run(view, options, requireSelection);

                Log.Info("格式化结果: changed=" + outcome.Changed +
                         " error=" + outcome.IsError + " msg=" + outcome.Message);

                if (outcome.IsError)
                {
                    CommandBarInjector.ShowError(Loc.T("Formatting Failed"), outcome.Message);
                    return false;
                }

                return outcome.Changed;
            }
            catch (Exception ex)
            {
                Log.Error("格式化执行异常", ex);
                CommandBarInjector.ShowError(Loc.T("Formatting Failed"), ex.Message);
                return false;
            }
        }

        private void OpenHistory()
        {
            try
            {
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                HistoryWindow.ShowFrom(dte);
            }
            catch (Exception ex)
            {
                Log.Error("打开 SQL 历史失败", ex);
                CommandBarInjector.ShowError(Loc.T("SQL History"), ex.Message);
            }
        }

        private void ChangeLanguage(string language)
        {
            try
            {
                if (string.Equals(FormatOptionsStore.Current.UiLanguage, language,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                FormatOptionsStore.Update(options => options.UiLanguage = language);
                System.Windows.Forms.MessageBox.Show(
                    Loc.T("Language saved. Restart SSMS for the change to take effect."),
                    "SSMS-SQLPlugin",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Error("切换语言失败", ex);
                CommandBarInjector.ShowError("SSMS-SQLPlugin", ex.Message);
            }
        }

        private void OpenSettings()
        {
            try
            {
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                SettingsWindow.ShowFrom(dte);
            }
            catch (Exception ex)
            {
                Log.Error("打开设置失败", ex);
                CommandBarInjector.ShowError(Loc.T("Settings"), ex.Message);
            }
        }

        private void RefreshCompletionCache()
        {
            try
            {
                CompletionRuntime.Current?.RefreshMetadata();
            }
            catch (Exception ex)
            {
                Log.Error("刷新补全缓存失败", ex);
            }
        }

        private void OpenLogFile()
        {
            try
            {
                string path = Infrastructure.Log.LogFilePath;
                if (!System.IO.File.Exists(path))
                {
                    CommandBarInjector.ShowError(Loc.T("Log"),
                        Loc.T("No log file yet:") + "\r\n" + path);
                    return;
                }
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                CommandBarInjector.ShowError(Loc.T("Failed to Open Log"), ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { FormatKeyHandler.Stop(); } catch { }
                try { AutoSnapshotTracker.Stop(); } catch { }
                try { ExecutionCapture.Stop(); } catch { }
                try { HistoryRuntime.Stop(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void ShowAbout()
        {
            try
            {
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                AboutWindow.ShowFrom(dte);
            }
            catch (Exception ex)
            {
                Log.Error("打开关于窗口失败", ex);
                System.Windows.Forms.MessageBox.Show(
                    "SSMS-SQLPlugin\r\n\r\n" +
                    Loc.F("Repository: {0}", "https://github.com/dreamstation625/SSMS-SQLPlugin"),
                    Loc.T("About SSMS-SQLPlugin"),
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
        }
    }
}
