using System;
using System.Collections.Generic;

namespace SsmsSqlPlugin.Formatting.Tokens
{
    /// <summary>
    /// T-SQL 关键字表 + 分类。
    ///
    /// 为什么需要它：ScriptDom 的 TokenType 里每个关键字基本是独立枚举值（Select、From、Where…），
    /// 逐个 switch 不现实。这里维护一张表，用大写文本查，覆盖排版真正关心的子集。
    ///
    /// ⚠ 关键区分：**「标识符升级」和「关键字识别」是两件事**
    ///
    /// ScriptDom 会把一批词标成 Identifier，其中包括：
    ///   · 类型名 —— int / varchar / datetime …… 排版需要按关键字处理（大写）
    ///   · 伪关键字 —— level / status / type / name / key / count / max ……
    ///     这些**不是保留字**，完全可以当列名用
    ///
    /// 早先的实现是「Identifier 只要在 All 表里就升级成 Keyword」，结果
    ///     WHERE level = 'vip'
    /// 被格式化成
    ///     WHERE LEVEL = 'vip'
    /// —— 列名被吃掉了大小写，SQL 虽然还能跑但用户会炸。
    /// 现在只允许 <see cref="DataTypes"/> 里的词升级，其余保持 Identifier。
    /// </summary>
    internal static class SqlKeywords
    {
        /// <summary>
        /// 类型名：ScriptDom 报成 Identifier，但排版要按关键字大写。
        /// **只有这张表**里的词允许从 Identifier 升级为 Keyword。
        /// </summary>
        private static readonly HashSet<string> DataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INT","BIGINT","SMALLINT","TINYINT","BIT","DECIMAL","NUMERIC","FLOAT","REAL","MONEY",
            "SMALLMONEY","DATETIME","DATETIME2","DATETIMEOFFSET","SMALLDATETIME","DATE","TIME",
            "CHAR","VARCHAR","NCHAR","NVARCHAR","TEXT","NTEXT","IMAGE","BINARY","VARBINARY",
            "UNIQUEIDENTIFIER","XML","SQL_VARIANT","GEOGRAPHY","GEOMETRY","HIERARCHYID",
            "ROWVERSION","TIMESTAMP","SYSNAME","NVARCHAR_MAX",
        };

