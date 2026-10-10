/* ============================================================================
   49_BlueTrack_ProviderSettingsDedupe.sql

   RUN THIS AFTER 01-48. Safe to re-run: a row already in the clean shape
   (one entry per setting, camelCase names) is left unchanged.

   Found 2026-10-09 on the dev host: the Okta (SAML) provider's settings held
   every setting twice -- "spEntityId" (camelCase, filled in on the Identity
   Providers page) and "SpEntityId" (PascalCase, empty, from
   12_BlueTrack_OidcSamlProviderSeed.sql). The API reads the names
   regardless of case and the last copy wins, so it saw the empty seed
   values: the Deployment page reported "Okta (SAML) is missing required
   fields" and SAML sign-in would have used blank settings.

   For every OIDC/SAML row whose settings are JSON and have a duplicate or a
   non-camelCase name, this rewrites them with ONE entry per setting (names
   compared regardless of case), named in camelCase as the admin page writes
   them, keeping a filled-in value over an empty one. Values are otherwise
   untouched. The page now merges names the same way
   (App/Web/src/utils/providerSettings.js), and script 12 now seeds
   camelCase names.

   Not audit-logged: web.audit_event needs a signed-in user, and this runs
   from the Migrator.
   ============================================================================ */

USE $DatabaseName$;
GO

DECLARE @Cleaned INT;

;WITH Settings AS (
    SELECT c.ProviderKey,
           k.[key] COLLATE Latin1_General_BIN2 AS Name,
           k.[value] AS Value,
           k.[type] AS JsonType,
           LOWER(k.[key]) AS NameKey,
           (LOWER(LEFT(k.[key], 1)) + SUBSTRING(k.[key], 2, 4000)) COLLATE Latin1_General_BIN2 AS CamelName,
           CASE WHEN k.[type] = 0 OR (k.[type] = 1 AND LTRIM(RTRIM(k.[value])) = '') THEN 0 ELSE 1 END AS IsFilled
    FROM web.identity_provider_config c
    CROSS APPLY OPENJSON(c.ConfigurationValues) k
    WHERE c.ProviderType IN ('OIDC', 'SAML') AND ISJSON(c.ConfigurationValues) = 1
),
NeedsCleaning AS (
    SELECT ProviderKey
    FROM Settings
    GROUP BY ProviderKey
    HAVING COUNT(*) > COUNT(DISTINCT NameKey)
        OR SUM(CASE WHEN Name <> CamelName THEN 1 ELSE 0 END) > 0
),
Ranked AS (
    -- Per setting: a filled-in value first, then the camelCase spelling.
    SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.ProviderKey, s.NameKey
                                   ORDER BY s.IsFilled DESC, CASE WHEN s.Name = s.CamelName THEN 0 ELSE 1 END, s.Name) AS Pick
    FROM Settings s
    JOIN NeedsCleaning n ON n.ProviderKey = s.ProviderKey
),
Rebuilt AS (
    SELECT ProviderKey,
           N'{' + STRING_AGG(CAST(N'"' + STRING_ESCAPE(CamelName, 'json') + N'":'
                -- STRING_ESCAPE also writes / as \/ (valid, but needless): undo just that.
                + CASE JsonType WHEN 1 THEN N'"' + REPLACE(STRING_ESCAPE(Value, 'json'), N'\/', N'/') + N'"'
                                WHEN 0 THEN N'null'
                                ELSE Value END AS NVARCHAR(MAX)), N',')
             WITHIN GROUP (ORDER BY CamelName) + N'}' AS CleanJson
    FROM Ranked
    WHERE Pick = 1
    GROUP BY ProviderKey
)
UPDATE c
SET ConfigurationValues = r.CleanJson
FROM web.identity_provider_config c
JOIN Rebuilt r ON r.ProviderKey = c.ProviderKey;

SET @Cleaned = @@ROWCOUNT;
PRINT CONCAT('49_BlueTrack_ProviderSettingsDedupe.sql complete: ', @Cleaned, ' provider(s) cleaned.');
GO
