using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SsmsSqlPlugin.Formatting;

// 排版引擎语料回归：输出效果 + 幂等校验
// 用法：
//   dotnet run              → 跑全部，打印每个用例
//   dotnet run -- --quiet   → 只打印失败项和统计
//   dotnet run -- --idem    → 只做幂等校验
class Program
{
    static int Main(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        bool idemOnly = args.Contains("--idem");

        var cases = LoadCases();
        var opt = FormatOptions.Default;
        opt.MaxLineWidth = 100;

        int pass = 0, fail = 0;
        var failures = new List<string>();

        foreach (var c in cases)
        {
            string once, twice;
            try
            {
                var r1 = new SqlFormatter().Format(c.Sql, opt);
                once = r1.Text;
                var r2 = new SqlFormatter().Format(once, opt);
                twice = r2.Text;
            }
            catch (Exception ex)
            {
                fail++;
                failures.Add(c.Name + " (异常: " + ex.Message + ")");
                continue;
            }

            bool idem = once == twice;

            if (!quiet && !idemOnly)
            {
                Console.WriteLine(new string('=', 78));
                Console.WriteLine("【" + c.Name + "】");
                Console.WriteLine(new string('-', 78));
                Console.WriteLine("输入:");
                Console.WriteLine(c.Sql);
                Console.WriteLine();
                Console.WriteLine("输出:");
                Console.WriteLine(once);
                Console.WriteLine();
            }

            if (idem)
            {
                pass++;
            }
            else
            {
                fail++;
                failures.Add(c.Name);
                Console.WriteLine(new string('=', 78));
                Console.WriteLine("⚠ 幂等失败: " + c.Name);
                Console.WriteLine("--- 第一次 ---");
                Console.WriteLine(once);
                Console.WriteLine("--- 第二次 ---");
                Console.WriteLine(twice);
                Console.WriteLine();
            }
        }

        Console.WriteLine(new string('=', 78));
        Console.WriteLine("用例总数: " + cases.Count + "   幂等通过: " + pass + "   失败: " + fail);
        if (failures.Count > 0)
        {
            Console.WriteLine("失败项:");
            foreach (var f in failures) Console.WriteLine("  - " + f);
        }
        return fail == 0 ? 0 : 1;
    }

    private sealed class Case
    {
        public string Name;
        public string Sql;
    }

