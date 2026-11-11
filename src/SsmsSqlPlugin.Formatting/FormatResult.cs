using System.Collections.Generic;

namespace SsmsSqlPlugin.Formatting
{
    /// <summary>格式化结果。<see cref="Text"/> 永远是可直接写回编辑器的文本。</summary>
    public sealed class FormatResult
    {
        public FormatResult(string text, IReadOnlyList<FormatError> errors, int formattedBatches, int skippedBatches)
        {
            Text = text;
            Errors = errors;
            FormattedBatches = formattedBatches;
            SkippedBatches = skippedBatches;
        }

        public string Text { get; }

        /// <summary>解析失败的批次。空表示全部成功。</summary>
        public IReadOnlyList<FormatError> Errors { get; }

        public int FormattedBatches { get; }

        /// <summary>跳过的批次数（含注释批 + 解析失败批）。</summary>
        public int SkippedBatches { get; }

        public bool IsSuccess => Errors.Count == 0;

        public static FormatResult Empty(string text) =>
            new FormatResult(text, new List<FormatError>(), 0, 0);
    }

    public sealed class FormatError
    {
        public FormatError(int line, int column, string message)
        {
            Line = line;
            Column = column;
            Message = message;
        }

        public int Line { get; }
        public int Column { get; }
        public string Message { get; }

        public override string ToString() =>
            Line > 0 ? $"第 {Line} 行第 {Column} 列：{Message}" : Message;
    }
}
