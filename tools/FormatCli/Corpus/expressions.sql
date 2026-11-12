SELECT
    IIF(a > 0, 'pos', 'neg') AS s1,
    CHOOSE(2, 'a', 'b', 'c') AS s2,
    CAST(a AS DECIMAL(18, 2)) AS d1,
    CONVERT(NVARCHAR(20), b, 120) AS d2,
    COALESCE(a, b, c) AS d3,
    NULLIF(a, 0) AS d4,
    JSON_VALUE(payload, '$.name') AS j1,
    jt.id,
    jt.name
FROM dbo.T
CROSS APPLY OPENJSON(payload) WITH (id INT '$.id', name NVARCHAR(100) '$.name') jt
CROSS APPLY (SELECT TOP (1) x FROM dbo.X WHERE X.Id = T.Id ORDER BY X.Seq DESC) x
OUTER APPLY dbo.fn_Get(T.Id) f
WHERE EXISTS (SELECT 1 FROM dbo.Y WHERE Y.Id = T.Id)
  AND T.Id IN (SELECT Id FROM dbo.Z)
  AND T.Amount BETWEEN 1 AND 100
  AND T.Name LIKE N'%abc%' ESCAPE N'\'
  AND T.Status IN (N'A', N'B', N'C')
OPTION (RECOMPILE, MAXDOP 4);
GO
