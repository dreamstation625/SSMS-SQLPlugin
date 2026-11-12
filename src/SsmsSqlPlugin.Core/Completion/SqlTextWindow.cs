using System;

namespace SsmsSqlPlugin.Core.Completion
{
    /// <summary>
    /// 取光标附近的一条语句窗口，避免在大脚本上每次按键都 tokenize 全文。
    /// 以分号/GO 为边界；没有边界时前后各留一段。
    /// </summary>
    internal sealed class SqlTextWindow
    {
        public int Start;
        public string Text;

        public static SqlTextWindow Around(string sql, int caret)
        {
            sql = sql ?? string.Empty;
            caret = Math.Max(0, Math.Min(caret, sql.Length));

            int start = Math.Max(0, caret - 6000);

            int lastSemicolon = -1;
            for (int i = caret - 1; i >= start; i--)
            {
                if (sql[i] == ';') { lastSemicolon = i; break; }
            }

            if (lastSemicolon >= 0)
            {
                start = lastSemicolon + 1;
            }
            else
            {
                int go = LastGoLine(sql, start, caret);
                if (go >= 0) start = go + 2;
            }

            int end = Math.Min(sql.Length, caret + 8000);
            for (int i = caret; i < end; i++)
            {
                if (sql[i] == ';') { end = i + 1; break; }
            }

            if (start > end) start = end;
            return new SqlTextWindow { Start = start, Text = sql.Substring(start, end - start) };
        }

        private static int LastGoLine(string sql, int from, int to)
        {
            int cursor = to;
            while (cursor > from)
            {
                int lineStart = sql.LastIndexOf('\n', cursor - 1, cursor - from);
                lineStart = lineStart < 0 ? from : lineStart + 1;

                string line = sql.Substring(lineStart, cursor - lineStart).Trim();
                if (line.Equals("GO", StringComparison.OrdinalIgnoreCase)) return lineStart;

                cursor = lineStart - 1;
            }
            return -1;
        }
    }
}
