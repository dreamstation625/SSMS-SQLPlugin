using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.CommandBars;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Menus
{
    /// <summary>
    /// 菜单元数据：一个菜单项就是「标题 + 回调」。
    /// </summary>
    internal sealed class MenuEntry
    {
        public MenuEntry(string caption, string tooltip, Action action, string tag = null)
        {
            Caption = caption;
            Tooltip = tooltip;
            Action = action;
            Tag = tag ?? caption;
        }

        public string Caption { get; }
        public string Tooltip { get; }
        public Action Action { get; }
        public string Tag { get; }

        /// <summary>是否是分隔线。</summary>
        public bool IsSeparator { get; private set; }

        /// <summary>
        /// 勾选状态取值器。非 null 时该菜单项渲染成"可勾选项"，
        /// 每次 Populate 时调一次决定是否打勾。
        ///
        /// 为什么要这样：CommandBars 的按钮只影响"当前渲染"，
        /// 打开菜单前 Shell 不会回调我们。所以只能在创建时定状态，
        /// 选项变更后需要重建菜单才能刷新勾 —— 见 TopLevelMenu.Refresh()。
        /// </summary>
        public Func<bool> CheckedProvider { get; private set; }

        public MenuEntry WithCheck(Func<bool> provider)
        {
            CheckedProvider = provider;
            return this;
        }

        public static MenuEntry Separator()
            => new MenuEntry("-", null, null) { IsSeparator = true };

        /// <summary>子菜单（二级弹层）。</summary>
        public List<MenuEntry> Children { get; private set; }

        public MenuEntry WithChildren(params MenuEntry[] children)
        {
            Children = new List<MenuEntry>(children);
            return this;
        }
    }

    /// <summary>
    /// 命令栏注入的公共工具。
    ///
    /// 三条硬规则（都是实测踩出来的）：
    ///   1) 必须强类型 cast，不能用 dynamic
    ///      SSMS 22.11 的 CommandBars 是 VS 自己的 Automation 包装
    ///      (Microsoft.VisualStudio.PlatformUI.Automation.*)，不支持 IDispatch
    ///      晚绑定，dynamic 访问 Caption 会抛 RuntimeBinderException。
    ///   2) 不用 AddCommandBarControl —— 它对 Automation 包装对象 QueryInterface
    ///      失败，返回 E_NOINTERFACE(0x80004002)。
    ///      改成 popup.Controls.Add(...) + Click 事件，完全不依赖命令系统。
    ///   3) Click 事件是唯一可靠的触发方式。按钮不绑命令 ID，点击直接进回调，
    ///      所以 QueryStatus/Exec 那套在这里用不上（也就没有"按钮变灰"之类的问题）。
    /// </summary>
    internal static class CommandBarInjector
    {
        /// <summary>文件菜单（编辑器右键菜单的父级不在这里，见 EditorContextMenu）。</summary>
        private const string MenuBarName = "MenuBar";

        /// <summary>取出顶层菜单栏。失败返回 null。</summary>
        public static CommandBar GetMenuBar(EnvDTE.DTE dte)
        {
            if (dte == null) return null;

            try
            {
                var cmdBars = dte.CommandBars as CommandBars;
                if (cmdBars == null)
                {
                    Log.Error("dte.CommandBars 无法转成强类型 CommandBars，实际类型=" +
                              (dte.CommandBars == null ? "null" : dte.CommandBars.GetType().FullName));
                    return null;
                }

                return cmdBars[MenuBarName];
            }
            catch (Exception ex)
            {
                Log.Error("取 MenuBar 失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 按标题关键字查顶层菜单。
        /// 本机实测：工具菜单索引 15、Caption "工具(&T)"。
        /// 中英文都要匹配，且要容忍 "&amp;" 和括号。
        /// </summary>
        public static CommandBarPopup FindTopMenu(CommandBar menuBar, params string[] nameHints)
        {
            if (menuBar == null || nameHints == null) return null;

            var controls = menuBar.Controls;
            for (int i = 1; i <= controls.Count; i++)
            {
                CommandBarControl c;
                try { c = controls[i]; }
                catch { continue; }

                string cap = SafeCaption(c);
                string norm = Normalize(cap);

                foreach (var hint in nameHints)
                {
                    if (norm.StartsWith(Normalize(hint), StringComparison.OrdinalIgnoreCase))
                    {
                        var popup = c as CommandBarPopup;
                        if (popup != null) return popup;
                    }
                }
            }
            return null;
        }

        /// <summary>把工具栏/菜单条上某个 popup 下的现有按钮全部清掉（按 Tag 识别）。</summary>
        public static int RemoveByTag(CommandBar bar, string tag)
        {
            if (bar == null) return 0;

            int removed = 0;
            var controls = bar.Controls;

            // 倒序删，避免索引失效
            for (int i = controls.Count; i >= 1; i--)
            {
                CommandBarControl c = null;
                try { c = controls[i]; }
                catch { continue; }

                string t = null;
                try { t = c.Tag as string; } catch { }

                if (string.Equals(t, tag, StringComparison.Ordinal))
                {
                    try { c.Delete(false); removed++; } catch { }
                }
            }
            return removed;
        }

        /// <summary>
        /// 往一个 <see cref="CommandBarControls"/> 集合里追加一批菜单项。
        ///
        /// ⚠ 参数刻意用 CommandBarControls 而不是 CommandBar。
        /// SSMS 22.11 返回的对象是 `_Marshaler` 动态包装，**只有拿到的那个接口
        /// 视图能转**：`c as CommandBarPopup` 成功，再 `(CommandBar)popup` 就抛
        /// InvalidCastException（实测日志："无法将类型为 _Marshaler 的对象强制
        /// 转换为 Microsoft.VisualStudio.CommandBars.CommandBar"）。
        /// CommandBarPopup.Controls 本身就是 CommandBarControls，
        /// 所以递归时直接把这个集合往下传，不再要求 CommandBar 视图。
        ///
        /// 返回实际创建的控件数，用于日志核对。
        /// 递归处理 Children → 生成二级弹层（msoControlPopup）。
        /// </summary>
        public static int Populate(CommandBarControls controls, IList<MenuEntry> entries)
        {
            if (controls == null || entries == null) return 0;

            int created = 0;

            foreach (var e in entries)
            {
                try
                {
                    if (e == null) continue;

                    if (e.IsSeparator)
                    {
                        // 按钮设 BeginGroup 就是分隔线（Automation 里没有独立分隔符类型）
                        var sep = (CommandBarButton)controls.Add(
                            MsoControlType.msoControlButton,
                            Type.Missing, Type.Missing,
                            controls.Count + 1, true);

                        sep.BeginGroup = true;
                        sep.Visible = true;
                        created++;
                        continue;
                    }

                    if (e.Children != null && e.Children.Count > 0)
                    {
                        // ── 二级弹层 ──
                        var popup = (CommandBarPopup)controls.Add(
                            MsoControlType.msoControlPopup,
                            Type.Missing, Type.Missing,
                            controls.Count + 1, true);

                        popup.Caption = e.Caption;
                        popup.Tag = e.Tag;
                        try { popup.TooltipText = e.Tooltip ?? e.Caption; } catch { }
                        popup.Visible = true;

                        // 注意：不转 CommandBar，直接把 popup 自己的 Controls 传下去
                        Populate(popup.Controls, e.Children);
                        created++;
                        continue;
                    }

                    // ── 普通按钮 ──
                    var btn = (CommandBarButton)controls.Add(
                        MsoControlType.msoControlButton,
                        Type.Missing, Type.Missing,
                        controls.Count + 1, true);

                    btn.Caption = e.Caption;
                    btn.Tag = e.Tag;
                    try { btn.TooltipText = e.Tooltip ?? e.Caption; } catch { }
                    btn.Visible = true;
                    btn.BeginGroup = false;

                    // 勾选状态：有 CheckedProvider 的项渲染成勾选按钮
                    if (e.CheckedProvider != null)
                    {
                        try
                        {
                            bool on = e.CheckedProvider();
                            btn.Style = MsoButtonStyle.msoButtonIconAndCaption;
                            btn.State = on ? MsoButtonState.msoButtonDown
                                           : MsoButtonState.msoButtonUp;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("设置勾选状态失败(" + e.Caption + "): " + ex.Message);
                        }
                    }

                    // 闭包捕获：用局部变量，避免循环变量共享
                    var entry = e;
                    btn.Click += delegate(CommandBarButton ctrl, ref bool cancelDefault)
                    {
                        cancelDefault = false;
                        Invoke(entry);
                    };

                    created++;
                }
                catch (Exception ex)
                {
                    Log.Error("创建菜单项失败: " + (e == null ? "(null)" : e.Caption), ex);
                }
            }

            return created;
        }

        /// <summary>CommandBar 版本的重载（菜单栏本身用）。</summary>
        public static int Populate(CommandBar bar, IList<MenuEntry> entries)
        {
            if (bar == null) return 0;
            return Populate(bar.Controls, entries);
        }

        /// <summary>执行菜单项回调，异常不许冒到 Shell。</summary>
        public static void Invoke(MenuEntry entry)
        {
            if (entry == null || entry.Action == null) return;

            try
            {
                Log.Info("菜单触发: " + entry.Caption);
                entry.Action();
            }
            catch (Exception ex)
            {
                Log.Error("菜单回调异常: " + entry.Caption, ex);
                ShowError("SSMS-SQLPlugin 出错", ex.Message);
            }
        }

        public static string SafeCaption(CommandBarControl c)
        {
            try { return c.Caption ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>去掉 mnemonic(&amp;) 和括号，用于标题比较。</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", string.Empty)
                    .Replace("(", string.Empty)
                    .Replace(")", string.Empty)
                    .Replace(" ", string.Empty)
                    .Trim();
        }

        public static void ShowError(string title, string message)
        {
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    message, title,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            catch { }
        }
    }
}
