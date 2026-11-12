namespace SsmsSqlPlugin.Storage
{
    public sealed class ExecutionRecord
    {
        public long Id { get; set; }
        public string StartedUtc { get; set; }
        public long DurationMs { get; set; }
        public string ServerName { get; set; }
        public string DatabaseName { get; set; }
        public string LoginName { get; set; }
        public byte[] ContentId { get; set; }
        public int RowsAffected { get; set; }
        public int Succeeded { get; set; }
        public int ErrorNumber { get; set; }
        public string ErrorMessage { get; set; }
        public string SourcePath { get; set; }
        public string EditorMoniker { get; set; }
        public int IsSelectionOnly { get; set; }

        /// <summary>软删除时间；为 null 表示在正常历史里。</summary>
        public string DeletedUtc { get; set; }

        /// <summary>execute=手动执行；edit=修改后自动记录；timer=定时自动记录。</summary>
        public string Kind { get; set; }

        /// <summary>写入时由存储层从 SqlText 计算内容哈希；查询时由 JOIN Contents 回填。</summary>
        public string SqlText { get; set; }
    }
}
