using System;
using System.Drawing;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SsmsSqlPlugin.History;

namespace SsmsSqlPlugin.Infrastructure
{
    /// <summary>
    /// 读取 SSMS 当前主题，切换自定义窗口的浅/深色配色。
    /// 菜单本身由宿主绘制，会跟随主题；这里只处理 WinForms 窗口。
    /// </summary>
    internal static class ThemeManager
    {
        public static void Initialize(Func<Type, object> getService)
        {
            bool dark = false;
            string themeName = "Light";

            try
            {
                var shell = getService?.Invoke(typeof(SVsUIShell)) as IVsUIShell5;
                if (shell != null)
                {
                    var values = VsColors.GetCurrentThemedColorValues();
                    foreach (var pair in values)
                    {
                        string name = pair.Key?.Name ?? "";
                        if (name != "EnvironmentBackground" &&
                            name != "ToolWindowBackground")
                            continue;

                        Color background = Color.FromArgb(unchecked((int)pair.Value));
                        dark = background.GetBrightness() < 0.5f;
                        themeName = dark ? "Dark" : "Light";
                        break;
                    }
                }
                else
                {
                    Log.Warn("[主题] IVsUIShell5 不可用");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[主题] 读取 SSMS 主题失败: " + ex.Message);
            }

            HistoryTheme.Apply(dark);
            Log.Info("[主题] " + themeName);
        }
    }
}
