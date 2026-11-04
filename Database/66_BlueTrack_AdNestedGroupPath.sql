/* ============================================================================
   66_BlueTrack_AdNestedGroupPath.sql

   RUN THIS AFTER 01-65. Guarded -- safe to re-run.

   - D-333: "Via AD group" names the groups between a Safe's member group and
     the person, when the person is in a nested group: the AD read stores the
     shortest chain of nested groups for each user (NestedPath, outermost
     first, joined with ' > '); NULL for a direct member, and for members
     read before this script until the next pull reads them again.
   - Copies usp_Apply_AdGroupMembers and vw_safe_access_people from
     48_BlueTrack_SelfHostedDirectExtras.sql (their latest versions) and adds
     NestedPath. AccessPath widens to NVARCHAR(1300) for the longer text.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.stg_shd_ad_group_members', 'NestedPath') IS NULL
    ALTER TABLE dbo.stg_shd_ad_group_members ADD NestedPath NVARCHAR(1000) NULL;
GO

IF COL_LENGTH('dbo.ad_group_member', 'NestedPath') IS NULL
    ALTER TABLE dbo.ad_group_member ADD NestedPath NVARCHAR(1000) NULL;
GO

/* As in 48_BlueTrack_SelfHostedDirectExtras.sql, plus NestedPath (D-333). */
CREATE OR ALTER PROCEDURE dbo.usp_Apply_AdGroupMembers
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.stg_shd_ad_group_reads) RETURN;
    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

    SELECT r.Status, r.DomainName, r.MemberCount, r.Message, r.GroupSource, r.GroupRef, g.GroupKey,
           CASE r.GroupSource WHEN 'SELFHOSTED' THEN N'Dn' ELSE N'Name' END AS MatchedBy
    INTO #reads
    FROM dbo.stg_shd_ad_group_reads r
    JOIN dbo.dim_source_system s ON s.SourceSystemCode = r.GroupSource
    JOIN dbo.dim_group g ON g.SourceSystemKey = s.SourceSystemKey
                        AND ((r.GroupSource = 'SELFHOSTED' AND g.SourceGroupId = r.GroupRef) OR (r.GroupSource = 'PRIVCLOUD' AND g.GroupName = r.GroupRef));

    DELETE m FROM dbo.ad_group_member m
    WHERE NOT EXISTS (SELECT 1 FROM #reads r WHERE r.GroupKey = m.GroupKey AND r.Status = 'Failed');

    INSERT INTO dbo.ad_group_member (GroupKey, DomainName, SamAccountName, DistinguishedName, ObjectSid, DisplayName, IsEnabled, NestedPath, LoadedAt)
    SELECT DISTINCT r.GroupKey, m.DomainName, m.SamAccountName, m.DistinguishedName, m.ObjectSid, m.DisplayName, m.IsEnabled, m.NestedPath, @Now
    FROM dbo.stg_shd_ad_group_members m
    JOIN #reads r ON r.GroupSource = m.GroupSource AND r.GroupRef = m.GroupRef AND r.Status = 'Found'
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ad_group_member x WHERE x.GroupKey = r.GroupKey AND x.DomainName = m.DomainName AND x.SamAccountName = m.SamAccountName);

    DELETE g FROM dbo.ad_group_read g WHERE NOT EXISTS (SELECT 1 FROM #reads r WHERE r.GroupKey = g.GroupKey);
    MERGE dbo.ad_group_read AS tgt
    USING (SELECT DISTINCT GroupKey, MatchedBy, Status, DomainName, MemberCount, Message FROM #reads) AS src ON tgt.GroupKey = src.GroupKey
    WHEN MATCHED THEN
        UPDATE SET MatchedBy = src.MatchedBy, Status = src.Status, Message = src.Message, LastAttemptAt = @Now,
                   DomainName = CASE WHEN src.Status = 'Failed' THEN tgt.DomainName ELSE src.DomainName END,
                   MemberCount = CASE WHEN src.Status = 'Failed' THEN tgt.MemberCount ELSE src.MemberCount END,
                   LastFoundAt = CASE WHEN src.Status = 'Found' THEN @Now WHEN src.Status = 'NotFound' THEN NULL ELSE tgt.LastFoundAt END
    WHEN NOT MATCHED THEN
        INSERT (GroupKey, MatchedBy, Status, DomainName, MemberCount, Message, LastAttemptAt, LastFoundAt)
        VALUES (src.GroupKey, src.MatchedBy, src.Status, src.DomainName, src.MemberCount, src.Message, @Now,
                CASE WHEN src.Status = 'Found' THEN @Now END);
END
GO

/* As in 48_BlueTrack_SelfHostedDirectExtras.sql; an AD user's AccessPath adds the nested groups (D-333). */
CREATE OR ALTER VIEW dbo.vw_safe_access_people AS
SELECT fse.SafeKey, fse.EntitlementKey, u.UserKey AS VaultUserKey, u.Username AS PersonName,
       CAST(NULL AS NVARCHAR(256)) AS DomainName, CAST(NULL AS NVARCHAR(256)) AS SamAccountName,
       NULLIF(LTRIM(CONCAT(u.FirstName, N' ', u.LastName)), N'') AS DisplayName, CAST(1 AS BIT) AS IsEnabled,
       CAST(N'Direct' AS NVARCHAR(1300)) AS AccessPath, u.UserType
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_user u ON u.UserKey = fse.UserKey
WHERE fse.MemberType = 'User'
UNION ALL
SELECT fse.SafeKey, fse.EntitlementKey, u.UserKey, u.Username, NULL, NULL,
       NULLIF(LTRIM(CONCAT(u.FirstName, N' ', u.LastName)), N''), CAST(1 AS BIT), CAST(N'Via Vault group: ' + g.GroupName AS NVARCHAR(300)), u.UserType
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
JOIN dbo.bridge_group_membership bgm ON bgm.GroupKey = g.GroupKey
JOIN dbo.dim_user u ON u.UserKey = bgm.UserKey
WHERE fse.MemberType = 'Group'
  AND NOT EXISTS (SELECT 1 FROM dbo.ad_group_read r WHERE r.GroupKey = g.GroupKey AND r.LastFoundAt IS NOT NULL)
UNION ALL
SELECT fse.SafeKey, fse.EntitlementKey, NULL, m.DomainName + N'\' + m.SamAccountName, m.DomainName, m.SamAccountName,
       m.DisplayName, m.IsEnabled, CAST(N'Via AD group: ' + g.GroupName + ISNULL(N' > ' + m.NestedPath, N'') AS NVARCHAR(1300)), NULL
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
JOIN dbo.ad_group_member m ON m.GroupKey = g.GroupKey
WHERE fse.MemberType = 'Group';
GO

PRINT '66_BlueTrack_AdNestedGroupPath.sql complete.';
GO
