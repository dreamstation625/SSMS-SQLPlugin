using System;

namespace SsmsSqlPlugin.Core.Completion
{
    /// <summary>
    /// 补全打分。核心目标是词首感知：
    ///   libr → Lib_Reader（Lib 是词首，且 libr 后是词边界 _）
    /// 平台默认匹配器只做子串/模糊匹配，排不到前面，所以候选先自己算分。
    /// </summary>
    public static class CompletionScorer
    {
        public static bool Matches(string text, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return true;
            return Score(text, prefix) > 0;
        }

        public static int Score(string text, string prefix)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (string.IsNullOrEmpty(prefix)) return 1;

            if (text.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return 100000;

            int index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                // 不做子序列匹配：cbo_ 会被 ABC_...（c/b/o/_ 分散出现）误命中
                return 0;
            }

            bool wordStart = index == 0 || IsBoundary(text[index - 1]);
            if (!wordStart)
            {
                // 词中命中：给一个较低但可见的分数（例如 read 命中 Lib_Reader 的中间）
                return 100 + Math.Max(0, 40 - index);
            }

            int score = 1000 + Math.Max(0, 200 - text.Length);
            if (index == 0) score += 100;

            int end = index + prefix.Length;
            if (end == text.Length || IsBoundary(text[end])) score += 500;

            score -= Math.Min(index, 80) * 2;
            return score;
        }

        private static bool IsBoundary(char c)
            => c == '_' || c == '.' || c == '@' || c == '#' || c == ' ' || c == '[';

    }
}
