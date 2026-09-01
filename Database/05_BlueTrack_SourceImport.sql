/* ============================================================================
   05_BlueTrack_SourceImport.sql

   RUN THIS FILE FIFTH, after 01-04 have all been run once to build the
   database, tables, and procedures. Unlike files 01-04 (one-time/idempotent
   setup), this file is OPERATIONAL -- run it every time you have a fresh
   set of source exports to load, followed by EXEC usp_RunFullLoad; (from
   file 04) to transform the freshly-loaded staging data into the
   dimension/fact/tracking tables.

   Answers "where does data actually get INTO the stg_pc_* / stg_sh_* staging
   tables in the first place?" -- files 02-04 all assume staging is already
   populated; this file is what populates it.

   Includes a USE BlueTrack; statement below, so you don't need to
   set the database context manually before running this.

   Contents:
     1. usp_Import_PC_Platforms, usp_Import_PC_Users, usp_Import_PC_Groups,
        usp_Import_PC_Safes, usp_Import_PC_Accounts, usp_Import_PC_Entitlements
        -- one per Privilege Cloud CSV export, via BULK INSERT
     2. usp_Import_SelfHosted_EVD -- same-instance cross-database copy from
        the live EVD database's tables into the stg_sh_* staging tables
     3. usp_Import_PrivilegeCloud_All, usp_Import_All -- orchestrators

   *** ENVIRONMENT ASSUMPTIONS BAKED INTO THIS FILE -- based on what you
   confirmed (EVD on the same instance; CSVs land in a folder the SQL
   Server engine itself can read). If either of those changes, the
   corresponding piece below needs to change with it: ***
     - usp_Import_SelfHosted_EVD uses a same-instance cross-database query
       (dynamic SQL with QUOTENAME(@EVDDatabaseName)). If EVD ever moves to
       a different server, this would need to become a Linked Server query
       instead -- a different setup (sp_addlinkedserver, four-part names)
       not covered here.
     - usp_Import_PC_* procs use BULK INSERT, which requires the SQL Server
       *service account* (not your own login) to have read access to the
       file path given -- typically a UNC path or a local path on the
       server itself. If CSVs instead need to be picked up from wherever a
       client/user is sitting, BULK INSERT is the wrong tool and you'd want
       a client-side loader (PowerShell/Python/SSIS) instead -- not covered
       here.
     - The CSV BULK INSERT options (FORMAT = 'CSV', FIELDQUOTE) require SQL
       Server 2017+ or Azure SQL Database. Confirm your instance version
       before relying on this.
     - Date parsing in the CSV-to-staging conversion uses TRY_CONVERT(DATE, ...)
       against whatever string is in each cell. I don't know what locale/
       format your actual exports use (e.g. MM/DD/YYYY vs YYYY-MM-DD) --
       TRY_CONVERT returns NULL rather than erroring on an unparseable
       value, so a systematic format mismatch will silently null out an
       entire date column rather than fail loudly. Check row counts and
       spot-check a few date values after the first real run.

   *** BATCHING NOTE ***
   Every CREATE OR ALTER PROCEDURE below must be the only statement in its
   batch -- each is followed by GO.

   *** FAIL-FAST BEHAVIOR ***
   Each individual import proc wraps its own work in TRY/CATCH, logs to
   import_log either way, and re-throws (THROW) on failure. The
   orchestrators below do not swallow that -- if one file fails, the
   orchestrator stops there rather than silently continuing to the next
   file. Whatever ran before the failure is already committed (each proc
   does its own TRUNCATE + INSERT as its own unit of work, not wrapped in a
   shared explicit transaction with the others) and logged in import_log,
   so a failure partway through does not roll back earlier successful
   imports in the same run.
   ============================================================================ */

USE BlueTrack;
GO

