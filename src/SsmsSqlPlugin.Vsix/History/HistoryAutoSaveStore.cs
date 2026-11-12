using System;
using System.IO;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.History
{
    internal enum AutoSaveMode
    {
        Off = 0,
        OnEdit = 1,
        Interval = 2,
    }

    internal sealed class AutoSaveSettings
    {
        public bool Enabled = true;
        public AutoSaveMode Mode = AutoSaveMode.OnEdit;
        public int IntervalMinutes = 5;

        public AutoSaveSettings Clone()
        {
            return new AutoSaveSettings
            {
                Enabled = Enabled,
                Mode = Mode,
                IntervalMinutes = IntervalMinutes,
            };
        }
    }

    /// <summary>
    /// 自动记录设置：关闭 / 修改后记录 / 每 N 分钟。
    /// 存 %LOCALAPPDATA%\SsmsSqlPlugin\autosave.cfg，格式极简 key=value。
    /// </summary>
    internal static class HistoryAutoSaveStore
    {
        private static readonly object Gate = new object();

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SsmsSqlPlugin");

        private static string FilePath => Path.Combine(Dir, "autosave.cfg");

        public static string ConfigPath => FilePath;

        private static AutoSaveSettings _current = Load();

        public static AutoSaveSettings Current
        {
            get { lock (Gate) { return _current.Clone(); } }
        }

        public static void Update(Action<AutoSaveSettings> mutate)
        {
            if (mutate == null) return;

            lock (Gate)
            {
                mutate(_current);
                Save(_current);
            }
        }

        public static string Describe(AutoSaveSettings settings)
        {
            if (settings == null || !settings.Enabled) return "关闭";
            if (settings.Mode == AutoSaveMode.OnEdit) return "修改后记录";
            return "每 " + Math.Max(1, settings.IntervalMinutes) + " 分钟";
        }

        private static AutoSaveSettings Load()
        {
            var settings = new AutoSaveSettings();

            try
            {
                if (!File.Exists(FilePath)) return settings;

                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.Enabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) settings.Mode = AutoSaveMode.Off;
                        else if (value.Equals("interval", StringComparison.OrdinalIgnoreCase)) settings.Mode = AutoSaveMode.Interval;
                        else settings.Mode = AutoSaveMode.OnEdit;
                    }
                    else if (key.Equals("interval", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("intervalMinutes", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int minutes) && minutes > 0)
                            settings.IntervalMinutes = minutes;
                    }
                }

                if (settings.Mode == AutoSaveMode.Off) settings.Enabled = false;
                if (!settings.Enabled) settings.Mode = AutoSaveMode.Off;
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 读取自动记录设置失败，使用默认值: " + ex.Message);
            }

            return settings;
        }

        private static void Save(AutoSaveSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Dir);

                string mode = settings.Mode == AutoSaveMode.Off
                    ? "off"
                    : settings.Mode == AutoSaveMode.Interval ? "interval" : "edit";

                File.WriteAllText(FilePath,
                    "enabled=" + (settings.Enabled && settings.Mode != AutoSaveMode.Off ? "1" : "0") + Environment.NewLine +
                    "mode=" + mode + Environment.NewLine +
                    "interval=" + Math.Max(1, settings.IntervalMinutes) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.Warn("[历史] 保存自动记录设置失败: " + ex.Message);
            }
        }
    }
}
