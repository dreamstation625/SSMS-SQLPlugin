using System;

namespace SsmsSqlPlugin.Storage
{
    public enum ExecutionDeletedFilter
    {
        Active = 0,
        Deleted = 1,
        All = 2,
    }

    public enum ExecutionKindFilter
    {
        All = 0,
        Execute = 1,
        Auto = 2,
    }

    /// <summary>SQL 历史窗口的查询条件。空字段表示不筛选。</summary>
    public sealed class ExecutionSearch
    {
        public string Text { get; set; }
        public string ServerName { get; set; }
        public string DatabaseName { get; set; }
        public DateTime? FromUtc { get; set; }
        public int Limit { get; set; } = 2000;

        /// <summary>默认只查正常历史；回收站查 Deleted。</summary>
        public ExecutionDeletedFilter Deleted { get; set; } = ExecutionDeletedFilter.Active;

        /// <summary>全部 / 仅执行 / 仅自动记录。</summary>
        public ExecutionKindFilter Kind { get; set; } = ExecutionKindFilter.All;
    }
}