        /// <summary>
        /// 语法固定搭配词：ScriptDom 常常把它们标成 Identifier（因为不是保留字），
        /// 但在这些位置出现时必然是关键字语义，必须大写。
        ///
        /// 典型：MERGE 的 MATCHED / SOURCE / TARGET、
        ///       SET 选项的 NOCOUNT / XACT_ABORT。
        /// 判据靠上下文（见 SqlTokenizer），不靠"表里有就升级"。
        /// </summary>
        private static readonly HashSet<string> ContextualKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MATCHED","SOURCE","TARGET","NOCOUNT","XACT_ABORT","ANSI_NULLS","QUOTED_IDENTIFIER",
            "ROLLUP","CUBE","GROUPING","SETS","UNBOUNDED","PRECEDING","FOLLOWING","CURRENT",
            "ROWS","RANGE","WITHIN","FILTER",
        };

        /// <summary>不参与排版决策、但需要大写转换的保留字。</summary>
        private static readonly HashSet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 查询
            "SELECT","FROM","WHERE","GROUP","BY","HAVING","ORDER","ASC","DESC","DISTINCT","TOP",
            "OFFSET","FETCH","NEXT","ROWS","ONLY","PERCENT","WITH","TIES",
            // 连接
            "JOIN","INNER","LEFT","RIGHT","FULL","OUTER","CROSS","APPLY","ON","USING",
            // 逻辑与谓词
            "AND","OR","NOT","IN","EXISTS","ANY","ALL","SOME","BETWEEN","LIKE","ESCAPE","IS",
            "NULL","TRUE","FALSE","UNKNOWN",
            // 集合运算
            "UNION","INTERSECT","EXCEPT",
            // 数据修改
            "INSERT","INTO","VALUES","UPDATE","SET","DELETE","MERGE","MATCHED","TARGET","SOURCE",
            "OUTPUT","DEFAULT","IDENTITY","INSERTED","DELETED",
            // DDL
            "CREATE","ALTER","DROP","TABLE","VIEW","INDEX","PROCEDURE","PROC","FUNCTION","TRIGGER",
            "DATABASE","SCHEMA","COLUMN","CONSTRAINT","PRIMARY","FOREIGN","REFERENCES",
            "UNIQUE","CLUSTERED","NONCLUSTERED","CHECK","ADD","MODIFY","IF",
            "TEMPORARY","TEMP","OPTION",
            // 流程控制
            "BEGIN","END","ELSE","WHILE","BREAK","CONTINUE","RETURN","GOTO","WAITFOR","DELAY",
            "TRY","CATCH","THROW","RAISERROR",
            // 变量与游标
            "DECLARE","CURSOR","LOCAL","GLOBAL","FORWARD_ONLY","SCROLL","FAST_FORWARD","STATIC",
            "DYNAMIC","OPEN","CLOSE","DEALLOCATE","PRIOR","FIRST","LAST",
            // 执行与事务
            "EXEC","EXECUTE","SP_EXECUTESQL","TRAN","TRANSACTION","COMMIT","ROLLBACK","SAVE",
            "ISOLATION",
            // 窗口与聚合
            "OVER","PARTITION",
            // CASE
            "CASE","WHEN","THEN",
            // 其他常用
            "AS","USE","GO","NOLOCK","HOLDLOCK","UPDLOCK","XLOCK","ROWLOCK","PAGLOCK","TABLOCK",
            "READPAST","READCOMMITTED","READUNCOMMITTED","REPEATABLEREAD","SERIALIZABLE",
            "RECOMPILE","MAXDOP","FORCE","HASH","LOOP",
            "NOCOUNT","XACT_ABORT","ANSI_NULLS","QUOTED_IDENTIFIER",
            // 其它
            "COLLATE","PIVOT","UNPIVOT","TABLESAMPLE","FOR","JSON",
        };

        /// <summary>语句起始关键字：这些词出现在语句边界时，前后加空行。</summary>
        private static readonly HashSet<string> StatementStarters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","INSERT","UPDATE","DELETE","MERGE","CREATE","ALTER","DROP","TRUNCATE",
            "DECLARE","SET","IF","WHILE","BEGIN","RETURN","EXEC","EXECUTE","PRINT",
            "RAISERROR","THROW","WAITFOR","USE","GRANT","DENY","REVOKE","WITH","OPEN","CLOSE",
            "DEALLOCATE","FETCH","COMMIT","ROLLBACK","SAVE","DBCC","BACKUP","RESTORE",
        };

        /// <summary>主要子句关键字：这些词之前强制换行（在语句内部）。</summary>
        private static readonly HashSet<string> MajorClauses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","GROUP","HAVING","ORDER","UNION","INTERSECT","EXCEPT",
            "INSERT","UPDATE","DELETE","SET","VALUES","OUTPUT","INTO",
            "JOIN","INNER","LEFT","RIGHT","FULL","CROSS","OUTER","APPLY",
            "ON","USING","PIVOT","UNPIVOT","OPTION",
        };

        /// <summary>二级关键字：跟在主关键字后，不单独起行。</summary>
        private static readonly HashSet<string> SecondaryKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BY","ALL","DISTINCT","TOP","INTO","APPLY","OUTER","ONLY","TIES","MATCHED",
        };

        /// <summary>二元/前缀逻辑连接词：前后加断点。</summary>
        private static readonly HashSet<string> LogicalOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AND","OR",
        };

        /// <summary>比较运算符：保持左右各一个空格。</summary>
        private static readonly HashSet<string> ComparisonKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IS","NOT","IN","LIKE","BETWEEN","ESCAPE",
        };

        public static bool IsKeyword(string upper) => upper != null && All.Contains(upper);

        /// <summary>是否是类型名（允许从 Identifier 升级为 Keyword）。</summary>
        public static bool IsDataType(string upper) => upper != null && DataTypes.Contains(upper);

        public static bool IsStatementStarter(string upper) => upper != null && StatementStarters.Contains(upper);

        public static bool IsMajorClause(string upper) => upper != null && MajorClauses.Contains(upper);

        public static bool IsSecondaryKeyword(string upper) => upper != null && SecondaryKeywords.Contains(upper);

        public static bool IsLogicalOperator(string upper) => upper != null && LogicalOperators.Contains(upper);

        public static bool IsComparisonKeyword(string upper) => upper != null && ComparisonKeywords.Contains(upper);
    }
}
