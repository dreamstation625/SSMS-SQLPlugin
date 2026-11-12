CREATE PROCEDURE dbo.usp_Process
    @BatchId INT,
    @Mode NVARCHAR(20) = N'FULL'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 临时表
    CREATE TABLE #Work (Id INT PRIMARY KEY, Val NVARCHAR(100));

    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO #Work (Id, Val)
        SELECT Id, Val FROM dbo.Source WHERE BatchId = @BatchId;

        DECLARE @i INT = 0;
        WHILE @i < 10
        BEGIN
            SET @i = @i + 1;
            IF @i % 2 = 0
            BEGIN
                UPDATE #Work SET Val = Val + N'_even' WHERE Id = @i;
            END
            ELSE
            BEGIN
                -- 奇数
                UPDATE #Work SET Val = Val + N'_odd' WHERE Id = @i;
            END
        END

        DECLARE @sql NVARCHAR(MAX) = N'SELECT * FROM #Work'; -- 动态 SQL
        EXEC sp_executesql @sql;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        DECLARE @msg NVARCHAR(2048) = ERROR_MESSAGE(); -- 错误信息
        THROW 50000, @msg, 1;
    END CATCH

    -- 游标
    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT Id FROM #Work;
    OPEN cur;
    FETCH NEXT FROM cur INTO @i;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        FETCH NEXT FROM cur INTO @i;
    END
    CLOSE cur;
    DEALLOCATE cur;

    DROP TABLE #Work;
END
GO
