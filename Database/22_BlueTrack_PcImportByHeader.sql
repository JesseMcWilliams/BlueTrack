/* ============================================================================
   22_BlueTrack_PcImportByHeader.sql

   RUN THIS AFTER 01-21. Guarded -- safe to re-run.

   D-228, decided by the user on 2026-10-14: the Privilege Cloud import reads
   each CSV's columns by their header names.

   Why: the nightly import failed at Users with Msg 7301 ("Cannot obtain the
   required interface ("IID_IColumnsInfo") from OLE DB provider "BULK"").
   That is how BULK INSERT ... FORMAT = 'CSV' reports a file whose column
   count differs from the table it loads into: the Users export went from 8
   columns to 10 (Email gone; Enabled, Suspended and Location added) and
   Groups from 6 to 7 (Directory added). The procedures loaded into
   fixed-column tables, so any layout change failed the whole import.

   1. web.cyberark_import_column     The one list of each Privilege Cloud
                                     file's columns (FileKind, ColumnName,
                                     IsRequired). The import reads it, and so
                                     does the Data Sources page's Test (the
                                     API), so the two can't disagree. Seeded
                                     here; a row already present is kept.
   2. dbo.usp_Import_PC_ReadCsvByHeader
                                     Reads only the file's first line (a
                                     BULK INSERT with LASTROW = 1), splits it
                                     into column names (quotes allowed), and
                                     fails naming the file and every missing
                                     required column. Otherwise it BULK
                                     INSERTs the data rows into a temp table
                                     with as many columns as the header has,
                                     then copies the known columns, by name,
                                     into the caller's #landing. Unknown
                                     columns are ignored; a known column the
                                     file lacks stays NULL. BULK INSERT's
                                     unhelpful 7301 and 7330 (a row with the
                                     wrong number of fields) are re-raised
                                     naming the file. Same permissions
                                     as before (BULK INSERT, as the SQL
                                     Server service account or the caller).
   3. The seven usp_Import_PC_* procedures
                                     Redefined to use it; their staging
                                     conversions are unchanged.
   4. New columns                    stg_pc_users / dim_user: Enabled and
                                     Suspended (BIT; true/yes/y/1 or
                                     false/no/n/0, anything else NULL) and
                                     Location (NVARCHAR(500)).
                                     stg_pc_groups / dim_group: Directory
                                     (NVARCHAR(200)). usp_Load_DimUser and
                                     usp_Load_DimGroup carry them over
                                     (Self-Hosted rows leave them NULL).
   ============================================================================ */

USE $DatabaseName$;
GO

/* ---- 1. The column list ---------------------------------------------------- */
IF OBJECT_ID(N'web.cyberark_import_column', N'U') IS NULL
BEGIN
    CREATE TABLE web.cyberark_import_column (
        FileKind     NVARCHAR(30)   NOT NULL
                     CONSTRAINT CK_cyberark_import_column_FileKind
                     CHECK (FileKind IN (N'Platforms', N'Users', N'Groups', N'GroupMembers', N'Safes', N'Accounts', N'Entitlements')),
        ColumnName   NVARCHAR(128)  NOT NULL,
        IsRequired   BIT            NOT NULL,
        ColumnOrder  INT            NOT NULL,
        CONSTRAINT PK_cyberark_import_column PRIMARY KEY (FileKind, ColumnName)
    );
END
GO

/* Required: the columns staging can't do without (its NOT NULL keys) and the
   ones the Load joins or filters on, so a file without them fails loudly
   instead of loading quietly wrong data. Everything else is optional. */
