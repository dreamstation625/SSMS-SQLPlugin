using System;
using Microsoft.VisualStudio.CommandBars;
using SsmsSqlPlugin.Editor;
using SsmsSqlPlugin.History;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Menus
{
    /// <summary>
    /// 顶部菜单栏的一级菜单「SSMS-SQLPlugin」+ 二级下拉项。
    ///
    /// 结构（用户要求"顶部工具栏中进入二级菜单"）：
    ///
    ///     菜单栏
    ///       └─ SSMS-SQLPlugin          ← 一级（挂在 MenuBar 上）
    ///            ├─ 格式化 SQL        Ctrl+K Ctrl+D
    ///            ├─ ──────────
    ///            ├─ 格式化选项  ▸     ← 二级弹层
    ///            │    ├─ 关键字大写      ✓
    ///            │    ├─ 缩进 2 空格
    ///            │    ├─ 缩进 4 空格      ✓
    ///            │    ├─ SELECT 每列一行  ✓
    ///            │    └─ 展开子查询       ✓
    ///            ├─ ──────────
    ///            ├─ 打开日志
    ///            └─ 关于
    ///
    /// 复用 CommandBarInjector.Populate 的递归能力生成多层菜单。
    /// </summary>
    internal sealed class TopLevelMenu
    {
        public const string MenuCaption = "SSMS-SQLPlugin";
        private const string TagPrefix = "SsmsSqlPlugin:";

        private readonly Func<FormatOptionsSnapshot> _getOptions;
        private readonly Action _format;
        private readonly Action _openHistory;
        private readonly Action _refreshCompletion;
        private readonly Action _openLog;
        private readonly Action _about;

        /// <summary>上次安装时拿到的 DTE，选项变更后用它重建菜单刷新勾选。</summary>
        private EnvDTE.DTE _dte;

        public TopLevelMenu(
            Func<FormatOptionsSnapshot> getOptions,
            Action format,
            Action openHistory,
            Action refreshCompletion,
            Action openLog,
            Action about)
        {
            _getOptions = getOptions;
            _format = format;
            _openHistory = openHistory;
            _refreshCompletion = refreshCompletion;
            _openLog = openLog;
            _about = about;
        }

        /// <summary>
        /// 在菜单栏上创建/更新这个一级菜单。
        /// 返回 true 表示成功挂上。
        ///
        /// 幂等：重复调用会先删掉旧的同名菜单再建，避免重启后出现两个。
        /// </summary>
        public bool Install(EnvDTE.DTE dte)
        {
            _dte = dte;

            var menuBar = CommandBarInjector.GetMenuBar(dte);
            if (menuBar == null)
            {
                Log.Error("顶部菜单安装失败：拿不到 MenuBar");
                return false;
            }

            // 清掉上一次可能残留的同名菜单（按 Tag 识别）
            int removed = 0;
            for (int i = menuBar.Controls.Count; i >= 1; i--)
            {
                CommandBarControl c;
                try { c = menuBar.Controls[i]; }
                catch { continue; }

                string tag = null;
                try { tag = c.Tag as string; } catch { }

                if (tag != null && tag.StartsWith(TagPrefix, StringComparison.Ordinal))
                {
                    try { c.Delete(false); removed++; } catch { }
                }
            }
            if (removed > 0) Log.Info("清掉旧的顶部菜单 " + removed + " 个");

            try
            {
                // 插到菜单栏末尾
                int insertAt = menuBar.Controls.Count + 1;
                Log.Info("准备在索引 " + insertAt + " 插入顶部菜单");

                var popup = menuBar.Controls.Add(
                    MsoControlType.msoControlPopup,
                    Type.Missing, Type.Missing,
                    insertAt, true) as CommandBarPopup;

                if (popup == null)
                {
                    Log.Error("菜单栏 Controls.Add 返回的对象不是 CommandBarPopup");
                    return false;
                }

                popup.Caption = MenuCaption;
                popup.Tag = TagPrefix + "Top";
                try { popup.TooltipText = "SSMS-SQLPlugin — SQL 格式化 / 历史 / 补全"; } catch { }
                popup.Visible = true;

                Log.Info("一级菜单已创建: " + popup.Caption);

                // 注意：这里**不能**把 popup 转成 CommandBar —— 会抛 InvalidCastException。
                // 直接把 popup.Controls 传下去。
                int n = CommandBarInjector.Populate(popup.Controls, BuildEntries());

                Log.Info("顶部菜单已安装：" + MenuCaption + "，子项 " + n + " 个，" +
                         "菜单栏现有 " + menuBar.Controls.Count + " 个顶级菜单");

                // 回读一遍，确认 Shell 真的收下了（而不只是我们本地对象有值）
                DumpMenu(popup, "  ");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("创建顶部菜单失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 递归 dump 菜单结构到日志。
        ///
        /// 用途：验证"Shell 真的收下了菜单"，而不只是我们本地对象有值。
        /// 注意这里读的一律是 COM 对象上的**回读值**（Caption/Visible），
        /// 不是我们自己传进去的字符串 —— 如果 Shell 丢弃了，回读会不同或抛异常。
        /// </summary>
        private static void DumpMenu(CommandBarPopup popup, string indent)
        {
            if (popup == null) return;

            try
            {
                var controls = popup.Controls;
                int count = controls.Count;
                Log.Info(indent + "└─ [" + popup.Caption + "] 子项 " + count + " 个");

                for (int i = 1; i <= count; i++)
                {
                    CommandBarControl c;
                    try { c = controls[i]; }
                    catch (Exception ex) { Log.Warn(indent + "   读子项 " + i + " 失败: " + ex.Message); continue; }

                    string cap, tag;
                    bool visible;
                    try { cap = c.Caption; } catch { cap = "(读不到)"; }
                    try { tag = c.Tag as string; } catch { tag = null; }
                    try { visible = c.Visible; } catch { visible = false; }

                    var child = c as CommandBarPopup;
                    string kind = child != null ? "POPUP" : "ITEM ";

                    Log.Info(indent + "   [" + i + "] " + kind +
                             " cap=\"" + cap + "\" visible=" + visible +
                             (tag != null ? " tag=" + tag : ""));

                    if (child != null) DumpMenu(child, indent + "   ");
                }
            }
            catch (Exception ex)
            {
                Log.Error("dump 菜单失败", ex);
            }
        }

        /// <summary>构建菜单项清单。</summary>
        private System.Collections.Generic.List<MenuEntry> BuildEntries()
        {
            var list = new System.Collections.Generic.List<MenuEntry>
            {
                new MenuEntry("格式化 SQL\tCtrl+K, Ctrl+D",
                              "格式化当前选区；没有选区则格式化全文",
                              _format,
                              TagPrefix + "Format"),
            };

            // ── 二级弹层：格式化选项 ──
            // 带勾选状态：当前生效的那一项打勾（缩进互斥、其余是开关）
            var indent2 = new MenuEntry("缩进 2 空格", "使用 2 个空格缩进",
                                        () => SetIndent(2), TagPrefix + "Indent2")
                .WithCheck(() => _getOptions().IndentSize == 2);

            var indent4 = new MenuEntry("缩进 4 空格", "使用 4 个空格缩进",
                                        () => SetIndent(4), TagPrefix + "Indent4")
                .WithCheck(() => _getOptions().IndentSize == 4);

            var upper = new MenuEntry("关键字大写", "关键字转成大写（关闭则小写）",
                                      ToggleUppercase, TagPrefix + "Upper")
                .WithCheck(() => _getOptions().UppercaseKeywords);

            var vertical = new MenuEntry("SELECT 每列一行", "SELECT 两列及以上时每列独占一行",
                                         ToggleVertical, TagPrefix + "Vertical")
                .WithCheck(() => _getOptions().VerticalSelectList);

            var subq = new MenuEntry("展开子查询", "括号里的子查询按子句纵向展开",
                                     ToggleSubquery, TagPrefix + "Subquery")
                .WithCheck(() => _getOptions().ExpandSubqueries);

            list.Add(MenuEntry.Separator());
            list.Add(new MenuEntry("格式化选项", "格式化风格设置", null, TagPrefix + "Options")
                .WithChildren(indent2, indent4, MenuEntry.Separator(), upper, vertical, subq));

            list.Add(MenuEntry.Separator());
            list.Add(new MenuEntry("SQL 历史...", "搜索、筛选执行历史，双击在查询窗口重开",
                                   _openHistory, TagPrefix + "History"));

            var autoOff = new MenuEntry("关闭", "不自动记录", () => SetAutoSave(AutoSaveMode.Off, 0),
                                        TagPrefix + "AutoOff")
                .WithCheck(() => !HistoryAutoSaveStore.Current.Enabled);

            var autoEdit = new MenuEntry("修改后记录", "内容有修改时自动记录，最多每分钟一条",
                                         () => SetAutoSave(AutoSaveMode.OnEdit, 0),
                                         TagPrefix + "AutoEdit")
                .WithCheck(() =>
                {
                    var settings = HistoryAutoSaveStore.Current;
                    return settings.Enabled && settings.Mode == AutoSaveMode.OnEdit;
                });

            var auto1 = new MenuEntry("每 1 分钟", "内容有变化才记录，最多每分钟一条",
                                      () => SetAutoSave(AutoSaveMode.Interval, 1),
                                      TagPrefix + "Auto1")
                .WithCheck(() => IsAutoInterval(1));

            var auto5 = new MenuEntry("每 5 分钟", "内容有变化才记录，最多每 5 分钟一条",
                                      () => SetAutoSave(AutoSaveMode.Interval, 5),
                                      TagPrefix + "Auto5")
                .WithCheck(() => IsAutoInterval(5));

            var auto15 = new MenuEntry("每 15 分钟", "内容有变化才记录，最多每 15 分钟一条",
                                       () => SetAutoSave(AutoSaveMode.Interval, 15),
                                       TagPrefix + "Auto15")
                .WithCheck(() => IsAutoInterval(15));

            var auto30 = new MenuEntry("每 30 分钟", "内容有变化才记录，最多每 30 分钟一条",
                                       () => SetAutoSave(AutoSaveMode.Interval, 30),
                                       TagPrefix + "Auto30")
                .WithCheck(() => IsAutoInterval(30));

            list.Add(new MenuEntry("自动记录（" + AutoSaveCaption() + "）",
                                   "不执行也记录当前 SQL；内容有变化才落库",
                                   null, TagPrefix + "AutoSave")
                .WithChildren(autoOff, autoEdit, MenuEntry.Separator(), auto1, auto5, auto15, auto30));

            list.Add(new MenuEntry("刷新补全缓存", "重新读取当前数据库的表和字段",
                                   _refreshCompletion, TagPrefix + "RefreshCompletion"));
            list.Add(new MenuEntry("打开日志", "打开插件日志文件（排查问题用）",
                                   _openLog, TagPrefix + "Log"));
            list.Add(new MenuEntry("关于 SSMS-SQLPlugin", "版本与项目信息",
                                   _about, TagPrefix + "About"));

            return list;
        }

        /// <summary>
        /// 重建整个一级菜单，用于刷新勾选状态。
        ///
        /// 为什么必须重建：CommandBars 的按钮样式（勾）只在创建时定，
        /// Shell 打开菜单前不会回调我们做 QueryStatus —— 那条路要有真正的
        /// IOleCommandTarget 命令注册，而我们的菜单项是纯 Click 事件实现的。
        /// 重建很便宜（几十个 COM 对象），改选项后立即重建即可。
        /// </summary>
        public void Refresh(EnvDTE.DTE dte)
        {
            Install(dte);
        }

        // ── 自动记录 ──

        private static bool IsAutoInterval(int minutes)
        {
            var settings = HistoryAutoSaveStore.Current;
            return settings.Enabled &&
                   settings.Mode == AutoSaveMode.Interval &&
                   settings.IntervalMinutes == minutes;
        }

        private static string AutoSaveCaption()
        {
            var settings = HistoryAutoSaveStore.Current;
            if (!settings.Enabled) return "关闭";
            return settings.Mode == AutoSaveMode.OnEdit
                ? "修改后"
                : "每 " + Math.Max(1, settings.IntervalMinutes) + " 分钟";
        }

        private void SetAutoSave(AutoSaveMode mode, int intervalMinutes)
        {
            HistoryAutoSaveStore.Update(settings =>
            {
                settings.Enabled = mode != AutoSaveMode.Off;
                settings.Mode = mode;
                if (intervalMinutes > 0) settings.IntervalMinutes = intervalMinutes;
            });

            Log.Info("自动记录 = " + HistoryAutoSaveStore.Describe(HistoryAutoSaveStore.Current));
            RefreshChecks();
        }

        // ── 选项操作 ──

        private void SetIndent(int size)
        {
            FormatOptionsStore.Update(s => s.IndentSize = size);
            Log.Info("缩进设为 " + size);
            RefreshChecks();
        }

        private void ToggleUppercase()
        {
            FormatOptionsStore.Update(s => s.UppercaseKeywords = !s.UppercaseKeywords);
            Log.Info("关键字大写 = " + FormatOptionsStore.Current.UppercaseKeywords);
            RefreshChecks();
        }

        private void ToggleVertical()
        {
            FormatOptionsStore.Update(s => s.VerticalSelectList = !s.VerticalSelectList);
            Log.Info("SELECT 每列一行 = " + FormatOptionsStore.Current.VerticalSelectList);
            RefreshChecks();
        }

        private void ToggleSubquery()
        {
            FormatOptionsStore.Update(s => s.ExpandSubqueries = !s.ExpandSubqueries);
            Log.Info("展开子查询 = " + FormatOptionsStore.Current.ExpandSubqueries);
            RefreshChecks();
        }

        /// <summary>重建菜单让勾选状态跟上最新选项。</summary>
        private void RefreshChecks()
        {
            if (_dte == null) return;
            try
            {
                Install(_dte);
                Log.Info("菜单已重建（刷新勾选状态）");
            }
            catch (Exception ex)
            {
                Log.Warn("重建菜单失败: " + ex.Message);
            }
        }
    }
}
