using System.Text;
using SsmsSqlPlugin.Formatting;

Console.OutputEncoding = Encoding.UTF8;

var path = args.Length > 0 ? args[0] : null;
string sql;

if (path != null)
{
    sql = File.ReadAllText(path, Encoding.UTF8);
}
else
{
    // 没有参数时用内置样例，方便直接跑
    sql = """
        select a.id,a.name,b.amount from dbo.Account a inner join dbo.Bill b on b.accountId=a.id where a.status=1 and b.amount>100 order by b.amount desc
        GO
        -- 这一批含注释，应当原样保留
        select 1 as x -- 行尾注释
        GO
        select from where
        GO
        update dbo.Account set name='x', status=2 where id=1
        """;
}

var formatter = new SqlFormatter();
var result = formatter.Format(sql);

Console.WriteLine("================ 格式化结果 ================");
Console.WriteLine(result.Text);
Console.WriteLine("================ 诊断 ================");
Console.WriteLine($"成功批次: {result.FormattedBatches}  跳过批次: {result.SkippedBatches}  错误数: {result.Errors.Count}");
foreach (var e in result.Errors)
{
    Console.WriteLine("  - " + e);
}

// 幂等性自检：对已格式化的结果再跑一次，应当完全一致
var again = formatter.Format(result.Text);
Console.WriteLine($"幂等: {(again.Text == result.Text ? "是" : "否（需要修规则）")}");
