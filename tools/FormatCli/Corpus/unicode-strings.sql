SELECT
    [中文列] AS 别名,
    "QuotedCol" AS "QuotedAlias",
    N'包含 ''单引号'' 和 -- 不是注释' AS 文本,
    N'多行
文本' AS 多行文本
FROM [dbo].[中文表] AS 表
WHERE 表.[中文列] = N'值';
GO
SELECT 1; SELECT 2;; SELECT 3;
GO
-- 只有注释
GO
SELECT 1
GO 3
SELECT 2
go -- 批次
SELECT 3
GO
