using System;
using Microsoft.VisualStudio.CommandBars;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Localization;

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
    /// 设置项集中在独立 SettingsWindow；菜单项左侧用 MenuIcons 的 16x16 图标填充
    /// CommandBars 图标位。需要勾选态的项（语言）在标题末尾追加 ✓：
    /// SSMS 22 的 Automation 包装不渲染 msoButtonDown，且会裁掉前导空格。
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
        private readonly Action<string> _changeLanguage;

        public TopLevelMenu(
            Action format,
            Action formatSelection,
            Action openHistory,
            Action openSettings,
            Action refreshCompletion,
            Action openLog,
            Action checkUpdates,
            Action about,
            Action<string> changeLanguage)
        {
            _format = format;
            _formatSelection = formatSelection;
            _openHistory = openHistory;
            _openSettings = openSettings;
            _refreshCompletion = refreshCompletion;
            _openLog = openLog;
            _checkUpdates = checkUpdates;
            _about = about;
            _changeLanguage = changeLanguage;
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
                try { popup.TooltipText = Loc.T("SSMS-SQLPlugin — SQL formatting, history and completion"); } catch { }
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

        private static bool IsLanguage(string value)
        {
            return string.Equals(Loc.Preference, value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>构建菜单项清单。</summary>
        private System.Collections.Generic.List<MenuEntry> BuildEntries()        {
            var list = new System.Collections.Generic.List<MenuEntry>
            {
                new MenuEntry(Loc.T("Format SQL") + "\tCtrl+K, Ctrl+D",
                              Loc.T("Format the current selection, or the whole document when nothing is selected"),
                              _format,
                              TagPrefix + "Format"),
                new MenuEntry(Loc.T("Format Selection") + "\tCtrl+K, Ctrl+F",
                              Loc.T("Format only the selected SQL; prompts when nothing is selected"),
                              _formatSelection,
                              TagPrefix + "FormatSelection"),
                MenuEntry.Separator(),
                new MenuEntry(Loc.T("SQL History..."),
                              Loc.T("Search and filter execution history; double-click to reopen in a query window"),
                              _openHistory, TagPrefix + "History"),
                new MenuEntry(Loc.T("Settings..."),
                              Loc.T("Formatting style, auto-save and history data"),
                              _openSettings, TagPrefix + "Settings"),
                new MenuEntry(Loc.T("Language"), Loc.T("Language"),
                              null, TagPrefix + "Language")
                    .WithChildren(
                        new MenuEntry(Loc.T("Follow system"), Loc.T("Language"),
                                      () => _changeLanguage?.Invoke("auto"),
                                      TagPrefix + "LanguageAuto")
                            .WithCheck(() => IsLanguage("auto")),
                        new MenuEntry(Loc.T("Chinese"), Loc.T("Language"),
                                      () => _changeLanguage?.Invoke("zh-CN"),
                                      TagPrefix + "LanguageZh")
                            .WithCheck(() => IsLanguage("zh-CN")),
                        new MenuEntry(Loc.T("English"), Loc.T("Language"),
                                      () => _changeLanguage?.Invoke("en"),
                                      TagPrefix + "LanguageEn")
                            .WithCheck(() => IsLanguage("en"))),
                MenuEntry.Separator(),
                new MenuEntry(Loc.T("Refresh Completion Cache"),
                              Loc.T("Reload tables and columns for the current database"),
                              _refreshCompletion, TagPrefix + "RefreshCompletion"),
                new MenuEntry(Loc.T("Open Log"),
                              Loc.T("Open the plugin log file for troubleshooting"),
                              _openLog, TagPrefix + "Log"),
                new MenuEntry(Loc.T("Check for Updates"),
                              Loc.T("Open the GitHub repository to see the latest version"),
                              _checkUpdates, TagPrefix + "CheckUpdates"),
                new MenuEntry(Loc.T("About SSMS-SQLPlugin"),
                              Loc.T("Version and project information"),
                              _about, TagPrefix + "About"),
            };

            return list;
        }
    }
}
