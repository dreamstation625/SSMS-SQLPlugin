using System;
using System.IO;
using System.Text;

namespace SsmsSqlPlugin.Probe
{
    /// <summary>
    /// 探针日志。写文件失败绝不能影响宿主，所以这里刻意吞掉异常。
    /// </summary>
    internal static class ProbeLog
    {
        private static readonly object Gate = new object();

        private static readonly string FilePath = Path.Combine(
            Path.GetTempPath(), "SsmsSqlPlugin", "probe.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.AppendAllText(
                        FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不抛：宿主里任何一个未捕获异常都可能导致包被静默禁用。
            }
        }
    }
}
