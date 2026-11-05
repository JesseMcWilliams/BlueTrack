/* ============================================================================
   10_BlueTrack_SignSessionActivity.sql

   D-337 (the user's choice of 2026-11-05: a signed procedure, not a grant to
   the app's login): lets BlueTrack show what its running Import, Load or
   Snapshot is waiting on. Reading another session's waits needs the server
   permission VIEW SERVER STATE. Instead of giving it to the app's login,
   which could then read every session on the server, this signs the one
   procedure dbo.usp_Get_EtlSessionActivity (Database/68) with a certificate
   whose login holds it. The app can call that procedure, and only that
   procedure gets the permission; it returns BlueTrack's own running steps
   and the session blocking one, never another session's SQL text.

   Like the other scripts in Database/Manual, App/Migrator never runs this.
   Deploy/Install-BlueTrack.ps1 runs it on every run (step
   Db.SessionActivity), since altering the procedure (a later script) drops
   the signature; by hand, as a sysadmin:

       sqlcmd -S <server> -C -I -v DatabaseName="BlueTrack" -i 10_BlueTrack_SignSessionActivity.sql

   Creates (or recreates, so it is safe to re-run), with the database's name
   in each, since BlueTrack and BlueTrackTest can share an instance:
     - in the database: the certificate BlueTrack_<db>_SessionActivity, its
       signature on dbo.usp_Get_EtlSessionActivity, then its private key
       removed (nothing else can be signed with it; the next run makes a new
       one, so its password is random and never kept);
     - in master: the same certificate (public key only), the login
       BlueTrack_<db>_SessionActivity made from it (it can't sign in), and
       GRANT VIEW SERVER STATE to that login.
   Check it: SELECT CanSeeSessions FROM the procedure's result, or
       SELECT * FROM sys.crypt_properties WHERE major_id = OBJECT_ID('dbo.usp_Get_EtlSessionActivity');
   ============================================================================ */

USE [$(DatabaseName)];
GO

SET NOCOUNT ON;

DECLARE @Name SYSNAME = N'BlueTrack_' + DB_NAME() + N'_SessionActivity';
DECLARE @Proc NVARCHAR(300) = N'dbo.usp_Get_EtlSessionActivity';
DECLARE @Sql NVARCHAR(MAX);

IF OBJECT_ID(@Proc) IS NULL
    THROW 50000, 'dbo.usp_Get_EtlSessionActivity is missing: run the Migrator (Database/68) first.', 1;

-- 1. Remove what an earlier run made.
IF EXISTS (SELECT 1 FROM sys.crypt_properties cp JOIN sys.certificates c ON c.thumbprint = cp.thumbprint
           WHERE cp.major_id = OBJECT_ID(@Proc) AND c.name = @Name)
BEGIN
    SET @Sql = N'DROP SIGNATURE FROM ' + @Proc + N' BY CERTIFICATE ' + QUOTENAME(@Name) + N';';
    EXEC sys.sp_executesql @Sql;
END;
IF EXISTS (SELECT 1 FROM sys.certificates WHERE name = @Name)
BEGIN
    SET @Sql = N'DROP CERTIFICATE ' + QUOTENAME(@Name) + N';';
    EXEC sys.sp_executesql @Sql;
END;
IF EXISTS (SELECT 1 FROM master.sys.server_principals WHERE name = @Name)
BEGIN
    SET @Sql = N'DROP LOGIN ' + QUOTENAME(@Name) + N';';
    EXEC master.sys.sp_executesql @Sql;
END;
IF EXISTS (SELECT 1 FROM master.sys.certificates WHERE name = @Name)
BEGIN
    SET @Sql = N'DROP CERTIFICATE ' + QUOTENAME(@Name) + N';';
    EXEC master.sys.sp_executesql @Sql;
END;

-- 2. A new certificate in the database; sign the procedure, then throw the private key away.
DECLARE @Password NVARCHAR(100) = CONVERT(NVARCHAR(36), NEWID()) + N'aA1!' + CONVERT(NVARCHAR(36), NEWID());
SET @Sql = N'CREATE CERTIFICATE ' + QUOTENAME(@Name) + N' ENCRYPTION BY PASSWORD = ' + QUOTENAME(@Password, '''')
         + N' WITH SUBJECT = N''BlueTrack: signs dbo.usp_Get_EtlSessionActivity (D-337)'', EXPIRY_DATE = ''20991231'';';
EXEC sys.sp_executesql @Sql;
SET @Sql = N'ADD SIGNATURE TO ' + @Proc + N' BY CERTIFICATE ' + QUOTENAME(@Name) + N' WITH PASSWORD = ' + QUOTENAME(@Password, '''') + N';';
EXEC sys.sp_executesql @Sql;
SET @Sql = N'ALTER CERTIFICATE ' + QUOTENAME(@Name) + N' REMOVE PRIVATE KEY;';
EXEC sys.sp_executesql @Sql;

-- 3. Its public key in master, a login from it, and the one permission.
DECLARE @Public VARBINARY(MAX) = CERTENCODED(CERT_ID(@Name));
SET @Sql = N'CREATE CERTIFICATE ' + QUOTENAME(@Name) + N' FROM BINARY = ' + CONVERT(NVARCHAR(MAX), @Public, 1) + N';'
         + N' CREATE LOGIN ' + QUOTENAME(@Name) + N' FROM CERTIFICATE ' + QUOTENAME(@Name) + N';'
         + N' GRANT VIEW SERVER STATE TO ' + QUOTENAME(@Name) + N';';
EXEC master.sys.sp_executesql @Sql;

PRINT '10_BlueTrack_SignSessionActivity.sql complete: ' + @Proc + ' is signed by ' + @Name + '.';
GO
