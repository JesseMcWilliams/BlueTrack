/* ============================================================================
   69_BlueTrack_ProgressRuleIgnoreCase.sql

   RUN THIS AFTER 01-68. Guarded -- safe to re-run.

   D-338 (the user, 2026-11-05: a Progress Rule's searches shouldn't care
   about case): dbo.fn_ProgressConditionMatches (49_) compared a condition's
   value with the account's field in the database's default collation. That
   ignores case on a server installed with the usual SQL_Latin1_General_CP1_CI_AS,
   but a customer's server or database may be case-sensitive. Every text
   comparison now names a case-insensitive collation, so Equals, Contains,
   Starts with and In ignore case wherever BlueTrack is installed. Otherwise
   the function is as in 49_.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER FUNCTION dbo.fn_ProgressConditionMatches(@Text NVARCHAR(4000), @Date DATETIME2, @Operator NVARCHAR(20), @Value NVARCHAR(400))
RETURNS BIT
AS
BEGIN
    DECLARE @t NVARCHAR(4000) = NULLIF(LTRIM(RTRIM(@Text)), N'');
    DECLARE @v NVARCHAR(400) = LTRIM(RTRIM(ISNULL(@Value, N'')));
    DECLARE @days INT = TRY_CAST(@v AS INT);
    DECLARE @inList BIT = CASE WHEN @t IS NOT NULL AND EXISTS (
        SELECT 1 FROM STRING_SPLIT(@v, N',') s WHERE LTRIM(RTRIM(s.value)) COLLATE Latin1_General_CI_AS = @t COLLATE Latin1_General_CI_AS) THEN 1 ELSE 0 END;
    DECLARE @equal BIT = CASE WHEN @t COLLATE Latin1_General_CI_AS = @v COLLATE Latin1_General_CI_AS THEN 1 ELSE 0 END;
    DECLARE @contains BIT = CASE WHEN @t IS NOT NULL AND @v <> N'' AND CHARINDEX(@v COLLATE Latin1_General_CI_AS, @t COLLATE Latin1_General_CI_AS) > 0 THEN 1 ELSE 0 END;
    RETURN CASE @Operator
        WHEN N'Equals'        THEN @equal
        WHEN N'NotEquals'     THEN 1 - @equal
        WHEN N'Contains'      THEN @contains
        WHEN N'NotContains'   THEN 1 - @contains
        WHEN N'StartsWith'    THEN CASE WHEN @t IS NOT NULL AND @v <> N'' AND LEFT(@t, LEN(@v)) COLLATE Latin1_General_CI_AS = @v COLLATE Latin1_General_CI_AS THEN 1 ELSE 0 END
        WHEN N'In'            THEN @inList
        WHEN N'NotIn'         THEN 1 - @inList
        WHEN N'IsEmpty'       THEN CASE WHEN @t IS NULL AND @Date IS NULL THEN 1 ELSE 0 END
        WHEN N'IsNotEmpty'    THEN CASE WHEN @t IS NOT NULL OR @Date IS NOT NULL THEN 1 ELSE 0 END
        WHEN N'WithinDays'    THEN CASE WHEN @days IS NOT NULL AND @Date >= DATEADD(DAY, -@days, CAST(SYSUTCDATETIME() AS DATE)) THEN 1 ELSE 0 END
        WHEN N'OlderThanDays' THEN CASE WHEN @days IS NOT NULL AND @Date < DATEADD(DAY, -@days, CAST(SYSUTCDATETIME() AS DATE)) THEN 1 ELSE 0 END
        ELSE 0
    END;
END
GO

PRINT '69_BlueTrack_ProgressRuleIgnoreCase.sql complete.';
GO
