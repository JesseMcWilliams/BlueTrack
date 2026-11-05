/* ============================================================================
   68_BlueTrack_RunProgress.sql

   RUN THIS AFTER 01-67. Guarded -- safe to re-run.

   D-337 (the user's request of 2026-11-05: a pull at a customer site seemed to
   hang and nobody could see where): the Self-Hosted Direct pull and the
   nightly Load record where they are while they run.

   - dbo.cyberark_direct_pull_step: one row per step of a pull (logon,
     Safes, accounts, account details, ...), with when it started and ended
     and how many items it has read of how many. The API writes it every few
     seconds during the pull, so the row of a pull that stops answering names
     its step even after the application restarts.
   - dbo.etl_run_log.SessionId: the SQL Server session that runs the step,
     so its waits can be looked up (dbo.usp_Get_EtlSessionActivity below).
   - dbo.etl_run_step: one row per procedure of the Load, with its start and
     end. The Load runs in one transaction, so these rows are written inside
     it: the app reads them WITH (NOLOCK) to see the running procedure, and a
     failed Load writes them again after its rollback (from a table
     variable, which a rollback doesn't undo), the procedures that had
     finished as RolledBack and the one that failed as Failed.
   - usp_RunFullLoad: the same procedures in the same order as 57_, now
     logged one by one.
   - dbo.usp_Get_EtlSessionActivity: for each Import, Load or Snapshot still
     running, what its session is doing: its wait, the procedure and
     statement it's in, and the session blocking it, if any. Reading other
     sessions needs VIEW SERVER STATE; the app's login doesn't have it.
     Database/Manual/10 signs this one procedure with a certificate that
     has it (the installer's step Db.SessionActivity), so the app can see
     only what this procedure returns. Unsigned (for a caller without the
     permission), it returns the runs only, with CanSeeSessions = 0. Any later script that alters it drops the
     signature; the installer signs it again on every run.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('dbo.cyberark_direct_pull_step') IS NULL
BEGIN
    CREATE TABLE dbo.cyberark_direct_pull_step (
        PullKey     BIGINT        NOT NULL CONSTRAINT FK_cyberark_direct_pull_step_Pull
                                  REFERENCES dbo.cyberark_direct_pull (PullKey) ON DELETE CASCADE,
        StepOrder   INT           NOT NULL,
        StepName    NVARCHAR(100) NOT NULL,
        StartedAt   DATETIME2     NOT NULL,
        CompletedAt DATETIME2     NULL,
        ItemsDone   INT           NULL,
        ItemsTotal  INT           NULL,
        CONSTRAINT PK_cyberark_direct_pull_step PRIMARY KEY (PullKey, StepOrder)
    );
END
GO

IF COL_LENGTH('dbo.etl_run_log', 'SessionId') IS NULL
    ALTER TABLE dbo.etl_run_log ADD SessionId INT NULL CONSTRAINT DF_etl_run_log_SessionId DEFAULT @@SPID;
GO

IF OBJECT_ID('dbo.etl_run_step') IS NULL
BEGIN
    CREATE TABLE dbo.etl_run_step (
        EtlRunKey   BIGINT        NOT NULL CONSTRAINT FK_etl_run_step_Run REFERENCES dbo.etl_run_log (EtlRunKey) ON DELETE CASCADE,
        StepOrder   INT           NOT NULL,
        ProcName    NVARCHAR(128) NOT NULL,
        StartedAt   DATETIME2     NOT NULL,
        CompletedAt DATETIME2     NULL,
        Status      NVARCHAR(20)  NOT NULL CONSTRAINT DF_etl_run_step_Status DEFAULT N'Started'
                                  CONSTRAINT CK_etl_run_step_Status CHECK (Status IN (N'Started', N'Succeeded', N'RolledBack', N'Failed')),
        CONSTRAINT PK_etl_run_step PRIMARY KEY (EtlRunKey, StepOrder)
    );
END
GO

/* As in 57_, with each procedure logged in dbo.etl_run_step. */
CREATE OR ALTER PROCEDURE usp_RunFullLoad
    @TakeSnapshot BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load');
    SET @EtlRunKey = SCOPE_IDENTITY();

    DECLARE @Procs TABLE (StepOrder INT NOT NULL PRIMARY KEY, ProcName NVARCHAR(128) NOT NULL);
    INSERT INTO @Procs (StepOrder, ProcName) VALUES
        (1,  N'dbo.usp_Load_DimLocation'),
        (2,  N'dbo.usp_Load_DimPlatform'),
        (3,  N'dbo.usp_Load_DimSafe'),
        (4,  N'dbo.usp_Load_DimUser'),
        (5,  N'dbo.usp_Load_DimGroup'),
        (6,  N'dbo.usp_Load_GroupMembership'),
        (7,  N'dbo.usp_Load_AdGroupMembers'),                     -- D-281
        (8,  N'dbo.usp_Load_DimCyberArkApplication'),             -- D-282
        (9,  N'dbo.usp_Load_FactAccount'),
        (10, N'dbo.usp_Load_FactAccountClassicDetail'),           -- D-280
        (11, N'dbo.usp_Load_CpmHealth'),                          -- D-288: before the Progress Rules, which can test RequiresPsm
        (12, N'dbo.usp_Load_FactSafeEntitlement'),                -- D-300: before the Progress Rules
        (13, N'dbo.usp_Check_SafeAccessPolicies'),                -- D-300, D-301
        (14, N'dbo.usp_Load_FactAccountProgress'),
        (15, N'dbo.usp_Load_AccountProgressAutoAdvance'),
        (16, N'dbo.usp_Load_AccountReconciliation'),
        -- D-224: usp_Load_FactAccountProgressHistory is in usp_RunDailySnapshot.
        (17, N'dbo.usp_DeriveTargetsFromAccountAddress'),         -- D-123
        (18, N'dbo.usp_DeriveAccountTargetMap_FromPendingSafes'),
        (19, N'dbo.usp_DeriveAccountTargetMap_FromAddress'),      -- D-193
        (20, N'dbo.usp_RecalculateRiskScores');

    -- A rollback doesn't undo a table variable: what ran is written again from here after one.
    DECLARE @Steps TABLE (StepOrder INT NOT NULL PRIMARY KEY, ProcName NVARCHAR(128) NOT NULL, StartedAt DATETIME2 NOT NULL, CompletedAt DATETIME2 NULL);
    DECLARE @Step INT = 1, @LastStep INT = (SELECT MAX(StepOrder) FROM @Procs), @Proc NVARCHAR(128), @At DATETIME2;

    BEGIN TRY
        BEGIN TRANSACTION;

        WHILE @Step <= @LastStep
        BEGIN
            SELECT @Proc = ProcName FROM @Procs WHERE StepOrder = @Step;
            SET @At = SYSUTCDATETIME();
            INSERT INTO @Steps (StepOrder, ProcName, StartedAt) VALUES (@Step, @Proc, @At);
            INSERT INTO dbo.etl_run_step (EtlRunKey, StepOrder, ProcName, StartedAt) VALUES (@EtlRunKey, @Step, @Proc, @At);

            EXEC @Proc;

            SET @At = SYSUTCDATETIME();
            UPDATE @Steps SET CompletedAt = @At WHERE StepOrder = @Step;
            UPDATE dbo.etl_run_step SET CompletedAt = @At, Status = N'Succeeded' WHERE EtlRunKey = @EtlRunKey AND StepOrder = @Step;
            SET @Step += 1;
        END;

        COMMIT TRANSACTION;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

        DELETE FROM dbo.etl_run_step WHERE EtlRunKey = @EtlRunKey;
        INSERT INTO dbo.etl_run_step (EtlRunKey, StepOrder, ProcName, StartedAt, CompletedAt, Status)
        SELECT @EtlRunKey, StepOrder, ProcName, StartedAt, COALESCE(CompletedAt, SYSUTCDATETIME()),
               CASE WHEN CompletedAt IS NULL THEN N'Failed' ELSE N'RolledBack' END
        FROM @Steps;

        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;

        -- D-224: snapshot the unchanged state; a snapshot failure is in its own etl_run_log row.
        IF @TakeSnapshot = 1
        BEGIN
            BEGIN TRY
                EXEC dbo.usp_RunDailySnapshot;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            END CATCH
        END;

        THROW;
    END CATCH

    IF @TakeSnapshot = 1
        EXEC dbo.usp_RunDailySnapshot;
