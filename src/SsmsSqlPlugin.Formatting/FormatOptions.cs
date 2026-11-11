namespace SsmsSqlPlugin.Formatting
{
    /// <summary>SELECT 列表的排版风格。</summary>
    public enum SelectListStyle
    {
        /// <summary>放得下就一行，放不下才每列一行。</summary>
        Auto = 0,

        /// <summary>两列及以上时，强制每列一行（垂直风格，SQL Prompt / VSCode 常见）。</summary>
        Vertical = 1,
    }

    /// <summary>
    /// 格式化选项。后续由 .sqlkit.json 驱动，字段命名对齐 SQL Prompt 以减少迁移成本。
    /// </summary>
    public sealed class FormatOptions
    {
        public static readonly FormatOptions Default = new FormatOptions();

        /// <summary>关键字大写（false = 小写）。</summary>
        public bool UppercaseKeywords { get; set; } = true;

        /// <summary>缩进空格数。</summary>
        public int IndentSize { get; set; } = 4;

        /// <summary>行宽预算，超过则折行。</summary>
        public int MaxLineWidth { get; set; } = 100;

        /// <summary>
        /// SELECT 列表风格。默认 <see cref="SelectListStyle.Vertical"/>：
        /// 两列及以上时每列一行。单列（含 SELECT *）保持同行，避免
        /// "SELECT\n    1" 这种无意义展开。
        /// </summary>
        public SelectListStyle SelectListStyle { get; set; } = SelectListStyle.Vertical;

        /// <summary>是否补分号。注意：目前只影响是否保留原文的分号，不会主动添加。</summary>
        public bool IncludeSemicolons { get; set; } = true;

        /// <summary>括号内是否展开（放不下时每项一行）；false 则尽量压平。</summary>
        public bool ExpandedParentheses { get; set; } = true;

        /// <summary>子查询（括号里是 SELECT）是否独立成块展开。</summary>
        public bool ExpandSubqueries { get; set; } = true;

        /// <summary>语句之间空行数。</summary>
        public int BlankLinesBetweenStatements { get; set; } = 1;

        /// <summary>是否保留注释。false 时注释被丢弃（默认必须 true）。</summary>
        public bool PreserveComments { get; set; } = true;

        /// <summary>输出换行符。null = 自动跟随输入（有 \r\n 用 \r\n，否则 \n）。</summary>
        public string NewLine { get; set; }

        public FormatOptions Clone() => (FormatOptions)MemberwiseClone();
    }
}
