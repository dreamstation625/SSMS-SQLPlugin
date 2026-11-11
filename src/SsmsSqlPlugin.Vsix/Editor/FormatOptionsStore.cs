using System;
using System.IO;
using System.Text;
using SsmsSqlPlugin.Formatting;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// 格式化选项的值对象（只含用户可调的项）。
    /// </summary>
    internal sealed class FormatOptionsSnapshot
    {
        public int IndentSize = 4;
        public bool UppercaseKeywords = true;
        public bool VerticalSelectList = true;
        public bool ExpandSubqueries = true;
        public int MaxLineWidth = 100;

        public FormatOptionsSnapshot Clone()
        {
            return new FormatOptionsSnapshot
            {
                IndentSize = IndentSize,
                UppercaseKeywords = UppercaseKeywords,
                VerticalSelectList = VerticalSelectList,
                ExpandSubqueries = ExpandSubqueries,
                MaxLineWidth = MaxLineWidth,
            };
        }

        /// <summary>转成排版引擎认识的 FormatOptions。</summary>
        public FormatOptions ToFormatOptions()
        {
            return new FormatOptions
            {
                IndentSize = IndentSize <= 0 ? 4 : IndentSize,
                UppercaseKeywords = UppercaseKeywords,
                MaxLineWidth = MaxLineWidth <= 0 ? 100 : MaxLineWidth,
                SelectListStyle = VerticalSelectList ? SelectListStyle.Vertical : SelectListStyle.Auto,
                ExpandSubqueries = ExpandSubqueries,
                // 换行符交给引擎自动跟随输入
                NewLine = null,
            };
        }
    }

    /// <summary>
    /// 选项的单例持有 + 持久化。
    ///
    /// 存到 %LOCALAPPDATA%\SsmsSqlPlugin\options.json。
    /// 手写极简 JSON —— 只有一个扁平对象，不值得为它引 JSON 库
    /// （也避免在 VSIX 里多带一个依赖 DLL）。
    ///
    /// 读取失败一律回落到默认值，绝不因为配置文件坏了就不工作。
    /// </summary>
    internal static class FormatOptionsStore
    {
        private static readonly object Gate = new object();

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SsmsSqlPlugin");

        private static string FilePath => Path.Combine(Dir, "options.json");

        public static string ConfigPath => FilePath;

        private static FormatOptionsSnapshot _current;

        /// <summary>当前生效的选项。首次访问时从磁盘加载。</summary>
        public static FormatOptionsSnapshot Current
        {
            get
            {
                lock (Gate)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
            set
            {
                lock (Gate) { _current = value ?? new FormatOptionsSnapshot(); }
            }
        }

        /// <summary>更新并落盘。</summary>
        public static void Update(Action<FormatOptionsSnapshot> mutate)
        {
            if (mutate == null) return;

            lock (Gate)
            {
                var s = (_current ?? Load()).Clone();
                mutate(s);
                _current = s;
                SaveLocked(s);
            }
        }

        public static void Save()
        {
            lock (Gate) { SaveLocked(_current ?? new FormatOptionsSnapshot()); }
        }

        private static void SaveLocked(FormatOptionsSnapshot s)
        {
            try
            {
                Directory.CreateDirectory(Dir);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"indentSize\": " + s.IndentSize + ",");
                sb.AppendLine("  \"uppercaseKeywords\": " + Bool(s.UppercaseKeywords) + ",");
                sb.AppendLine("  \"verticalSelectList\": " + Bool(s.VerticalSelectList) + ",");
                sb.AppendLine("  \"expandSubqueries\": " + Bool(s.ExpandSubqueries) + ",");
                sb.AppendLine("  \"maxLineWidth\": " + s.MaxLineWidth);
                sb.AppendLine("}");

                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("保存选项失败: " + ex.Message);
            }
        }

        private static FormatOptionsSnapshot Load()
        {
            var s = new FormatOptionsSnapshot();

            try
            {
                if (!File.Exists(FilePath)) return s;

                string json = File.ReadAllText(FilePath);

                s.IndentSize = ReadInt(json, "indentSize", s.IndentSize);
                s.UppercaseKeywords = ReadBool(json, "uppercaseKeywords", s.UppercaseKeywords);
                s.VerticalSelectList = ReadBool(json, "verticalSelectList", s.VerticalSelectList);
                s.ExpandSubqueries = ReadBool(json, "expandSubqueries", s.ExpandSubqueries);
                s.MaxLineWidth = ReadInt(json, "maxLineWidth", s.MaxLineWidth);
            }
            catch (Exception ex)
            {
                Log.Warn("读取选项失败，使用默认值: " + ex.Message);
            }

            return s;
        }

        private static string Bool(bool b) => b ? "true" : "false";

        private static int ReadInt(string json, string key, int fallback)
        {
            string raw = ReadRaw(json, key);
            int v;
            return raw != null && int.TryParse(raw, out v) ? v : fallback;
        }

        private static bool ReadBool(string json, string key, bool fallback)
        {
            string raw = ReadRaw(json, key);
            if (raw == null) return fallback;
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从极简 JSON 里抠出 "key": value 的 value。</summary>
        private static string ReadRaw(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;

            int colon = json.IndexOf(':', i);
            if (colon < 0) return null;

            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;

            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' &&
                   json[end] != '\r' && json[end] != '\n')
                end++;

            return json.Substring(start, end - start).Trim();
        }
    }
}
