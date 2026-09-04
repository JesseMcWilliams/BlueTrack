/* ============================================================================
   14_BlueTrack_SecretsStoreSchema.sql

   RUN THIS AFTER 01-13.

   web.secrets_store: designed in full in Design_Secrets_Storage.md's Data
   Model section but never actually added to 06_BlueTrack_WebInterface_Schema.sql
   -- found missing while building the Secrets Store Configuration admin
   page. This is the config *record* only (which backend is active, plus
   its non-secret settings) -- it does not implement any actual backend
   (Windows DPAPI, CyberArk CP, etc.); those remain unbuilt, consistent
   with AuthenticationExtensions.cs's own note that only WindowsIntegrated
   authentication is wired so far.

   "Exactly one active backend at a time" (per the design doc) is enforced
   at the application layer (SecretsStoreRepository), not a database
   constraint -- consistent with how this project avoids triggers/CHECK
   constraints for business rules elsewhere (e.g. risk_exception's
   AccountKey/ApplicationKey exclusivity).
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.secrets_store', 'U') IS NULL
BEGIN
    CREATE TABLE web.secrets_store (
        SecretStoreKey     INT IDENTITY(1,1) PRIMARY KEY,
        BackendType         NVARCHAR(50)     NOT NULL UNIQUE,   -- AzureKeyVault / AwsSecretsManager / WindowsDpapi / CyberArkCCP / CyberArkCP / CyberArkConjur
        IsActive             BIT              NOT NULL DEFAULT 0,
        BackendSettings         NVARCHAR(MAX)    NULL             -- non-secret backend-specific settings as JSON (e.g. Key Vault URI)
    );

    INSERT INTO web.secrets_store (BackendType, IsActive, BackendSettings) VALUES
        ('WindowsDpapi',      1, NULL),   -- first backend built overall (D-36) -- seeded active by default
        ('CyberArkCP',        0, NULL),   -- designated first CyberArk backend (D-32)
        ('AzureKeyVault',     0, NULL),
        ('AwsSecretsManager', 0, NULL),
        ('CyberArkCCP',       0, NULL),
        ('CyberArkConjur',    0, NULL);
END
GO

PRINT 'web.secrets_store created and seeded (D-06/D-15/D-32/D-36).';
