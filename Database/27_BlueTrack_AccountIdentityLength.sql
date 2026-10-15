/* ============================================================================
   27_BlueTrack_AccountIdentityLength.sql

   RUN THIS AFTER 01-26. Guarded -- safe to re-run.

   D-236: an account inventory feed identifies an account by its lowercase
   "username@address" (D-212), and the column holding it,
   dbo.fact_account.SourceAccountId, was NVARCHAR(100). A pending-accounts
   export uses a SID as the user name (about 45 characters) with a fully
   qualified address, so many rows went over the limit and failed with
   "UserName and Address together are longer than 99 characters".

   The column becomes NVARCHAR(400): 800 bytes, so UQ_fact_account
   (SourceSystemKey, SourceAccountId) stays under the 900-byte index key limit
   of every SQL Server version. The unique constraint has to be dropped and
   re-added around the change. Nothing else references the column's width.
   ============================================================================ */

USE $DatabaseName$;
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.fact_account') AND name = 'SourceAccountId' AND max_length < 800
)
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('dbo.fact_account') AND name = 'UQ_fact_account')
        ALTER TABLE dbo.fact_account DROP CONSTRAINT UQ_fact_account;

    ALTER TABLE dbo.fact_account ALTER COLUMN SourceAccountId NVARCHAR(400) NOT NULL;

    ALTER TABLE dbo.fact_account ADD CONSTRAINT UQ_fact_account UNIQUE (SourceSystemKey, SourceAccountId);

    COMMIT TRANSACTION;
END
GO