INSERT INTO web.cyberark_import_column (FileKind, ColumnName, IsRequired, ColumnOrder)
SELECT s.FileKind, s.ColumnName, s.IsRequired, s.ColumnOrder
FROM (VALUES
    (N'Platforms', N'PlatformID', 1, 1),
    (N'Platforms', N'Name', 0, 2),
    (N'Platforms', N'Description', 0, 3),
    (N'Platforms', N'Active', 0, 4),
    (N'Platforms', N'PlatformType', 0, 5),
    (N'Users', N'UserID', 1, 1),
    (N'Users', N'Username', 1, 2),
    (N'Users', N'UserType', 0, 3),
    (N'Users', N'Source', 0, 4),
    (N'Users', N'ComponentUser', 0, 5),
    (N'Users', N'Email', 0, 6),
    (N'Users', N'FirstName', 0, 7),
    (N'Users', N'LastName', 0, 8),
    (N'Users', N'Enabled', 0, 9),
    (N'Users', N'Suspended', 0, 10),
    (N'Users', N'Location', 0, 11),
    (N'Groups', N'GroupID', 1, 1),
    (N'Groups', N'GroupName', 1, 2),
    (N'Groups', N'Description', 0, 3),
    (N'Groups', N'Location', 0, 4),
    (N'Groups', N'GroupType', 0, 5),
    (N'Groups', N'Directory', 0, 6),
    (N'Groups', N'DirectoryType', 0, 7),
    (N'GroupMembers', N'RootGroupName', 1, 1),
    (N'GroupMembers', N'MemberName', 1, 2),
    (N'GroupMembers', N'MemberID', 1, 3),
    (N'GroupMembers', N'MemberType', 1, 4),
    (N'GroupMembers', N'MemberLevel', 0, 5),
    (N'GroupMembers', N'Relationship', 0, 6),
    (N'Safes', N'SafeUrlId', 1, 1),
    (N'Safes', N'SafeName', 1, 2),
    (N'Safes', N'SafeNumber', 1, 3),
    (N'Safes', N'Description', 0, 4),
    (N'Safes', N'Location', 0, 5),
    (N'Safes', N'CreatorId', 0, 6),
    (N'Safes', N'Creator', 0, 7),
    (N'Safes', N'OLACEnabled', 0, 8),
    (N'Safes', N'ManagingCPM', 0, 9),
    (N'Safes', N'VersionRetention', 0, 10),
    (N'Safes', N'DayRetention', 0, 11),
    (N'Safes', N'AutoPurge', 0, 12),
    (N'Safes', N'Created', 0, 13),
    (N'Safes', N'LastModified', 0, 14),
    (N'Safes', N'IsExpiredMember', 0, 15),
    (N'Accounts', N'AccountID', 1, 1),
    (N'Accounts', N'AccountName', 1, 2),
    (N'Accounts', N'Address', 1, 3),
    (N'Accounts', N'UserName', 1, 4),
    (N'Accounts', N'PlatformID', 1, 5),
    (N'Accounts', N'SafeName', 1, 6),
    (N'Accounts', N'SecretType', 0, 7),
    (N'Accounts', N'AutoManaged', 0, 8),
    (N'Accounts', N'CPMStatus', 0, 9),
    (N'Accounts', N'ManualReason', 0, 10),
    (N'Accounts', N'LastCPMModified', 0, 11),
    (N'Accounts', N'LastReconciled', 0, 12),
    (N'Accounts', N'LastVerified', 0, 13),
    (N'Accounts', N'RemoteMachines', 0, 14),
    (N'Accounts', N'RemoteAccessRestricted', 0, 15),
    (N'Accounts', N'CategoryModified', 0, 16),
    (N'Accounts', N'Deleted', 1, 17),
    (N'Accounts', N'Created', 0, 18),
    (N'Accounts', N'Platform_LogonDomain', 0, 19),
    (N'Entitlements', N'SafeUrlId', 1, 1),
    (N'Entitlements', N'SafeName', 1, 2),
    (N'Entitlements', N'SafeNumber', 1, 3),
    (N'Entitlements', N'MemberId', 1, 4),
    (N'Entitlements', N'MemberName', 1, 5),
    (N'Entitlements', N'MemberType', 1, 6),
    (N'Entitlements', N'MembershipExpirationDate', 0, 7),
    (N'Entitlements', N'IsExpiredMembershipEnable', 0, 8),
    (N'Entitlements', N'IsPredefinedUser', 0, 9),
    (N'Entitlements', N'UseAccounts', 0, 10),
    (N'Entitlements', N'RetrieveAccounts', 0, 11),
    (N'Entitlements', N'ListAccounts', 0, 12),
    (N'Entitlements', N'AddAccounts', 0, 13),
    (N'Entitlements', N'UpdateAccountContent', 0, 14),
    (N'Entitlements', N'UpdateAccountProperties', 0, 15),
    (N'Entitlements', N'InitiateCPMAccountManagementOperations', 0, 16),
    (N'Entitlements', N'SpecifyNextAccountContent', 0, 17),
    (N'Entitlements', N'RenameAccounts', 0, 18),
    (N'Entitlements', N'DeleteAccounts', 0, 19),
    (N'Entitlements', N'UnlockAccounts', 0, 20),
    (N'Entitlements', N'ManageSafe', 0, 21),
    (N'Entitlements', N'ManageSafeMembers', 0, 22),
    (N'Entitlements', N'BackupSafe', 0, 23),
    (N'Entitlements', N'ViewAuditLog', 0, 24),
    (N'Entitlements', N'ViewSafeMembers', 0, 25),
    (N'Entitlements', N'AccessWithoutConfirmation', 0, 26),
    (N'Entitlements', N'CreateFolders', 0, 27),
    (N'Entitlements', N'DeleteFolders', 0, 28),
    (N'Entitlements', N'MoveAccountsAndFolders', 0, 29),
    (N'Entitlements', N'RequestsAuthorizationLevel1', 0, 30),
    (N'Entitlements', N'RequestsAuthorizationLevel2', 0, 31)
) AS s (FileKind, ColumnName, IsRequired, ColumnOrder)
WHERE NOT EXISTS (SELECT 1 FROM web.cyberark_import_column c WHERE c.FileKind = s.FileKind AND c.ColumnName = s.ColumnName);
GO

