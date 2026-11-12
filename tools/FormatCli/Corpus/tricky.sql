SELECT a>=1, b<>2, c!=3, d<=4, e+=5 FROM t; -- 复合运算符
GO
SELECT ((((a + b)))) AS x, (a + (b * (c - d))) AS y FROM t; -- 嵌套括号
GO
SELECT a -- 注释在运算符前
     + b AS s
FROM t;
GO
SELECT 1/0;
GO
BEGIN
    SELECT 1;
-- 缺 END