CREATE OR ALTER PROCEDURE usp_Import_PC_Platforms (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

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
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE usp_Import_PC_Users (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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
    [LastName] NVARCHAR(4000) NULL
        );

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_users;

        INSERT INTO stg_pc_users (ImportBatchId, SourceFileName, LoadTimestamp, [UserID], [Username], [UserType], [Source], [ComponentUser], [Email], [FirstName], [LastName])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
        NULLIF(LEFT(LTRIM(RTRIM([UserID])), 50), ''),
        NULLIF(LEFT(LTRIM(RTRIM([Username])), 200), ''),
        NULLIF(LEFT(LTRIM(RTRIM([UserType])), 100), ''),
        NULLIF(LEFT(LTRIM(RTRIM([Source])), 100), ''),
        TRY_CONVERT(BIT, NULLIF(LTRIM(RTRIM([ComponentUser])), '')),
        NULLIF(LEFT(LTRIM(RTRIM([Email])), 320), ''),
        NULLIF(LEFT(LTRIM(RTRIM([FirstName])), 200), ''),
        NULLIF(LEFT(LTRIM(RTRIM([LastName])), 200), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE usp_Import_PC_Groups (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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
    [DirectoryType] NVARCHAR(4000) NULL
        );

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

        -- full-snapshot replace, consistent with the rest of the ETL's
        -- "staging holds one clean current snapshot" design assumption
        TRUNCATE TABLE stg_pc_groups;

        INSERT INTO stg_pc_groups (ImportBatchId, SourceFileName, LoadTimestamp, [GroupID], [GroupName], [Description], [Location], [GroupType], [DirectoryType])
        SELECT
            @ImportBatchId, @SourceFileName, SYSUTCDATETIME(),
        NULLIF(LEFT(LTRIM(RTRIM([GroupID])), 50), ''),
        NULLIF(LEFT(LTRIM(RTRIM([GroupName])), 200), ''),
        NULLIF(LEFT(LTRIM(RTRIM([Description])), 1000), ''),
        NULLIF(LEFT(LTRIM(RTRIM([Location])), 500), ''),
        NULLIF(LEFT(LTRIM(RTRIM([GroupType])), 50), ''),
        NULLIF(LEFT(LTRIM(RTRIM([DirectoryType])), 100), '')
        FROM #landing;

        SET @RowsLoaded = @@ROWCOUNT;
        DROP TABLE #landing;

        UPDATE import_log SET RowsLoaded = @RowsLoaded, CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE usp_Import_PC_Safes (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

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
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE usp_Import_PC_Accounts (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

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
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


CREATE OR ALTER PROCEDURE usp_Import_PC_Entitlements (@FilePath NVARCHAR(500), @ExportDate DATE)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

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
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO

CREATE OR ALTER PROCEDURE usp_Import_PC_GroupMembers (@FilePath NVARCHAR(500))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @SourceFileName NVARCHAR(260) = RIGHT(@FilePath, CHARINDEX('\', REVERSE(@FilePath) + '\') - 1);
    DECLARE @sql NVARCHAR(MAX);
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

        -- BULK INSERT requires a literal file path, not a variable, so this
        -- goes through dynamic SQL. Requires SQL Server 2017+ or Azure SQL
        -- Database for FORMAT = 'CSV' with FIELDQUOTE support (handles
        -- quoted fields containing embedded commas correctly); on older
        -- versions you'd need a format file instead, which isn't covered
        -- here. Also assumes the SQL Server service account has read access
        -- to the file path given -- a UNC path or a local path on the
        -- server itself, not a path only your client machine can see.
        SET @sql = N'BULK INSERT #landing FROM ''' + REPLACE(@FilePath, '''', '''''') + N''' WITH (
            FORMAT = ''CSV'', FIRSTROW = 2, FIELDQUOTE = ''"'', FIELDTERMINATOR = '','',
            ROWTERMINATOR = ''0x0a'', CODEPAGE = ''65001'', TABLOCK
        );';
        EXEC sp_executesql @sql;

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
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO


/* ============================================================================
   Self-Hosted EVD import -- same-instance cross-database copy.

   Reads directly from the live EVD database's tables into the stg_sh_*
   staging tables. Because this reads real SQL Server columns (not parsed
   CSV text), no text-to-type conversion is needed here the way the
   Privilege Cloud CSV import procs need -- types come across as-is.

   *** PERMISSIONS NOTE *** Cross-database access isn't automatic just
   because both databases sit on the same instance. Whatever login/user
   executes this procedure needs SELECT permission on the EVD database's
   tables specifically (or db_datareader membership there) -- being able to
   query BlueTrack does not imply access to the EVD database. Cross-
   database ownership chaining does not apply here since these are two
   separately-owned databases, not the same schema/owner. Grant that access
   before running this, or it will fail with a permissions error naming the
   EVD database.

   Dynamic SQL is required because the database name (@EVDDatabaseName)
   can't be substituted into a static three-part object name -- QUOTENAME
   is used when building it to guard against a malformed/malicious database
   name value.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Import_SelfHosted_EVD (@EVDDatabaseName NVARCHAR(128))
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @ImportBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @sql NVARCHAR(MAX);
    DECLARE @RowsUsers INT = 0;
    DECLARE @RowsGroups INT = 0;
    DECLARE @RowsGroupMembers INT = 0;
    DECLARE @RowsSafes INT = 0;
    DECLARE @RowsOwners INT = 0;
    DECLARE @RowsFiles INT = 0;
    DECLARE @RowsObjectProperties INT = 0;
    DECLARE @RowsRequests INT = 0;
    DECLARE @RowsConfirmations INT = 0;

    IF DB_ID(@EVDDatabaseName) IS NULL
    BEGIN
        RAISERROR('EVD database "%s" was not found on this instance. Confirm the database name and that it is on the same instance as BlueTrack.', 16, 1, @EVDDatabaseName);
        RETURN;
    END

    INSERT INTO import_log (ImportBatchId, SourceSystemKey, SourceFileName, Status)
    VALUES (@ImportBatchId, @SelfHostedKey, @EVDDatabaseName, 'Started');

    BEGIN TRY

        -- CAUsers -> stg_sh_users
        TRUNCATE TABLE stg_sh_users;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAUsers (EVD)'' AS SourceFileName, CAUUserID, CAUUserName, CAULocationID, CAULocationName, CAUFirstName, CAULastName, CAUBusinessEmail, CAUDisabled, CAUExpirationDate, CAUPasswordNeverExpires, CAUAuthenticationMethods, CAUAuthorizations, CAUGatewayAccountAuthorizations, CAUDistinguishedName, CAUExternalInternal, CAULDAPFullDN, CAULDAPDirectory, CAUMapName, CAUMapID, CAULastLogonDate, CAUPrevLogonDate, CAUUserTypeID, CAURestrictedInterfaces, CAUApplicationMetadata, CAUCreationDate, CAUVaultID
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAUsers;';
        INSERT INTO stg_sh_users (ImportBatchId, SourceFileName, CAUUserID, CAUUserName, CAULocationID, CAULocationName, CAUFirstName, CAULastName, CAUBusinessEmail, CAUDisabled, CAUExpirationDate, CAUPasswordNeverExpires, CAUAuthenticationMethods, CAUAuthorizations, CAUGatewayAccountAuthorizations, CAUDistinguishedName, CAUExternalInternal, CAULDAPFullDN, CAULDAPDirectory, CAUMapName, CAUMapID, CAULastLogonDate, CAUPrevLogonDate, CAUUserTypeID, CAURestrictedInterfaces, CAUApplicationMetadata, CAUCreationDate, CAUVaultID)
        EXEC sp_executesql @sql;
        SET @RowsUsers = @@ROWCOUNT;

        -- CAGroups -> stg_sh_groups
        TRUNCATE TABLE stg_sh_groups;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAGroups (EVD)'' AS SourceFileName, CAGGroupID, CAGGroupName, CAGLocationID, CAGLocationName, CAGDescription, CAGExternalGroupName, CAGExternalInternal, CAGLDAPFullDN, CAGLDAPDirectory, CAGMapName, CAGMapID, CAGVaultID
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAGroups;';
        INSERT INTO stg_sh_groups (ImportBatchId, SourceFileName, CAGGroupID, CAGGroupName, CAGLocationID, CAGLocationName, CAGDescription, CAGExternalGroupName, CAGExternalInternal, CAGLDAPFullDN, CAGLDAPDirectory, CAGMapName, CAGMapID, CAGVaultID)
        EXEC sp_executesql @sql;
        SET @RowsGroups = @@ROWCOUNT;

        -- CAGroupMembers -> stg_sh_groupmembers
        TRUNCATE TABLE stg_sh_groupmembers;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAGroupMembers (EVD)'' AS SourceFileName, CAGMGroupID, CAGMUserID, CAGMMemberIsGroup, CAGMVaultID
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAGroupMembers;';
        INSERT INTO stg_sh_groupmembers (ImportBatchId, SourceFileName, CAGMGroupID, CAGMUserID, CAGMMemberIsGroup, CAGMVaultID)
        EXEC sp_executesql @sql;
        SET @RowsGroupMembers = @@ROWCOUNT;

        -- CASafes -> stg_sh_safes
        TRUNCATE TABLE stg_sh_safes;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CASafes (EVD)'' AS SourceFileName, CASSafeID, CASSafeName, CASLocationID, CASLocationName, CASSize, CASMaxSize, CASUsedSize, CASLastUsed, CASSecurityLevel, CASDailyVersions, CASMonthlyVersions, CASYearlyVersions, CASLogRetentionPeriod, CASObjectsRetentionPeriod, CASRequestsRetentionPeriod, CASConfirmersCount, CASConfirmType, CASRequireReasonToRetrieve, CASEnforceExclusivePasswords, CASRequireContentValidation, CASCreationDate, CASCreatedBy, CASNumberOfPasswordVersions, CASVaultID
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CASafes;';
        INSERT INTO stg_sh_safes (ImportBatchId, SourceFileName, CASSafeID, CASSafeName, CASLocationID, CASLocationName, CASSize, CASMaxSize, CASUsedSize, CASLastUsed, CASSecurityLevel, CASDailyVersions, CASMonthlyVersions, CASYearlyVersions, CASLogRetentionPeriod, CASObjectsRetentionPeriod, CASRequestsRetentionPeriod, CASConfirmersCount, CASConfirmType, CASRequireReasonToRetrieve, CASEnforceExclusivePasswords, CASRequireContentValidation, CASCreationDate, CASCreatedBy, CASNumberOfPasswordVersions, CASVaultID)
        EXEC sp_executesql @sql;
        SET @RowsSafes = @@ROWCOUNT;

        -- CAOwners -> stg_sh_owners
        TRUNCATE TABLE stg_sh_owners;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAOwners (EVD)'' AS SourceFileName, CAOSafeID, CAOSafeName, CAOOwnerID, CAOOwnerName, CAOOwnerType, CAOExpirationDate, CAOList, CAORetrieve, CAOCreateObject, CAOUpdateObject, CAOUpdateObjectProperties, CAORenameObject, CAODelete, CAOInitiateCPMChange, CAOInitiateCPMChangeWithManualPassword, CAOCreateFolder, CAODeleteFolder, CAOUnlockObject, CAOMoveFrom, CAOMoveInto, CAOManageSafe, CAOManageSafeOwners, CAOValidateSafeContent, CAOBackup, CAONoConfirmRequired, CAOConfirm, CAOEventsList, CAOEventsAdd, CAOVaultID
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAOwners;';
        INSERT INTO stg_sh_owners (ImportBatchId, SourceFileName, CAOSafeID, CAOSafeName, CAOOwnerID, CAOOwnerName, CAOOwnerType, CAOExpirationDate, CAOList, CAORetrieve, CAOCreateObject, CAOUpdateObject, CAOUpdateObjectProperties, CAORenameObject, CAODelete, CAOInitiateCPMChange, CAOInitiateCPMChangeWithManualPassword, CAOCreateFolder, CAODeleteFolder, CAOUnlockObject, CAOMoveFrom, CAOMoveInto, CAOManageSafe, CAOManageSafeOwners, CAOValidateSafeContent, CAOBackup, CAONoConfirmRequired, CAOConfirm, CAOEventsList, CAOEventsAdd, CAOVaultID)
        EXEC sp_executesql @sql;
        SET @RowsOwners = @@ROWCOUNT;

        -- CAFiles -> stg_sh_files
        TRUNCATE TABLE stg_sh_files;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAFiles (EVD)'' AS SourceFileName, CAFSafeID, CAFSafeName, CAFFolder, CAFFileID, CAFFileName, CAFInternalName, CAFSize, CAFCreatedBy, CAFCreationDate, CAFLastUsedBy, CAFLastUsedDate, CAFModificationDate, CAFModifiedBy, CAFDeletedBy, CAFDeletionDate, CAFValidationStatus, CAFType, CAFCompressedSize, CAFLastModifiedDate, CAFLastModifiedBy, CAFLastUsedByHuman, CAFLastUsedHumanDate, CAFLastUsedByComponent, CAFLastUsedComponentDate, CAFVaultID
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAFiles;';
        INSERT INTO stg_sh_files (ImportBatchId, SourceFileName, CAFSafeID, CAFSafeName, CAFFolder, CAFFileID, CAFFileName, CAFInternalName, CAFSize, CAFCreatedBy, CAFCreationDate, CAFLastUsedBy, CAFLastUsedDate, CAFModificationDate, CAFModifiedBy, CAFDeletedBy, CAFDeletionDate, CAFValidationStatus, CAFType, CAFCompressedSize, CAFLastModifiedDate, CAFLastModifiedBy, CAFLastUsedByHuman, CAFLastUsedHumanDate, CAFLastUsedByComponent, CAFLastUsedComponentDate, CAFVaultID)
        EXEC sp_executesql @sql;
        SET @RowsFiles = @@ROWCOUNT;

        -- CAObjectProperties -> stg_sh_objectproperties
        TRUNCATE TABLE stg_sh_objectproperties;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAObjectProperties (EVD)'' AS SourceFileName, CAOPObjectPropertyId, CAOPObjectPropertyName, CAOPSafeId, CAOPFileId, CAOPObjectPropertyValue, CAOPOptions
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAObjectProperties;';
        INSERT INTO stg_sh_objectproperties (ImportBatchId, SourceFileName, CAOPObjectPropertyId, CAOPObjectPropertyName, CAOPSafeId, CAOPFileId, CAOPObjectPropertyValue, CAOPOptions)
        EXEC sp_executesql @sql;
        SET @RowsObjectProperties = @@ROWCOUNT;

        -- CARequests -> stg_sh_requests
        TRUNCATE TABLE stg_sh_requests;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CARequests (EVD)'' AS SourceFileName, CARRequestID, CARUserID, CARUserName, CARSafeID, CARSafeName, CARFileID, CARFileName, CARReason, CARCreationDate, CARExpirationDate, CARStatus
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CARequests;';
        INSERT INTO stg_sh_requests (ImportBatchId, SourceFileName, CARRequestID, CARUserID, CARUserName, CARSafeID, CARSafeName, CARFileID, CARFileName, CARReason, CARCreationDate, CARExpirationDate, CARStatus)
        EXEC sp_executesql @sql;
        SET @RowsRequests = @@ROWCOUNT;

        -- CAConfirmations -> stg_sh_confirmations
        TRUNCATE TABLE stg_sh_confirmations;
        SET @sql = N'SELECT ''' + CAST(@ImportBatchId AS NVARCHAR(36)) + N''' AS ImportBatchId, N''CAConfirmations (EVD)'' AS SourceFileName, CACRequestID, CACSafeID, CACSafeName, CACUserID, CACAction
                     FROM ' + QUOTENAME(@EVDDatabaseName) + N'.dbo.CAConfirmations;';
        INSERT INTO stg_sh_confirmations (ImportBatchId, SourceFileName, CACRequestID, CACSafeID, CACSafeName, CACUserID, CACAction)
        EXEC sp_executesql @sql;
        SET @RowsConfirmations = @@ROWCOUNT;

        UPDATE import_log
        SET RowsLoaded = @RowsUsers + @RowsGroups + @RowsGroupMembers + @RowsSafes + @RowsOwners + @RowsFiles + @RowsObjectProperties + @RowsRequests + @RowsConfirmations,
            CompletedAt = SYSUTCDATETIME(), Status = 'Succeeded'
        WHERE ImportBatchId = @ImportBatchId;
    END TRY
    BEGIN CATCH
        UPDATE import_log SET CompletedAt = SYSUTCDATETIME(), Status = 'Failed', ErrorMessage = ERROR_MESSAGE()
        WHERE ImportBatchId = @ImportBatchId;
        THROW;
    END CATCH
END
GO

/* ============================================================================
   Orchestrators
   ============================================================================ */

CREATE OR ALTER PROCEDURE usp_Import_PrivilegeCloud_All (
    @PlatformsFile     NVARCHAR(500),
    @UsersFile         NVARCHAR(500),
    @GroupsFile        NVARCHAR(500),
    @GroupMembersFile  NVARCHAR(500),
    @SafesFile         NVARCHAR(500),
    @AccountsFile      NVARCHAR(500),
    @EntitlementsFile  NVARCHAR(500),
    @EntitlementsExportDate DATE   -- Entitlements filename is date-stamped (e.g. Export_Entitlements_2026-08-25.csv);
                                    -- pass that date explicitly rather than having this parse it out of the filename
)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC usp_Import_PC_Platforms     @FilePath = @PlatformsFile;
    EXEC usp_Import_PC_Users         @FilePath = @UsersFile;
    EXEC usp_Import_PC_Groups        @FilePath = @GroupsFile;
    EXEC usp_Import_PC_GroupMembers  @FilePath = @GroupMembersFile;
    EXEC usp_Import_PC_Safes         @FilePath = @SafesFile;
    EXEC usp_Import_PC_Accounts      @FilePath = @AccountsFile;
    EXEC usp_Import_PC_Entitlements  @FilePath = @EntitlementsFile, @ExportDate = @EntitlementsExportDate;
END
GO


/* ============================================================================
   Single entry point: imports both sources' staging data. Does NOT call
   usp_RunFullLoad -- that's a deliberate separation (import vs. transform
   stay as two explicit steps you run in sequence), matching how the rest
   of this project's files are documented. After this succeeds, run:
       EXEC usp_RunFullLoad;
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Import_All (
    @EVDDatabaseName   NVARCHAR(128),
    @PlatformsFile     NVARCHAR(500),
    @UsersFile         NVARCHAR(500),
    @GroupsFile        NVARCHAR(500),
    @GroupMembersFile  NVARCHAR(500),
    @SafesFile         NVARCHAR(500),
    @AccountsFile      NVARCHAR(500),
    @EntitlementsFile  NVARCHAR(500),
    @EntitlementsExportDate DATE
)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC usp_Import_SelfHosted_EVD @EVDDatabaseName = @EVDDatabaseName;
    EXEC usp_Import_PrivilegeCloud_All
        @PlatformsFile = @PlatformsFile, @UsersFile = @UsersFile, @GroupsFile = @GroupsFile,
        @GroupMembersFile = @GroupMembersFile, @SafesFile = @SafesFile, @AccountsFile = @AccountsFile,
        @EntitlementsFile = @EntitlementsFile, @EntitlementsExportDate = @EntitlementsExportDate;
END
GO

PRINT 'Source import procedures created successfully.';
PRINT 'Example usage:';
PRINT '  EXEC usp_Import_All';
PRINT '    @EVDDatabaseName = ''YourEVDDatabaseName'',';
PRINT '    @PlatformsFile = ''\\server\share\Export_PlatformsList.csv'',';
PRINT '    @UsersFile = ''\\server\share\Export_UsersList.csv'',';
PRINT '    @GroupsFile = ''\\server\share\Export_GroupsList.csv'',';
PRINT '    @GroupMembersFile = ''\\server\share\Export_Local_Group_Members_2026-08-27.csv'',';
PRINT '    @SafesFile = ''\\server\share\Export_SafesList.csv'',';
PRINT '    @AccountsFile = ''\\server\share\Export_AccountsList.csv'',';
PRINT '    @EntitlementsFile = ''\\server\share\Export_Entitlements_2026-08-25.csv'',';
PRINT '    @EntitlementsExportDate = ''2026-08-25'';';
PRINT '  EXEC usp_RunFullLoad;';
