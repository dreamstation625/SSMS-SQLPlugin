MERGE INTO dbo.Target AS t
USING (
    SELECT Id, Val FROM dbo.Source -- 源
) AS s
ON t.Id = s.Id -- 匹配
WHEN MATCHED AND s.Val <> t.Val THEN
    UPDATE SET t.Val = s.Val -- 更新
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Id, Val) VALUES (s.Id, s.Val) -- 插入
WHEN NOT MATCHED BY SOURCE THEN
    DELETE -- 删除
OUTPUT $action, inserted.Id, deleted.Id INTO dbo.Audit (Action, NewId, OldId);
GO

INSERT INTO dbo.T (A, B)
OUTPUT inserted.Id, inserted.A INTO @ids (Id, A)
SELECT A, B FROM dbo.S WHERE Flag = 1;
GO

UPDATE tgt
SET tgt.Val = src.Val,
    tgt.ModifiedAt = SYSUTCDATETIME()
FROM dbo.Target tgt
INNER JOIN dbo.Source src ON src.Id = tgt.Id
WHERE src.Flag = 1;
GO

DELETE tgt
FROM dbo.Target tgt
INNER JOIN dbo.Source src ON src.Id = tgt.Id
WHERE src.Flag = 0;
GO