END
GO

/* No dynamic SQL in here: a certificate's signature doesn't carry into it (Database/Manual/10). */
CREATE OR ALTER PROCEDURE dbo.usp_Get_EtlSessionActivity
AS
BEGIN
    SET NOCOUNT ON;

    -- Without the permission SQL Server refuses the session views outright (error 371 on SQL Server 2025), so they
    -- are read only when the signature (Database/Manual/10) or the caller has it.
    IF HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') <> 1
    BEGIN
        SELECT EtlRunKey, StepName, StartedAt, SessionId, CAST(0 AS BIT) AS CanSeeSessions
        FROM dbo.etl_run_log WITH (NOLOCK)   -- never wait behind the run it reports on
        WHERE Status = N'Started' AND StartedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
        ORDER BY EtlRunKey;
        RETURN;
    END;

    -- sys.dm_exec_sessions' times are in the server's local time; etl_run_log's are in UTC.
    DECLARE @LocalOffsetMinutes INT = DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME());

    SELECT r.EtlRunKey, r.StepName, r.StartedAt, r.SessionId, CAST(1 AS BIT) AS CanSeeSessions,
           CAST(CASE WHEN s.session_id IS NULL THEN 0 ELSE 1 END AS BIT) AS SessionFound,
           q.status AS RequestStatus, q.command AS Command,
           q.wait_type AS WaitType, q.wait_time AS WaitMs, q.wait_resource AS WaitResource,
           q.last_wait_type AS LastWaitType, q.total_elapsed_time AS RequestElapsedMs,
           NULLIF(q.blocking_session_id, 0) AS BlockingSessionId,
           OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' + OBJECT_NAME(t.objectid, t.dbid) AS CurrentProcedure,
           LEFT(LTRIM(SUBSTRING(t.text, q.statement_start_offset / 2 + 1,
                CASE WHEN q.statement_end_offset = -1 THEN 4000 ELSE (q.statement_end_offset - q.statement_start_offset) / 2 + 1 END)), 400) AS CurrentStatement,
           -- The blocker may be any session on the server: who and what it is, never its SQL text.
           bs.login_name AS BlockerLogin, bs.host_name AS BlockerHost, bs.program_name AS BlockerProgram,
           bs.status AS BlockerStatus, DB_NAME(bs.database_id) AS BlockerDatabase, bs.open_transaction_count AS BlockerOpenTransactions,
           DATEADD(MINUTE, -@LocalOffsetMinutes, bs.last_request_start_time) AS BlockerLastRequestStart, br.command AS BlockerCommand, br.wait_type AS BlockerWaitType
    FROM dbo.etl_run_log r WITH (NOLOCK)   -- never wait behind the run it reports on
    LEFT JOIN sys.dm_exec_sessions s
           ON s.session_id = r.SessionId
          -- Not a later session given the same id. A second's slack: login_time (datetime) rounds to 3 ms, and a new
          -- connection can write its run row within a few ms of signing in.
          AND s.login_time <= DATEADD(SECOND, 1, DATEADD(MINUTE, @LocalOffsetMinutes, r.StartedAt))
    LEFT JOIN sys.dm_exec_requests q ON q.session_id = s.session_id AND q.database_id = DB_ID()
    OUTER APPLY sys.dm_exec_sql_text(q.sql_handle) t
    LEFT JOIN sys.dm_exec_sessions bs ON bs.session_id = q.blocking_session_id AND q.blocking_session_id <> q.session_id
    LEFT JOIN sys.dm_exec_requests br ON br.session_id = bs.session_id
    WHERE r.Status = N'Started' AND r.StartedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
    ORDER BY r.EtlRunKey;
END
GO

PRINT '68_BlueTrack_RunProgress.sql complete.';
GO
