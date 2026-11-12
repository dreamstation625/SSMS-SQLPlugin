using System.Collections.Generic;

namespace SsmsSqlPlugin.Core.Metadata
{
    public sealed class TableMetadata
    {
        public string Schema;
        public string Name;
        /// <summary>sys.objects.type：U/V/TF/IF/P/PC；脚本临时表用 TEMP/CTE。</summary>
        public string Kind;
        public readonly List<ColumnMetadata> Columns = new List<ColumnMetadata>();

        public string FullName
            => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;

        private string KindTrimmed => (Kind ?? string.Empty).Trim();

        public bool IsProcedure
            => KindTrimmed == "P" || KindTrimmed == "PC";

        public bool IsView => KindTrimmed == "V";

        public bool IsTable
            => KindTrimmed == "U" || KindTrimmed == "TF" || KindTrimmed == "IF" ||
               KindTrimmed == "TEMP" || KindTrimmed == "CTE";
    }
}
