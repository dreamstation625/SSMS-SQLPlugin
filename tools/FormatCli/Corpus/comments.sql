-- 文件头
/* 块注释
   第二行 */
SELECT
    a, -- 列1
    /* 列2前 */ b,
    c /* 列3后 */
FROM dbo.T -- 表
WHERE a = 1 /* 中间 */ AND b = 2 -- 行尾
   OR c = 3
GROUP BY -- 分组
    a, b, c
HAVING COUNT(*) > 0 -- 有数据
ORDER BY a; -- 排序
GO

WITH cte AS ( -- CTE 开始
    SELECT 1 AS n -- 常量
) -- CTE 结束
SELECT * FROM cte;
GO

SELECT MAX(
    CASE
        WHEN a = 1 -- 条件1
        THEN b -- 结果1
        ELSE c -- 其他
    END
) AS v
FROM dbo.T;
GO

MERGE INTO dbo.T AS t
USING dbo.S AS s
ON t.Id = s.Id -- 匹配条件
WHEN MATCHED THEN UPDATE SET t.V = s.V -- 更新分支
WHEN NOT MATCHED THEN INSERT (Id, V) VALUES (s.Id, s.V); -- 插入分支
GO
