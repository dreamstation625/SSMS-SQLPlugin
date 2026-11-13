using System;
using Microsoft.VisualStudio.CommandBars;
using SsmsSqlPlugin.Infrastructure;
using SsmsSqlPlugin.Localization;

namespace SsmsSqlPlugin.Menus
{
    /// <summary>
    /// 编辑器右键菜单注入。
    ///
    /// 编辑器右键菜单在 SSMS 里是一个叫 "Code Window" / "上下文菜单" 的
    /// CommandBarPopup，通过 DTE.CommandBars 拿不到稳定标题（中文版是
    /// 「代码窗口」，英文版是 "Code Window"）。
    ///
    /// 做法：拿到当前活动视图后，用 vsView.GetSelection 拿到光标位置，
    /// 交给 Shell 弹自己的右键菜单；我们只在**菜单弹出前**把项加进去。
    /// 但 VS Automation 没有"菜单即将弹出"的事件……
    ///
    /// 所以实际可行的两条路：
    ///   A) CommandBars["Code Window"] —— 在部分版本可直接取到并常驻添加
    ///   B) 快捷键 + 顶部菜单 —— 完全不依赖右键菜单
    ///
    /// 本类走 A，失败则静默降级（B 兜底），不影响主功能。
    /// </summary>
    internal sealed class EditorContextMenu
    {
        private const string TagPrefix = "SsmsSqlPlugin:";

        /// <summary>VS / SSMS 里编辑器右键菜单的可能名称（多语言）。</summary>
        private static readonly string[] CandidateNames =
        {
            "Code Window",
            "代码窗口",
            "Editor Context",
            "编辑器上下文",
            "Text Editor",
            "Context Menu",
            "上下文菜单",
        };

        private readonly Action _format;
        private readonly Action _formatSelection;

        public EditorContextMenu(Action format, Action formatSelection)
        {
            _format = format;
            _formatSelection = formatSelection;
        }

        /// <summary>
        /// 尝试把「格式化 SQL」加进编辑器右键菜单。
        /// 返回 true = 成功。失败不是错误 —— 快捷键和顶部菜单仍然可用。
        /// </summary>
        public bool TryInstall(EnvDTE.DTE dte)
        {
            if (dte == null) return false;

            CommandBars cmdBars;
            try
            {
                cmdBars = dte.CommandBars as CommandBars;
                if (cmdBars == null)
                {
                    Log.Warn("右键菜单：cmdBars 强类型转换失败，跳过");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("右键菜单：取 CommandBars 失败 " + ex.Message);
                return false;
            }

            foreach (var name in CandidateNames)
            {
                CommandBar bar = null;
                try
                {
                    bar = cmdBars[name];
                }
                catch
                {
                    continue;   // 这个名字不存在
                }

                if (bar == null) continue;

                try
                {
                    int n = bar.Controls.Count;
                    Log.Info("右键菜单：命中 \"" + name + "\"，现有子项 " + n + " 个");

                    // 清旧的
                    CommandBarInjector.RemoveByTag(bar, TagPrefix + "CtxFormat");
                    CommandBarInjector.RemoveByTag(bar, TagPrefix + "CtxFormatSelection");

                    var entries = new System.Collections.Generic.List<MenuEntry>
                    {
                        new MenuEntry(Loc.T("Format SQL"),
                                      Loc.T("Format the current selection or the whole document"),
                                      _format, TagPrefix + "CtxFormat"),
                        new MenuEntry(Loc.T("Format Selection"),
                                      Loc.T("Format only the selected SQL"),
                                      _formatSelection, TagPrefix + "CtxFormatSelection"),
                    };

                    for (int i = 0; i < entries.Count; i++)
                    {
                        var added = bar.Controls.Add(
                            MsoControlType.msoControlButton,
                            Type.Missing, Type.Missing,
                            bar.Controls.Count + 1, true);

                        var btn = (CommandBarButton)added;
                        var entry = entries[i];
                        btn.Caption = entry.Caption;
                        btn.Tag = entry.Tag;
                        try
                        {
                            btn.TooltipText = i == 0
                                ? Loc.T("Format the current selection or whole document (Ctrl+K, Ctrl+D)")
                                : Loc.T("Format only the selected SQL (Ctrl+K, Ctrl+F)");
                        }
                        catch { }
                        btn.BeginGroup = i == 0;   // 前面画一条分隔线
                        btn.Visible = true;
                        btn.Click += delegate(CommandBarButton ctrl, ref bool cancelDefault)
                        {
                            cancelDefault = false;
                            CommandBarInjector.Invoke(entry);
                        };
                    }

                    Log.Info("右键菜单：已注入「格式化 SQL / 格式化选定内容」");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("右键菜单：往 \"" + name + "\" 注入失败 " + ex.Message);
                }
            }

            Log.Warn("右键菜单：没找到可注入的编辑器上下文菜单（快捷键与顶部菜单仍可用）");
            return false;
        }
    }
}
