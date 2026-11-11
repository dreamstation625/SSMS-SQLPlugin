namespace SsmsSqlPlugin.Formatting
{
    /// <summary>格式化选项。后续会由 .sqlkit.json 驱动，这里先给最小集合。</summary>
    public sealed class FormatOptions
    {
        public static readonly FormatOptions Default = new FormatOptions();

        public bool UppercaseKeywords { get; set; } = true;
        public int IndentSize { get; set; } = 4;
        public bool IncludeSemicolons { get; set; } = true;
    }
}
