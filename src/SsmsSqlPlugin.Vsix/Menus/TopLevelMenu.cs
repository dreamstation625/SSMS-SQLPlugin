using System;
using Microsoft.VisualStudio.CommandBars;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Menus
{
    /// <summary>
    /// 顶部菜单栏的一级菜单「SSMS-SQLPlugin」+ 二级下拉项。
    ///
    /// 结构（用户要求"顶部工具栏中进入二级菜单"）：
    ///
    ///     菜单栏
    ///       └─ SSMS-SQLPlugin
    ///            ├─ 格式化 SQL        Ctrl+K Ctrl+D
    ///            ├─ 格式化选定内容    Ctrl+K Ctrl+F
    ///            ├─ ──────────
    ///            ├─ SQL 历史...
    ///            ├─ 设置...
    ///            ├─ ──────────
    ///            ├─ 刷新补全缓存
    ///            ├─ 打开日志
    ///            ├─ 检查更新
    ///            └─ 关于
    ///
    /// 设置项集中在独立 SettingsWindow，菜单不再放带勾选项；
    /// 菜单项左侧用 MenuIcons 的 16x16 图标填充 CommandBars 图标位。
    /// </summary>
    internal sealed class TopLevelMenu
    {
        public const string MenuCaption = "SSMS-SQLPlugin";
        private const string TagPrefix = "SsmsSqlPlugin:";

        private readonly Action _format;
        private readonly Action _formatSelection;
        private readonly Action _openHistory;
        private readonly Action _openSettings;
        private readonly Action _refreshCompletion;
        private readonly Action _openLog;
        private readonly Action _checkUpdates;
        private readonly Action _about;

        public TopLevelMenu(
            Action format,
            Action formatSelection,
            Action openHistory,
            Action openSettings,
            Action refreshCompletion,
            Action openLog,
            Action checkUpdates,
            Action about)
        {
            _format = format;
            _formatSelection = formatSelection;
            _openHistory = openHistory;
            _openSettings = openSettings;
            _refreshCompletion = refreshCompletion;
            _openLog = openLog;
            _checkUpdates = checkUpdates;
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
                new MenuEntry("格式化选定内容\tCtrl+K, Ctrl+F",
                              "只格式化选中的 SQL；没有选区会提示",
                              _formatSelection,
                              TagPrefix + "FormatSelection"),
                MenuEntry.Separator(),
                new MenuEntry("SQL 历史...", "搜索、筛选执行历史，双击在查询窗口重开",
                              _openHistory, TagPrefix + "History"),
                new MenuEntry("设置...", "格式化风格、自动记录与历史数据",
                              _openSettings, TagPrefix + "Settings"),
                MenuEntry.Separator(),
                new MenuEntry("刷新补全缓存", "重新读取当前数据库的表和字段",
                              _refreshCompletion, TagPrefix + "RefreshCompletion"),
                new MenuEntry("打开日志", "打开插件日志文件（排查问题用）",
                              _openLog, TagPrefix + "Log"),
                new MenuEntry("检查更新", "打开 GitHub 仓库，查看最新版本",
                              _checkUpdates, TagPrefix + "CheckUpdates"),
                new MenuEntry("关于 SSMS-SQLPlugin", "版本与项目信息",
                              _about, TagPrefix + "About"),
            };

            return list;
        }
    }
}
