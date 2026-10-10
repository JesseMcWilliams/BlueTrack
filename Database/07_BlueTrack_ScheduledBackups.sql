/* ============================================================================
   07_BlueTrack_ScheduledBackups.sql

   RUN THIS AFTER 01-06. Guarded (COL_LENGTH / OBJECT_ID / sys checks) -- safe
   to re-run.

   D-200: scheduled database backups, run by the API itself
   (App/Api/Backups/ScheduledBackupService.cs) into web.app_config.BackupFolder,
   which must be inside FileAccess:AllowedRoots (D-197). Options chosen
   directly on 2026-10-10: in the app (not a SQL Agent job), daily at a set
   time, keep N days, the BlueTrack database only.

     web.app_config.ScheduledBackupTime          time of day; NULL = off (the default).
     web.app_config.ScheduledBackupRetentionDays scheduled backup files older than this
                                                  are deleted after each scheduled
                                                  backup (default 14). Manual backups
                                                  from the Deployment page never are.
     web.backup_run                              one row per backup, scheduled or manual:
                                                  outcome, file, files deleted, error.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'ScheduledBackupTime') IS NULL
    ALTER TABLE web.app_config ADD ScheduledBackupTime TIME(0) NULL;
GO
IF COL_LENGTH('web.app_config', 'ScheduledBackupRetentionDays') IS NULL
    ALTER TABLE web.app_config ADD ScheduledBackupRetentionDays INT NOT NULL
        CONSTRAINT DF_app_config_ScheduledBackupRetentionDays DEFAULT 14
        CONSTRAINT CK_app_config_ScheduledBackupRetentionDays CHECK (ScheduledBackupRetentionDays BETWEEN 1 AND 3650);
GO

IF OBJECT_ID('web.backup_run', 'U') IS NULL
BEGIN
    CREATE TABLE web.backup_run (
        BackupRunKey     BIGINT IDENTITY(1,1) PRIMARY KEY,
        TriggerType      NVARCHAR(20)    NOT NULL,   -- 'Schedule' / 'Manual'
        TriggeredBy      INT             NULL REFERENCES web.app_user(UserKey),
        StartedAt        DATETIME2       NOT NULL,
        FinishedAt       DATETIME2       NULL,
        Outcome          NVARCHAR(20)    NOT NULL,   -- 'Succeeded' / 'Failed'
        FilePath         NVARCHAR(500)   NULL,
        FilesDeleted     INT             NULL,       -- old scheduled backups removed afterwards
        ErrorMessage     NVARCHAR(2000)  NULL,
        CONSTRAINT CK_backup_run_TriggerType CHECK (TriggerType IN ('Schedule', 'Manual')),
        CONSTRAINT CK_backup_run_Outcome CHECK (Outcome IN ('Succeeded', 'Failed'))
    );
    CREATE INDEX IX_backup_run_StartedAt ON web.backup_run (StartedAt DESC);
END
GO

PRINT '07_BlueTrack_ScheduledBackups.sql complete.';
