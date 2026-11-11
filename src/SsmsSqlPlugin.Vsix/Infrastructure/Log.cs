using System;
using System.IO;
using System.Text;

namespace SsmsSqlPlugin.Infrastructure
{
    /// <summary>
    /// 插件日志。
    ///
    /// 写在 %TEMP%\SsmsSqlPlugin\plugin.log。
    /// 所有写入都吞异常 —— 宿主里任何一个未捕获异常都可能导致包被静默禁用，
    /// 日志绝对不能让主流程挂掉。
    ///
    /// 开发期靠这个文件排查，正式版应改为「仅错误时写」并给用户开关。
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        private static readonly string FilePath = Path.Combine(
            Path.GetTempPath(), "SsmsSqlPlugin", "plugin.log");

        public static string LogFilePath => FilePath;

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERR ", message);

        public static void Error(string message, Exception ex)
            => Write("ERR ", message + " :: " + Describe(ex));

        /// <summary>把异常的完整因果链摊平成一行，方便在日志里一眼看完。</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";

            var sb = new StringBuilder();
            var cur = ex;
            int depth = 0;

            while (cur != null && depth < 5)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }

            if (ex.StackTrace != null)
            {
                sb.Append(" | ").Append(FirstLine(ex.StackTrace));
            }

            return sb.ToString();
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl > 0 ? s.Substring(0, nl) : s;
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    File.AppendAllText(
                        FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + level + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不抛
            }
        }
    }
}
