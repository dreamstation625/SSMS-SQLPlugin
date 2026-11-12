using System.Collections.Generic;

namespace SsmsSqlPlugin.Core.Completion
{
    /// <summary>不依赖数据库的内置候选：关键字、函数、数据类型。</summary>
    public static class BuiltInCatalog
    {
        public sealed class Entry
        {
            public string Text;
            public string Description;
            public CompletionKind Kind;
        }

        private static readonly Entry[] Keywords =
        {
            E("SELECT", "查询数据"), E("FROM", "指定来源表"), E("WHERE", "筛选条件"),
            E("GROUP BY", "分组"), E("HAVING", "分组后筛选"), E("ORDER BY", "排序"),
            E("DISTINCT", "去重"), E("TOP", "限制行数"),
            E("INNER JOIN", "内连接"), E("LEFT JOIN", "左连接"), E("RIGHT JOIN", "右连接"),
            E("FULL JOIN", "全连接"), E("CROSS JOIN", "交叉连接"), E("CROSS APPLY", "相关表值函数"),
            E("OUTER APPLY", "相关表值函数"), E("ON", "连接条件"), E("AS", "别名"),
            E("AND", "并且"), E("OR", "或者"), E("NOT", "取反"), E("IN", "集合内"),
            E("EXISTS", "存在"), E("BETWEEN", "区间"), E("LIKE", "模式匹配"), E("IS NULL", "为空"),
            E("IS NOT NULL", "不为空"), E("CASE", "条件表达式"), E("WHEN", "条件"), E("THEN", "结果"),
            E("ELSE", "否则"), E("END", "结束"),
            E("INSERT", "插入"), E("INTO", "目标"), E("VALUES", "值列表"),
            E("UPDATE", "更新"), E("SET", "赋值"), E("DELETE", "删除"), E("MERGE", "合并"),
            E("OUTPUT", "输出"), E("DEFAULT", "默认值"),
            E("CREATE", "创建"), E("ALTER", "修改"), E("DROP", "删除对象"), E("TABLE", "表"),
            E("VIEW", "视图"), E("PROCEDURE", "存储过程"), E("FUNCTION", "函数"),
            E("INDEX", "索引"), E("WITH", "CTE / 提示"), E("UNION", "并集"), E("UNION ALL", "并集保留重复"),
            E("INTERSECT", "交集"), E("EXCEPT", "差集"),
            E("EXEC", "执行"), E("EXECUTE", "执行"), E("DECLARE", "声明变量"), E("BEGIN", "开始"),
            E("COMMIT", "提交事务"), E("ROLLBACK", "回滚事务"), E("TRAN", "事务"),
            E("IF", "条件"), E("WHILE", "循环"), E("RETURN", "返回"), E("PRINT", "打印"),
            E("TRY", "捕获开始"), E("CATCH", "捕获"), E("THROW", "抛错"),
            E("USE", "切换数据库"), E("GO", "批分隔符"), E("NULL", "空值"), E("TRUE", "真"), E("FALSE", "假"),
        };

        private static readonly Entry[] Functions =
        {
            E("COUNT", "行数"), E("SUM", "求和"), E("AVG", "平均值"), E("MIN", "最小值"), E("MAX", "最大值"),
            E("ISNULL", "空值替换"), E("COALESCE", "首个非空值"), E("NULLIF", "相等则空"),
            E("CAST", "类型转换"), E("TRY_CAST", "安全类型转换"), E("CONVERT", "类型转换"),
            E("TRY_CONVERT", "安全类型转换"), E("GETDATE", "当前时间"), E("SYSDATETIME", "当前时间"),
            E("DATEADD", "日期加减"), E("DATEDIFF", "日期差"), E("DATEPART", "日期部分"),
            E("FORMAT", "格式化"), E("CONCAT", "拼接"), E("CONCAT_WS", "带分隔符拼接"),
            E("LEN", "长度"), E("DATALENGTH", "字节长度"), E("SUBSTRING", "截取"),
            E("LEFT", "左侧截取"), E("RIGHT", "右侧截取"), E("CHARINDEX", "查找位置"),
            E("REPLACE", "替换"), E("STUFF", "替换插入"), E("UPPER", "转大写"), E("LOWER", "转小写"),
            E("LTRIM", "去左空格"), E("RTRIM", "去右空格"), E("TRIM", "去空格"),
            E("ROUND", "四舍五入"), E("ABS", "绝对值"), E("FLOOR", "向下取整"), E("CEILING", "向上取整"),
            E("ROW_NUMBER", "行号"), E("RANK", "排名"), E("DENSE_RANK", "密集排名"),
            E("NTILE", "分桶"), E("LAG", "前一行"), E("LEAD", "后一行"),
            E("STRING_AGG", "字符串聚合"), E("IIF", "条件函数"), E("CHOOSE", "按序号选择"),
            E("OBJECT_ID", "对象 ID"), E("SCOPE_IDENTITY", "当前作用域自增 ID"),
        };

        private static readonly Entry[] DataTypes =
        {
            E("INT", "整数"), E("BIGINT", "大整数"), E("SMALLINT", "小整数"), E("TINYINT", "微整数"),
            E("BIT", "布尔"), E("DECIMAL", "定点数"), E("NUMERIC", "定点数"), E("FLOAT", "浮点数"),
            E("REAL", "浮点数"), E("MONEY", "货币"), E("DATETIME", "日期时间"), E("DATETIME2", "日期时间"),
            E("DATE", "日期"), E("TIME", "时间"), E("CHAR", "定长字符"), E("VARCHAR", "变长字符"),
            E("NCHAR", "定长 Unicode"), E("NVARCHAR", "变长 Unicode"), E("TEXT", "长文本"),
            E("NTEXT", "长 Unicode 文本"), E("BINARY", "二进制"), E("VARBINARY", "变长二进制"),
            E("UNIQUEIDENTIFIER", "GUID"), E("XML", "XML"), E("SQL_VARIANT", "任意类型"),
        };

        private static Entry E(string text, string desc)
            => new Entry { Text = text, Description = desc };

        static BuiltInCatalog()
        {
            foreach (var e in Keywords) e.Kind = CompletionKind.Keyword;
            foreach (var e in Functions) e.Kind = CompletionKind.Function;
            foreach (var e in DataTypes) e.Kind = CompletionKind.DataType;
        }

        public static IReadOnlyList<Entry> KeywordEntries => Keywords;
        public static IReadOnlyList<Entry> FunctionEntries => Functions;
        public static IReadOnlyList<Entry> DataTypeEntries => DataTypes;
    }
}