    private static List<Case> LoadCases()
    {
        return new List<Case>
        {
            C("简单 SELECT", "select a,b,c from t where x=1 and y=2"),
            C("带行尾注释", @"-- 顶部注释
select a.id,a.name as 姓名 from users a where a.status=1 -- 行尾
and a.amount>100"),
            C("多表 JOIN", "select a.id,b.v from dbo.users a inner join dbo.orders b on a.id=b.uid left join dbo.t3 c on c.id=a.id where a.s=1"),
            C("子查询", "select * from (select id,name from users where status=1) x where x.id>10"),
            C("嵌套子查询", "select * from (select * from (select id from t where a=1) p where p.id>2) q where q.id<9"),
            C("INSERT 多值", "insert into t(a,b,c) values(1,2,3),(4,5,6),(7,8,9)"),
            C("INSERT SELECT", "insert into t(a,b) select x,y from s where x>0"),
            C("UPDATE", "update t set a=1,b=2 where id=3"),
            C("DELETE", "delete from t where id=3"),
            C("MERGE", "merge into tgt as t using src as s on t.id=s.id when matched then update set t.v=s.v when not matched then insert(id,v) values(s.id,s.v);"),
            C("CASE", "select case when a=1 then 'x' when a=2 then 'y' else 'z' end as r from t"),
            C("窗口函数", "select id,row_number() over(partition by a order by b desc) as rn,sum(v) over(partition by a) as s from t"),
            C("DDL 建表", "create table t(id int not null primary key,n nvarchar(50) default N'',c datetime)"),
            C("DDL 索引", "create nonclustered index ix_t_a on t(a) include(b,c) where a is not null"),
            C("ALTER TABLE", "alter table t add c2 int null, c3 varchar(20) not null"),
            C("IF BEGIN END", "if @x>0 begin print 'a' end else begin print 'b' end"),
            C("WHILE", "while @i<10 begin set @i=@i+1 if @i=5 break end"),
            C("TRY CATCH", "begin try select 1/0 end try begin catch print error_message() end catch"),
            C("DECLARE", "declare @a int=1,@b varchar(20)='x'"),
            C("EXEC", "exec dbo.p @a=1,@b=2"),
            C("GO 分批", "select 1\ngo\nselect 2"),
            C("GO 重复次数", "select 1\ngo 3\nselect 2"),
            C("长列清单折行", "select customer_id, customer_name, order_date, total_amount, status, created_by, modified_at, notes, remark1, remark2 from dbo.customer_orders where status = 'active'"),
            C("长 WHERE", "select * from t where aaaaaaaaaaaa = 1 and bbbbbbbbbbbb = 2 and cccccccccccc = 3 and dddddddddddd = 4 and eeeeeeeeeeee = 5"),
            C("块注释-行内", "select 1 /* 中间注释 */ , 2"),
            C("块注释-多行", "select 1\n/* 第一行\n   第二行\n   第三行 */\n, 2"),
            C("CTE", "with cte as (select 1 as n) select * from cte"),
            C("UNION", "select a from t1 union all select b from t2"),
            C("IN 列表", "select * from t where id in (1,2,3,4,5)"),
            C("BETWEEN LIKE", "select * from t where a between 1 and 9 and b like '%x%'"),
            C("中文标识符", "select 用户编号, 用户名称 from 用户表 where 状态=1"),
            C("复杂真实查询", @"-- 月度销售汇总
select c.customer_id,c.customer_name,sum(o.amount) as total,count(*) as cnt
from dbo.customers c
inner join dbo.orders o on o.customer_id=c.customer_id
where o.order_date>='2026-01-01' and o.status<>'cancelled' -- 排除已取消
group by c.customer_id,c.customer_name
having sum(o.amount)>1000
order by total desc"),

            // ── 子查询展开 ──
            C("FROM 子查询展开", "select x.id, x.name from (select id, name from users where status = 1 and type = 2 and city = 'SH') x where x.id > 10"),
            C("WHERE IN 子查询", "select * from orders where customer_id in (select id from customers where level = 'vip' and created_at > '2026-01-01')"),
            C("EXISTS 子查询", "select * from t1 where exists (select 1 from t2 where t2.id = t1.id and t2.flag = 1) and t1.v > 0"),
            C("标量子查询", "select id, (select max(v) from t2 where t2.a = t1.a) as mx, name from t1"),
            C("CTE 多段", "with a as (select id, v from t1 where v > 0 and v < 100), b as (select id from a where id in (select id from t3)) select * from b"),

            // ── 控制流 ──
            C("IF ELSE IF", "if @a = 1 begin select 1 end else if @a = 2 begin select 2 end else begin select 3 end"),
            C("嵌套 BEGIN", "begin begin print 'a' end print 'b' end"),
            C("WHILE 带块", "while @i < 10 begin set @i = @i + 1 if @i = 5 begin break end end"),
            C("IF 单语句", "if @x > 0 print 'positive'"),
            C("CASE 嵌套在 SELECT", "select id, case when a = 1 then 'x' when a = 2 then case when b = 1 then 'y1' else 'y2' end else 'z' end as r from t"),

            // ── BETWEEN / 边界 ──
            C("BETWEEN 带 AND", "select * from t where d between '2026-01-01' and '2026-12-31' and s = 1"),
            C("NOT BETWEEN", "select * from t where a not between 1 and 9"),
            C("复杂表达式", "select a + b * c - d / e as v, (a + b) * (c - d) as w from t"),

            // ── 注释 ──
            C("多行注释夹在语句中", "select a, -- 第一列\n b /* 第二列 */, c from t"),
            C("注释后接 AND", "select * from t where a = 1 -- 注释\n and b = 2 -- 另一条\n and c = 3"),
            C("纯注释批", "-- 只有注释\n-- 什么都不做"),

            // ── 真实场景 ──
            C("分页查询", "select * from (select row_number() over (order by id desc) as rn, t.* from t where status = 1) x where x.rn between 1 and 20"),
            C("存储过程体", @"create procedure p @a int as
begin
    set nocount on
    if @a is null begin return end
    select id, name from t where v = @a
end"),
            C("UPDATE FROM", "update t set t.v = s.v, t.u = s.u from tgt t inner join src s on s.id = t.id where s.flag = 1"),
            C("DELETE JOIN", "delete t from tgt t inner join src s on s.id = t.id where s.flag = 0"),
            C("MERGE 多分支", @"merge into tgt as t using src as s on t.id = s.id
when matched and s.flag = 1 then update set t.v = s.v
when matched then delete
when not matched by target then insert (id, v) values (s.id, s.v)
when not matched by source then delete;"),
            C("GROUP BY ROLLUP", "select a, b, sum(v) as s from t group by rollup(a, b) having sum(v) > 0 order by a, b"),
            C("窗口 frame", "select id, sum(v) over (partition by a order by b rows between unbounded preceding and current row) as run from t"),
            C("PIVOT", "select * from (select a, b, v from t) p pivot (sum(v) for b in ([x], [y], [z])) as pv"),
            C("STRING_AGG", "select a, string_agg(cast(b as nvarchar(max)), ',') within group (order by b) as bs from t group by a"),
            C("长 IN 列表", "select * from t where status in ('a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p')"),
            C("多语句串", "declare @a int = 1; set @a = 2; select @a as v;"),
        };
    }

    private static Case C(string name, string sql) => new Case { Name = name, Sql = sql };
}
