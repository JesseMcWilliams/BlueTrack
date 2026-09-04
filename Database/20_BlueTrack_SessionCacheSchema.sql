/* ============================================================================
   20_BlueTrack_SessionCacheSchema.sql

   RUN THIS AFTER 01-19.

   D-82 (Design_Decision_Register.md's "session-layer-dependent
   follow-ups"): the user chose a SQL Server-backed distributed cache
   (Microsoft.Extensions.Caching.SqlServer) over Redis, since no such
   infrastructure exists in this environment yet and SQL Server is already
   the one confirmed, reachable piece of shared infrastructure. This is
   the exact table shape Microsoft.Extensions.Caching.SqlServer requires --
   confirmed by actually running the real `dotnet-sql-cache create` tool
   (installed via `dotnet tool install --global dotnet-sql-cache`) against
   this database and reading back what it really created via
   INFORMATION_SCHEMA, not copied from documentation. The only difference
   from the tool's own output: an explicit PRIMARY KEY constraint name
   (the tool leaves it system-generated), for consistency with this
   project's convention of naming its own constraints.

   "Session" here is BlueTrack's cached-rights-per-identity concept (see
   App/Api/Auth/UserRightsCache.cs), not an ASP.NET Core cookie-based
   Session -- Windows Negotiate doesn't need cookie-based session tracking
   for anything else the app does, so no cookie/session-ID machinery was
   introduced on top of this cache.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.distributed_cache', 'U') IS NULL
BEGIN
    CREATE TABLE web.distributed_cache (
        Id                             NVARCHAR(449)     NOT NULL,
        Value                            VARBINARY(MAX)    NOT NULL,
        ExpiresAtTime                      DATETIMEOFFSET    NOT NULL,
        SlidingExpirationInSeconds            BIGINT            NULL,
        AbsoluteExpiration                       DATETIMEOFFSET    NULL,
        CONSTRAINT PK_distributed_cache PRIMARY KEY CLUSTERED (Id)
    );

    CREATE NONCLUSTERED INDEX Index_ExpiresAtTime ON web.distributed_cache (ExpiresAtTime);
END
GO

PRINT 'web.distributed_cache created (D-82).';
