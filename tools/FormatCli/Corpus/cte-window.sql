-- 递归 CTE + 多层 CTE + 窗口函数 + 分组
WITH Tree AS (
    SELECT Id, ParentId, Name, 1 AS Lvl
    FROM dbo.Nodes
    WHERE ParentId IS NULL
    UNION ALL
    SELECT c.Id, c.ParentId, c.Name, p.Lvl + 1
    FROM dbo.Nodes c
    INNER JOIN Tree p ON p.Id = c.ParentId
),
Ranked AS (
    SELECT t.Id,
           t.Name,
           t.Lvl,
           ROW_NUMBER() OVER (PARTITION BY t.ParentId ORDER BY t.Name) AS rn,
           SUM(t.Amount) OVER (PARTITION BY t.ParentId ORDER BY t.Id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
    FROM Tree t
)
SELECT r.Id, r.Name, r.Lvl, r.rn, r.running,
       GROUPING(r.Lvl) AS g
FROM Ranked r
GROUP BY GROUPING SETS ((r.Id, r.Name, r.Lvl, r.rn, r.running), (r.Lvl))
HAVING COUNT(*) > 0
ORDER BY r.Lvl, r.rn
OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY;
GO

-- PIVOT / UNPIVOT
SELECT p.*
FROM (
    SELECT Category, [Year], Amount
    FROM dbo.Sales
) s
PIVOT (SUM(Amount) FOR [Year] IN ([2024], [2025], [2026])) p;
GO

SELECT u.Category, u.[Year], u.Amount
FROM dbo.PivotSales p
UNPIVOT (Amount FOR [Year] IN ([2024], [2025], [2026])) u;
GO
