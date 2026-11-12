using System;

namespace SsmsSqlPlugin.Storage
{
    public sealed class FileRecord
    {
        public long Id { get; set; }
        public string FullPath { get; set; }
        public string FirstSeenUtc { get; set; }
        public string LastOpenedUtc { get; set; }
        public string LastSavedUtc { get; set; }
        public int OpenCount { get; set; }
    }

    public sealed class FileRevisionRecord
    {
        public long Id { get; set; }
        public long FileId { get; set; }
        public byte[] ContentId { get; set; }
        public string CapturedUtc { get; set; }
        public string Kind { get; set; }

        /// <summary>由 Contents 回填；超过快照上限时为空（Length 仍有原值）。</summary>
        public string SqlText { get; set; }
        public long SqlLength { get; set; }
    }

    public sealed class RetentionOptions
    {
        public int ExecutionDays { get; set; } = 90;
        public long MaxDatabaseBytes { get; set; } = 200L * 1024 * 1024;
        public int MaxRevisionsPerFile { get; set; } = 30;
        public int SnapshotMinIntervalSeconds { get; set; } = 60;
        public long MaxSnapshotBytes { get; set; } = 2L * 1024 * 1024;
        /// <summary>回收站保留天数；到期后彻底删除。</summary>
        public int RecycleBinDays { get; set; } = 30;
        /// <summary>按体积淘汰时至少保留最新的 N 条，避免删掉正在用的记录。</summary>
        public int ProtectRecentExecutions { get; set; } = 50;

        public static RetentionOptions Default => new RetentionOptions();
    }

    public sealed class RetentionResult
    {
        public int DeletedExecutions;
        public int DeletedRevisions;
        public int DeletedContents;
        public int PurgedRecycleBin;
        public bool Vacuumed;
        public long DatabaseBytesAfter;
        public DateTime RunUtc;
    }
}