/* ---- 4. New columns (before the procedures that use them) ------------------ */
IF COL_LENGTH(N'dbo.stg_pc_users', N'Enabled') IS NULL
    ALTER TABLE dbo.stg_pc_users ADD Enabled BIT NULL;
IF COL_LENGTH(N'dbo.stg_pc_users', N'Suspended') IS NULL
    ALTER TABLE dbo.stg_pc_users ADD Suspended BIT NULL;
IF COL_LENGTH(N'dbo.stg_pc_users', N'Location') IS NULL
    ALTER TABLE dbo.stg_pc_users ADD Location NVARCHAR(500) NULL;
IF COL_LENGTH(N'dbo.stg_pc_groups', N'Directory') IS NULL
    ALTER TABLE dbo.stg_pc_groups ADD Directory NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.dim_user', N'Enabled') IS NULL
    ALTER TABLE dbo.dim_user ADD Enabled BIT NULL;
IF COL_LENGTH(N'dbo.dim_user', N'Suspended') IS NULL
    ALTER TABLE dbo.dim_user ADD Suspended BIT NULL;
IF COL_LENGTH(N'dbo.dim_user', N'Location') IS NULL
    ALTER TABLE dbo.dim_user ADD Location NVARCHAR(500) NULL;
IF COL_LENGTH(N'dbo.dim_group', N'Directory') IS NULL
    ALTER TABLE dbo.dim_group ADD Directory NVARCHAR(200) NULL;
GO

/* ---- 2. The reader ----------------------------------------------------------
   The caller creates #landing, one NVARCHAR(4000) column per known column of
   @FileKind, named as in web.cyberark_import_column. Header names match
   case-insensitively and ignoring surrounding spaces; a UTF-8 byte order mark
   is dropped. The data rows load as before: FORMAT = 'CSV', quoted fields
   (with embedded commas) allowed, CRLF line ends, UTF-8. */
CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_ReadCsvByHeader (@FilePath NVARCHAR(500), @FileKind NVARCHAR(30))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @FileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX(N'\', REVERSE(@FilePath) + N'\') - 1);
    DECLARE @QuotedPath NVARCHAR(1000) = REPLACE(@FilePath, N'''', N'''''');
    DECLARE @sql NVARCHAR(MAX), @Message NVARCHAR(2000);

    IF NOT EXISTS (SELECT 1 FROM web.cyberark_import_column WHERE FileKind = @FileKind)
    BEGIN
        SET @Message = N'usp_Import_PC_ReadCsvByHeader: no columns are listed for file kind ''' + ISNULL(@FileKind, N'(null)') + N''' in web.cyberark_import_column.';
        THROW 50000, @Message, 1;
    END

    -- The header line only. One NVARCHAR(MAX) column takes the whole line,
    -- whatever it contains; LASTROW = 1 stops there.
    CREATE TABLE #csv_header (Line NVARCHAR(MAX) NULL);
    SET @sql = N'BULK INSERT #csv_header FROM ''' + @QuotedPath + N''' WITH (
        ROWTERMINATOR = ''0x0a'', FIELDTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', LASTROW = 1);';
    EXEC sp_executesql @sql;

    DECLARE @Line NVARCHAR(MAX) = (SELECT TOP (1) Line FROM #csv_header);
    SET @Line = REPLACE(ISNULL(@Line, N''), NCHAR(65279), N'');
    WHILE RIGHT(@Line, 1) IN (NCHAR(13), NCHAR(10)) SET @Line = LEFT(@Line, LEN(@Line + N'x') - 2);

    IF LTRIM(@Line) = N''
    BEGIN
        SET @Message = @FileKind + N' file ''' + @FileName + N''' is empty: it has no header row.';
        THROW 50000, @Message, 1;
    END

    -- Split the header (RFC 4180: "..." quotes, "" is a quote inside them).
    CREATE TABLE #csv_column (Ordinal INT NOT NULL PRIMARY KEY, Name NVARCHAR(4000) COLLATE DATABASE_DEFAULT NOT NULL);
    DECLARE @i INT = 1, @n INT = LEN(@Line + N'x') - 1, @ch NCHAR(1), @InQuotes BIT = 0,
            @Field NVARCHAR(4000) = N'', @Ordinal INT = 0;
    WHILE @i <= @n
    BEGIN
        SET @ch = SUBSTRING(@Line, @i, 1);
        IF @InQuotes = 1
        BEGIN
            IF @ch = N'"'
            BEGIN
                IF SUBSTRING(@Line, @i + 1, 1) = N'"' BEGIN SET @Field += N'"'; SET @i += 1; END
                ELSE SET @InQuotes = 0;
            END
            ELSE SET @Field += @ch;
        END
        ELSE IF @ch = N'"' SET @InQuotes = 1;
        ELSE IF @ch = N','
        BEGIN
            SET @Ordinal += 1;
            INSERT INTO #csv_column (Ordinal, Name) VALUES (@Ordinal, LTRIM(RTRIM(@Field)));
            SET @Field = N'';
        END
        ELSE SET @Field += @ch;
        SET @i += 1;
    END
    SET @Ordinal += 1;
    INSERT INTO #csv_column (Ordinal, Name) VALUES (@Ordinal, LTRIM(RTRIM(@Field)));

    -- Every required column must be there.
    DECLARE @Missing NVARCHAR(MAX) = (
        SELECT STRING_AGG(CAST(ic.ColumnName AS NVARCHAR(MAX)), N', ') WITHIN GROUP (ORDER BY ic.ColumnOrder)
        FROM web.cyberark_import_column ic
        WHERE ic.FileKind = @FileKind AND ic.IsRequired = 1
          AND NOT EXISTS (SELECT 1 FROM #csv_column h WHERE h.Name COLLATE Latin1_General_CI_AS = ic.ColumnName COLLATE Latin1_General_CI_AS));
    IF @Missing IS NOT NULL
    BEGIN
        DECLARE @Found NVARCHAR(MAX) = (SELECT STRING_AGG(CAST(Name AS NVARCHAR(MAX)), N', ') WITHIN GROUP (ORDER BY Ordinal) FROM #csv_column);
        SET @Message = LEFT(@FileKind + N' file ''' + @FileName + N''' is missing the required column(s): ' + @Missing
            + N'. Its header has: ' + @Found, 1990);
        THROW 50000, @Message, 1;
    END

    -- Known columns found in the header: the first occurrence of each name,
    -- and only those the caller's #landing has.
    DECLARE @RawColumns NVARCHAR(MAX), @InsertColumns NVARCHAR(MAX), @SelectColumns NVARCHAR(MAX);
    SELECT @RawColumns = STRING_AGG(CAST(N'c' + CAST(Ordinal AS NVARCHAR(10)) + N' NVARCHAR(4000) NULL' AS NVARCHAR(MAX)), N', ')
                         WITHIN GROUP (ORDER BY Ordinal)
    FROM #csv_column;

    SELECT @InsertColumns = STRING_AGG(CAST(QUOTENAME(m.ColumnName) AS NVARCHAR(MAX)), N', ') WITHIN GROUP (ORDER BY m.Ordinal),
           @SelectColumns = STRING_AGG(CAST(N'c' + CAST(m.Ordinal AS NVARCHAR(10)) AS NVARCHAR(MAX)), N', ') WITHIN GROUP (ORDER BY m.Ordinal)
    FROM (
        SELECT ic.ColumnName, MIN(h.Ordinal) AS Ordinal
        FROM web.cyberark_import_column ic
        JOIN #csv_column h ON h.Name COLLATE Latin1_General_CI_AS = ic.ColumnName COLLATE Latin1_General_CI_AS
        WHERE ic.FileKind = @FileKind
          AND EXISTS (SELECT 1 FROM tempdb.sys.columns lc
                      WHERE lc.object_id = OBJECT_ID(N'tempdb..#landing')
                        AND lc.name COLLATE Latin1_General_CI_AS = ic.ColumnName COLLATE Latin1_General_CI_AS)
        GROUP BY ic.ColumnName
    ) AS m;

    -- One batch, so #csv_raw lives until the copy is done.
    SET @sql = N'CREATE TABLE #csv_raw (' + @RawColumns + N');
        BULK INSERT #csv_raw FROM ''' + @QuotedPath + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0d0a'', CODEPAGE = ''65001'', TABLOCK);
        INSERT INTO #landing (' + @InsertColumns + N') SELECT ' + @SelectColumns + N' FROM #csv_raw;';
    BEGIN TRY
        EXEC sp_executesql @sql;
    END TRY
    BEGIN CATCH
        -- BULK INSERT's own messages don't say what is wrong. 7301: the header
        -- row has a different column count than the table (what failed before
        -- D-228; now only a header SQL Server splits differently from the
        -- parser above). 7330: a data row has a different field count.
        IF ERROR_NUMBER() IN (7301, 7330)
        BEGIN
            SET @Message = LEFT(@FileKind + N' file ''' + @FileName + N''' could not be read: '
                + CASE ERROR_NUMBER()
                    WHEN 7301 THEN N'SQL Server split its header row into a different number of columns than the '
                        + CAST(@Ordinal AS NVARCHAR(10)) + N' found here. Quote each column name, or none, with no spaces around the quotes.'
                    ELSE N'a data row has more or fewer fields than the header''s ' + CAST(@Ordinal AS NVARCHAR(10))
                        + N', or a quote is not closed.'
                  END
                + N' (SQL Server error ' + CAST(ERROR_NUMBER() AS NVARCHAR(10)) + N': ' + ERROR_MESSAGE() + N')', 1990);
            THROW 50000, @Message, 1;
        END;
        THROW;
    END CATCH
END
GO

/* ---- 3. The seven file imports ---------------------------------------------
   As in 03_BlueTrack_Baseline_SourceImport.sql, but #landing is filled by
   usp_Import_PC_ReadCsvByHeader. Users gains Enabled, Suspended, Location;
   Groups gains Directory. */

CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_Platforms (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [PlatformID] NVARCHAR(4000) NULL,
            [Name] NVARCHAR(4000) NULL,
            [Description] NVARCHAR(4000) NULL,
            [Active] NVARCHAR(4000) NULL,
            [PlatformType] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'Platforms';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_platforms;

        INSERT INTO stg_pc_platforms (ImportBatchId, SourceFileName, LoadTimestamp, [PlatformID], [Name], [Description], [Active], [PlatformType])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
            NULLIF(LEFT(LTRIM(RTRIM([PlatformID])), 100), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Name])), 200), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Description])), 1000), ''),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([Active])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([PlatformType])), 50), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_Users (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [UserID] NVARCHAR(4000) NULL,
            [Username] NVARCHAR(4000) NULL,
            [UserType] NVARCHAR(4000) NULL,
            [Source] NVARCHAR(4000) NULL,
            [ComponentUser] NVARCHAR(4000) NULL,
            [Email] NVARCHAR(4000) NULL,
            [FirstName] NVARCHAR(4000) NULL,
            [LastName] NVARCHAR(4000) NULL,
            [Enabled] NVARCHAR(4000) NULL,
            [Suspended] NVARCHAR(4000) NULL,
            [Location] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'Users';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_users;

        INSERT INTO stg_pc_users (ImportBatchId, SourceFileName, LoadTimestamp, [UserID], [Username], [UserType], [Source], [ComponentUser], [Email], [FirstName], [LastName], [Enabled], [Suspended], [Location])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
            NULLIF(LEFT(LTRIM(RTRIM([UserID])), 50), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Username])), 200), ''),
            NULLIF(LEFT(LTRIM(RTRIM([UserType])), 100), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Source])), 100), ''),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ComponentUser])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([Email])), 320), ''),
            NULLIF(LEFT(LTRIM(RTRIM([FirstName])), 200), ''),
            NULLIF(LEFT(LTRIM(RTRIM([LastName])), 200), ''),
            CASE UPPER(LTRIM(RTRIM([Enabled]))) WHEN 'TRUE' THEN 1 WHEN 'YES' THEN 1 WHEN 'Y' THEN 1 WHEN '1' THEN 1
                                       WHEN 'FALSE' THEN 0 WHEN 'NO' THEN 0 WHEN 'N' THEN 0 WHEN '0' THEN 0 END,
            CASE UPPER(LTRIM(RTRIM([Suspended]))) WHEN 'TRUE' THEN 1 WHEN 'YES' THEN 1 WHEN 'Y' THEN 1 WHEN '1' THEN 1
                                       WHEN 'FALSE' THEN 0 WHEN 'NO' THEN 0 WHEN 'N' THEN 0 WHEN '0' THEN 0 END,
            NULLIF(LEFT(LTRIM(RTRIM([Location])), 500), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_Groups (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [GroupID] NVARCHAR(4000) NULL,
            [GroupName] NVARCHAR(4000) NULL,
            [Description] NVARCHAR(4000) NULL,
            [Location] NVARCHAR(4000) NULL,
            [GroupType] NVARCHAR(4000) NULL,
            [Directory] NVARCHAR(4000) NULL,
            [DirectoryType] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'Groups';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_groups;

        INSERT INTO stg_pc_groups (ImportBatchId, SourceFileName, LoadTimestamp, [GroupID], [GroupName], [Description], [Location], [GroupType], [Directory], [DirectoryType])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
            NULLIF(LEFT(LTRIM(RTRIM([GroupID])), 50), ''),
            NULLIF(LEFT(LTRIM(RTRIM([GroupName])), 200), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Description])), 1000), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Location])), 500), ''),
            NULLIF(LEFT(LTRIM(RTRIM([GroupType])), 50), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Directory])), 200), ''),
            NULLIF(LEFT(LTRIM(RTRIM([DirectoryType])), 100), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_GroupMembers (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [RootGroupName] NVARCHAR(4000) NULL,
            [MemberName] NVARCHAR(4000) NULL,
            [MemberID] NVARCHAR(4000) NULL,
            [MemberType] NVARCHAR(4000) NULL,
            [MemberLevel] NVARCHAR(4000) NULL,
            [Relationship] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'GroupMembers';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_groupmembers;

        INSERT INTO stg_pc_groupmembers (ImportBatchId, SourceFileName, LoadTimestamp, [RootGroupName], [MemberName], [MemberID], [MemberType], [MemberLevel], [Relationship])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
            NULLIF(LEFT(LTRIM(RTRIM([RootGroupName])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([MemberName])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([MemberID])), 50), ''),
            NULLIF(LEFT(LTRIM(RTRIM([MemberType])), 20), ''),
            NULLIF(LEFT(LTRIM(RTRIM([MemberLevel])), 20), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Relationship])), 300), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_Safes (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [SafeUrlId] NVARCHAR(4000) NULL,
            [SafeName] NVARCHAR(4000) NULL,
            [SafeNumber] NVARCHAR(4000) NULL,
            [Description] NVARCHAR(4000) NULL,
            [Location] NVARCHAR(4000) NULL,
            [CreatorId] NVARCHAR(4000) NULL,
            [Creator] NVARCHAR(4000) NULL,
            [OLACEnabled] NVARCHAR(4000) NULL,
            [ManagingCPM] NVARCHAR(4000) NULL,
            [VersionRetention] NVARCHAR(4000) NULL,
            [DayRetention] NVARCHAR(4000) NULL,
            [AutoPurge] NVARCHAR(4000) NULL,
            [Created] NVARCHAR(4000) NULL,
            [LastModified] NVARCHAR(4000) NULL,
            [IsExpiredMember] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'Safes';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_safes;

        INSERT INTO stg_pc_safes (ImportBatchId, SourceFileName, LoadTimestamp, [SafeUrlId], [SafeName], [SafeNumber], [Description], [Location], [CreatorId], [Creator], [OLACEnabled], [ManagingCPM], [VersionRetention], [DayRetention], [AutoPurge], [Created], [LastModified], [IsExpiredMember])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
            NULLIF(LEFT(LTRIM(RTRIM([SafeUrlId])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([SafeName])), 300), ''),
            TRY_CONVERT(INT, NULLIF(LTRIM(RTRIM([SafeNumber])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([Description])), 1000), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Location])), 500), ''),
            NULLIF(LEFT(LTRIM(RTRIM([CreatorId])), 100), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Creator])), 300), ''),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([OLACEnabled])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([ManagingCPM])), 200), ''),
            TRY_CONVERT(INT, NULLIF(LTRIM(RTRIM([VersionRetention])), '')),
            TRY_CONVERT(INT, NULLIF(LTRIM(RTRIM([DayRetention])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([AutoPurge])), '')),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([Created])), '')),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([LastModified])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([IsExpiredMember])), ''))
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_Accounts (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [AccountID] NVARCHAR(4000) NULL,
            [AccountName] NVARCHAR(4000) NULL,
            [Address] NVARCHAR(4000) NULL,
            [UserName] NVARCHAR(4000) NULL,
            [PlatformID] NVARCHAR(4000) NULL,
            [SafeName] NVARCHAR(4000) NULL,
            [SecretType] NVARCHAR(4000) NULL,
            [AutoManaged] NVARCHAR(4000) NULL,
            [CPMStatus] NVARCHAR(4000) NULL,
            [ManualReason] NVARCHAR(4000) NULL,
            [LastCPMModified] NVARCHAR(4000) NULL,
            [LastReconciled] NVARCHAR(4000) NULL,
            [LastVerified] NVARCHAR(4000) NULL,
            [RemoteMachines] NVARCHAR(4000) NULL,
            [RemoteAccessRestricted] NVARCHAR(4000) NULL,
            [CategoryModified] NVARCHAR(4000) NULL,
            [Deleted] NVARCHAR(4000) NULL,
            [Created] NVARCHAR(4000) NULL,
            [Platform_LogonDomain] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'Accounts';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_accounts;

        INSERT INTO stg_pc_accounts (ImportBatchId, SourceFileName, LoadTimestamp, [AccountID], [AccountName], [Address], [UserName], [PlatformID], [SafeName], [SecretType], [AutoManaged], [CPMStatus], [ManualReason], [LastCPMModified], [LastReconciled], [LastVerified], [RemoteMachines], [RemoteAccessRestricted], [CategoryModified], [Deleted], [Created], [Platform_LogonDomain])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
            NULLIF(LEFT(LTRIM(RTRIM([AccountID])), 50), ''),
            NULLIF(LEFT(LTRIM(RTRIM([AccountName])), 500), ''),
            NULLIF(LEFT(LTRIM(RTRIM([Address])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([UserName])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([PlatformID])), 100), ''),
            NULLIF(LEFT(LTRIM(RTRIM([SafeName])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([SecretType])), 50), ''),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([AutoManaged])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([CPMStatus])), 50), ''),
            NULLIF(LEFT(LTRIM(RTRIM([ManualReason])), 500), ''),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([LastCPMModified])), '')),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([LastReconciled])), '')),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([LastVerified])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([RemoteMachines])), 1000), ''),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([RemoteAccessRestricted])), '')),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([CategoryModified])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([Deleted])), '')),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([Created])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([Platform_LogonDomain])), 200), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE dbo.usp_Import_PC_Entitlements (@FilePath NVARCHAR(500), @ExportDate DATE)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @RowsLoaded INT;

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @PrivCloudKey, @SourceFileName, 'Started');

    BEGIN TRY
        IF OBJECT_ID('tempdb..#landing') IS NOT NULL DROP TABLE #landing;
        CREATE TABLE #landing (
            [SafeUrlId] NVARCHAR(4000) NULL,
            [SafeName] NVARCHAR(4000) NULL,
            [SafeNumber] NVARCHAR(4000) NULL,
            [MemberId] NVARCHAR(4000) NULL,
            [MemberName] NVARCHAR(4000) NULL,
            [MemberType] NVARCHAR(4000) NULL,
            [MembershipExpirationDate] NVARCHAR(4000) NULL,
            [IsExpiredMembershipEnable] NVARCHAR(4000) NULL,
            [IsPredefinedUser] NVARCHAR(4000) NULL,
            [UseAccounts] NVARCHAR(4000) NULL,
            [RetrieveAccounts] NVARCHAR(4000) NULL,
            [ListAccounts] NVARCHAR(4000) NULL,
            [AddAccounts] NVARCHAR(4000) NULL,
            [UpdateAccountContent] NVARCHAR(4000) NULL,
            [UpdateAccountProperties] NVARCHAR(4000) NULL,
            [InitiateCPMAccountManagementOperations] NVARCHAR(4000) NULL,
            [SpecifyNextAccountContent] NVARCHAR(4000) NULL,
            [RenameAccounts] NVARCHAR(4000) NULL,
            [DeleteAccounts] NVARCHAR(4000) NULL,
            [UnlockAccounts] NVARCHAR(4000) NULL,
            [ManageSafe] NVARCHAR(4000) NULL,
            [ManageSafeMembers] NVARCHAR(4000) NULL,
            [BackupSafe] NVARCHAR(4000) NULL,
            [ViewAuditLog] NVARCHAR(4000) NULL,
            [ViewSafeMembers] NVARCHAR(4000) NULL,
            [AccessWithoutConfirmation] NVARCHAR(4000) NULL,
            [CreateFolders] NVARCHAR(4000) NULL,
            [DeleteFolders] NVARCHAR(4000) NULL,
            [MoveAccountsAndFolders] NVARCHAR(4000) NULL,
            [RequestsAuthorizationLevel1] NVARCHAR(4000) NULL,
            [RequestsAuthorizationLevel2] NVARCHAR(4000) NULL
        );

        -- D-228: fills #landing by header name; a column the file lacks stays NULL.
        EXEC dbo.usp_Import_PC_ReadCsvByHeader @FilePath = @FilePath, @FileKind = N'Entitlements';

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_entitlements;

        INSERT INTO stg_pc_entitlements (ImportBatchId, SourceFileName, LoadTimestamp, ExportDate, [SafeUrlId], [SafeName], [SafeNumber], [MemberId], [MemberName], [MemberType], [MembershipExpirationDate], [IsExpiredMembershipEnable], [IsPredefinedUser], [UseAccounts], [RetrieveAccounts], [ListAccounts], [AddAccounts], [UpdateAccountContent], [UpdateAccountProperties], [InitiateCPMAccountManagementOperations], [SpecifyNextAccountContent], [RenameAccounts], [DeleteAccounts], [UnlockAccounts], [ManageSafe], [ManageSafeMembers], [BackupSafe], [ViewAuditLog], [ViewSafeMembers], [AccessWithoutConfirmation], [CreateFolders], [DeleteFolders], [MoveAccountsAndFolders], [RequestsAuthorizationLevel1], [RequestsAuthorizationLevel2])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(), @ExportDate,
            NULLIF(LEFT(LTRIM(RTRIM([SafeUrlId])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([SafeName])), 300), ''),
            TRY_CONVERT(INT, NULLIF(LTRIM(RTRIM([SafeNumber])), '')),
            NULLIF(LEFT(LTRIM(RTRIM([MemberId])), 100), ''),
            NULLIF(LEFT(LTRIM(RTRIM([MemberName])), 300), ''),
            NULLIF(LEFT(LTRIM(RTRIM([MemberType])), 20), ''),
            TRY_CONVERT(DATE, NULLIF(LTRIM(RTRIM([MembershipExpirationDate])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([IsExpiredMembershipEnable])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([IsPredefinedUser])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([UseAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([RetrieveAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ListAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([AddAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([UpdateAccountContent])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([UpdateAccountProperties])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([InitiateCPMAccountManagementOperations])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([SpecifyNextAccountContent])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([RenameAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([DeleteAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([UnlockAccounts])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ManageSafe])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ManageSafeMembers])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([BackupSafe])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ViewAuditLog])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ViewSafeMembers])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([AccessWithoutConfirmation])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([CreateFolders])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([DeleteFolders])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([MoveAccountsAndFolders])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([RequestsAuthorizationLevel1])), '')),
            TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([RequestsAuthorizationLevel2])), ''))
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 2000)
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


/* ---- 4. The dimension loads ------------------------------------------------
   As in 02_BlueTrack_Baseline_EtlLoads.sql, plus the new columns for
   Privilege Cloud. The Self-Hosted halves are unchanged and leave them NULL. */
CREATE OR ALTER PROCEDURE dbo.usp_Load_DimUser
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- Privilege Cloud
    MERGE dim_user AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, UserID AS SourceUserId, Username, UserType,
               Source AS UserSource, ComponentUser, Email, FirstName, LastName,
               Enabled, Suspended, Location,
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_users
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceUserId = src.SourceUserId
    WHEN MATCHED THEN
        UPDATE SET Username = src.Username, UserType = src.UserType, UserSource = src.UserSource,
                   ComponentUser = src.ComponentUser, Email = src.Email, FirstName = src.FirstName, LastName = src.LastName,
                   Enabled = src.Enabled, Suspended = src.Suspended, Location = src.Location
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceUserId, Username, UserType, UserSource, ComponentUser, Email, FirstName, LastName,
                Enabled, Suspended, Location)
        VALUES (src.SourceSystemKey, src.SourceUserId, src.Username, src.UserType, src.UserSource,
                src.ComponentUser, src.Email, src.FirstName, src.LastName,
                src.Enabled, src.Suspended, src.Location);

    -- Self-Hosted
    MERGE dim_user AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, CAST(u.CAUUserID AS NVARCHAR(100)) AS SourceUserId,
               u.CAUUserName AS Username, CAST(u.CAUUserTypeID AS NVARCHAR(100)) AS UserType,
               CASE WHEN u.CAUExternalInternal = 1 THEN 'Internal'
                    WHEN u.CAUExternalInternal = 2 THEN 'External'
                    ELSE NULL END AS UserSource,          -- decoded via CATextCodes type 4
               CAST(NULL AS BIT) AS ComponentUser,
               u.CAUBusinessEmail AS Email, u.CAUFirstName AS FirstName, u.CAULastName AS LastName,
               loc.LocationKey
        FROM stg_sh_users u
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey
                                   AND loc.SourceLocationId = CAST(u.CAULocationID AS NVARCHAR(50))
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceUserId = src.SourceUserId
    WHEN MATCHED THEN
        UPDATE SET Username = src.Username, UserType = src.UserType, UserSource = src.UserSource,
                   Email = src.Email, FirstName = src.FirstName, LastName = src.LastName, LocationKey = src.LocationKey
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceUserId, Username, UserType, UserSource, ComponentUser, Email, FirstName, LastName, LocationKey)
        VALUES (src.SourceSystemKey, src.SourceUserId, src.Username, src.UserType, src.UserSource,
                src.ComponentUser, src.Email, src.FirstName, src.LastName, src.LocationKey);
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_Load_DimGroup
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- Privilege Cloud
    MERGE dim_group AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, GroupID AS SourceGroupId, GroupName, Description,
               Location, GroupType, Directory, CAST(DirectoryType AS NVARCHAR(100)) AS DirectoryType,
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_groups
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceGroupId = src.SourceGroupId
    WHEN MATCHED THEN
        UPDATE SET GroupName = src.GroupName, Description = src.Description, Location = src.Location,
                   GroupType = src.GroupType, Directory = src.Directory, DirectoryType = src.DirectoryType
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceGroupId, GroupName, Description, Location, GroupType, Directory, DirectoryType)
        VALUES (src.SourceSystemKey, src.SourceGroupId, src.GroupName, src.Description, src.Location, src.GroupType,
                src.Directory, src.DirectoryType);

    -- Self-Hosted
    MERGE dim_group AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, CAST(g.CAGGroupID AS NVARCHAR(100)) AS SourceGroupId,
               g.CAGGroupName AS GroupName, g.CAGDescription AS Description, g.CAGLocationName AS Location,
               CASE WHEN g.CAGExternalInternal = 1 THEN 'Internal'
                    WHEN g.CAGExternalInternal = 2 THEN 'External'
                    ELSE NULL END AS GroupType,
               g.CAGLDAPDirectory AS DirectoryType,
               loc.LocationKey
        FROM stg_sh_groups g
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey
                                   AND loc.SourceLocationId = CAST(g.CAGLocationID AS NVARCHAR(50))
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceGroupId = src.SourceGroupId
    WHEN MATCHED THEN
        UPDATE SET GroupName = src.GroupName, Description = src.Description, Location = src.Location,
                   GroupType = src.GroupType, DirectoryType = src.DirectoryType, LocationKey = src.LocationKey
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceGroupId, GroupName, Description, Location, GroupType, DirectoryType, LocationKey)
        VALUES (src.SourceSystemKey, src.SourceGroupId, src.GroupName, src.Description, src.Location,
                src.GroupType, src.DirectoryType, src.LocationKey);
END
GO

PRINT '22_BlueTrack_PcImportByHeader.sql complete.';
GO
