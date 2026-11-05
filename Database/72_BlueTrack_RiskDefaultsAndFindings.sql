/* ============================================================================
   72_BlueTrack_RiskDefaultsAndFindings.sql

   RUN THIS AFTER 01-71. Guarded -- safe to re-run.

   D-349 (the user, 2026-11-05: "Rename Risk Score Bands to just Risk Score.
   Back Bands a sub tab and add a new sub tab for Defaults. This is where the
   default scores will be set." and "If a system has a hardcoded secret then
   its risk score should increase, if the hard coded secret is also
   considered privileged then it should have a bigger increase."; the user's
   choices: a finding is a risk value in the algorithm; a hard-coded secret
   raises both the system it was found on and the account it names;
   privileged when DNA or BlueTrack says so; Defaults hold targets, groups
   and findings):
   - web.app_config: DefaultAddressTargetRiskScore (a target made from an
     account's address, was 0), DefaultTargetRiskScore and
     DefaultAccessGroupRiskScore (a new target's and group's starting score
     on their forms). 0-1000, default 0, so nothing changes until set.
   - web.risk_finding_type: each kind of finding and its risk value (0-1000)
     and whether it counts. Seeded: DnaHardCodedSecret 300,
     DnaHardCodedPrivilegedSecret 600, DnaPassTheHash 400, DnaNonCompliant 200.
   - web.risk_finding: the findings now, rebuilt by web.usp_DeriveRiskFindings
     (after a DNA import, and first thing in usp_RecalculateRiskScores):
       * a hard-coded credential (web.dna_hard_coded_credential) on the
         target whose name or identifier is its Machine Name (full or short
         name, either way), and on each BlueTrack account with its user name
         (a DOMAIN\ prefix ignored) whose address is its Target System
         Address (a :port dropped; full or short name). Privileged when DNA
         lists that user on the target system or on the machine as
         Privileged... (Account Category) or in a privileged domain group, or
         when a matched account's AD account is classed Admin (D-233);
       * a DNA account linked to a BlueTrack account (D-346) that is
         Pass-the-Hash vulnerable, or Non-compliant.
     When the set changes, every score is marked stale.
   - web.ufn_ReachableRiskValues (and _ForAccessGroupSet) also return, as
     EntityType 'Finding', one value per kind of finding per reachable
     target (a linked target counted as its primary, D-229) and, for an
     account, per kind on the account itself. So a finding goes through the
     active algorithm like any target, and Reports > Risk Score lists it.
   - usp_DeriveTargetsFromAccountAddress uses DefaultAddressTargetRiskScore.
   - Audit type RiskDefaultsChanged.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'DefaultAddressTargetRiskScore') IS NULL
    ALTER TABLE web.app_config ADD
        DefaultAddressTargetRiskScore INT NOT NULL CONSTRAINT DF_app_config_DefaultAddressTargetRiskScore DEFAULT 0
            CONSTRAINT CK_app_config_DefaultAddressTargetRiskScore CHECK (DefaultAddressTargetRiskScore BETWEEN 0 AND 1000),
        DefaultTargetRiskScore INT NOT NULL CONSTRAINT DF_app_config_DefaultTargetRiskScore DEFAULT 0
            CONSTRAINT CK_app_config_DefaultTargetRiskScore CHECK (DefaultTargetRiskScore BETWEEN 0 AND 1000),
        DefaultAccessGroupRiskScore INT NOT NULL CONSTRAINT DF_app_config_DefaultAccessGroupRiskScore DEFAULT 0
            CONSTRAINT CK_app_config_DefaultAccessGroupRiskScore CHECK (DefaultAccessGroupRiskScore BETWEEN 0 AND 1000);
GO

IF OBJECT_ID('web.risk_finding_type') IS NULL
    CREATE TABLE web.risk_finding_type (
        FindingType   NVARCHAR(50)  NOT NULL CONSTRAINT PK_risk_finding_type PRIMARY KEY,
        DisplayName   NVARCHAR(100) NOT NULL,
        Description   NVARCHAR(500) NOT NULL,
        SortOrder     INT           NOT NULL,
        RiskValue     INT           NOT NULL CONSTRAINT CK_risk_finding_type_RiskValue CHECK (RiskValue BETWEEN 0 AND 1000),
        IsEnabled     BIT           NOT NULL CONSTRAINT DF_risk_finding_type_IsEnabled DEFAULT 1
    );
GO

INSERT INTO web.risk_finding_type (FindingType, DisplayName, Description, SortOrder, RiskValue)
SELECT v.FindingType, v.DisplayName, v.Description, v.SortOrder, v.RiskValue
FROM (VALUES
    (N'DnaHardCodedSecret', N'Hard-coded secret', N'DNA found a hard-coded credential in a file: on the system it was found on, and on the account it names.', 1, 300),
    (N'DnaHardCodedPrivilegedSecret', N'Hard-coded privileged secret', N'As a hard-coded secret, for an account DNA lists as privileged (its Account Category or a privileged domain group) or whose AD account is classed Admin.', 2, 600),
    (N'DnaPassTheHash', N'Pass-the-Hash vulnerable', N'DNA marks the account Pass-the-Hash vulnerable (DNA account linked to this BlueTrack account).', 3, 400),
    (N'DnaNonCompliant', N'Non-compliant', N'DNA marks the account non-compliant with its password policy (DNA account linked to this BlueTrack account).', 4, 200)
) v(FindingType, DisplayName, Description, SortOrder, RiskValue)
WHERE NOT EXISTS (SELECT 1 FROM web.risk_finding_type t WHERE t.FindingType = v.FindingType);
GO

IF OBJECT_ID('web.risk_finding') IS NULL
BEGIN
    CREATE TABLE web.risk_finding (
        RiskFindingKey  BIGINT IDENTITY(1,1) CONSTRAINT PK_risk_finding PRIMARY KEY,
        FindingType     NVARCHAR(50) NOT NULL CONSTRAINT FK_risk_finding_Type REFERENCES web.risk_finding_type (FindingType),
        TargetKey       INT          NULL CONSTRAINT FK_risk_finding_Target REFERENCES web.dim_target (TargetKey) ON DELETE CASCADE,
        AccountKey      BIGINT       NULL CONSTRAINT FK_risk_finding_Account REFERENCES dbo.fact_account (AccountKey) ON DELETE CASCADE,
        DnaHardCodedKey INT          NULL,   -- no FK: the DNA rows are replaced by each import, then this is rebuilt
        DnaAccountKey   INT          NULL,
        CONSTRAINT CK_risk_finding_OnSomething CHECK (TargetKey IS NOT NULL OR AccountKey IS NOT NULL)
    );
    CREATE INDEX IX_risk_finding_Target ON web.risk_finding (TargetKey) INCLUDE (FindingType) WHERE TargetKey IS NOT NULL;
    CREATE INDEX IX_risk_finding_Account ON web.risk_finding (AccountKey) INCLUDE (FindingType) WHERE AccountKey IS NOT NULL;
END
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('RiskDefaultsChanged', 'Admin > Risk Score, Defaults: a default score or a finding''s risk value changed (D-349)')
) v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO


/* The findings now. Names compare without regard to case; a full name and a
   short one match either way (SRV01 and SRV01.corp.example), two different
   full names don't. */
CREATE OR ALTER PROCEDURE web.usp_DeriveRiskFindings
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BeforeCount INT, @BeforeSum INT;
    SELECT @BeforeCount = COUNT(*), @BeforeSum = CHECKSUM_AGG(CHECKSUM(FindingType, TargetKey, AccountKey)) FROM web.risk_finding;

    IF OBJECT_ID('tempdb..#hcc') IS NOT NULL DROP TABLE #hcc;
    CREATE TABLE #hcc (
        DnaHardCodedKey INT           NOT NULL PRIMARY KEY,
        Machine         NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        MachineShort    NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        UserName        NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        TargetAddress   NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        TargetShort     NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        IsPrivileged    BIT           NOT NULL DEFAULT 0
    );

    INSERT INTO #hcc (DnaHardCodedKey, Machine, MachineShort, UserName, TargetAddress, TargetShort)
    SELECT h.DnaHardCodedKey, m.Machine, LEFT(m.Machine, CHARINDEX(N'.', m.Machine + N'.') - 1),
           u.UserName, a.TargetAddress, LEFT(a.TargetAddress, CHARINDEX(N'.', a.TargetAddress + N'.') - 1)
    FROM web.dna_hard_coded_credential h
    CROSS APPLY (SELECT NULLIF(NULLIF(LTRIM(RTRIM(h.MachineName)), N''), N'N/A') AS Machine) m
    CROSS APPLY (SELECT NULLIF(NULLIF(LTRIM(RTRIM(h.AccountName)), N''), N'N/A') AS RawUser,
                        NULLIF(NULLIF(LTRIM(RTRIM(h.TargetSystemAddress)), N''), N'N/A') AS RawAddress) r
    CROSS APPLY (SELECT CASE WHEN CHARINDEX(N'\', r.RawUser) > 0 THEN SUBSTRING(r.RawUser, CHARINDEX(N'\', r.RawUser) + 1, 300) ELSE r.RawUser END AS UserName) u
    -- One colon is a port (SAIATEST:446); more is an IPv6 address, kept whole.
    CROSS APPLY (SELECT CASE WHEN LEN(r.RawAddress) - LEN(REPLACE(r.RawAddress, N':', N'')) = 1
                             THEN LEFT(r.RawAddress, CHARINDEX(N':', r.RawAddress) - 1) ELSE r.RawAddress END AS TargetAddress) a;

    -- The BlueTrack accounts each credential names.
    IF OBJECT_ID('tempdb..#hccAccount') IS NOT NULL DROP TABLE #hccAccount;
    SELECT DISTINCT h.DnaHardCodedKey, fa.AccountKey
    INTO #hccAccount
    FROM #hcc h
    JOIN dbo.fact_account fa
      ON fa.IsDeleted = 0 AND h.UserName IS NOT NULL AND h.TargetAddress IS NOT NULL
     AND fa.UserName COLLATE Latin1_General_CI_AS = h.UserName
     AND (   fa.Address COLLATE Latin1_General_CI_AS = h.TargetAddress
          OR (CHARINDEX(N'.', fa.Address) = 0 AND fa.Address COLLATE Latin1_General_CI_AS = h.TargetShort)
          OR (CHARINDEX(N'.', h.TargetAddress) = 0 AND LEFT(fa.Address, CHARINDEX(N'.', fa.Address + N'.') - 1) COLLATE Latin1_General_CI_AS = h.TargetAddress));

    -- Privileged: DNA says so for that user on the target system or on the machine, or the account's AD account is Admin.
    UPDATE h SET IsPrivileged = 1
    FROM #hcc h
    WHERE EXISTS (
            SELECT 1 FROM web.dna_account d
            CROSS APPLY (SELECT CASE WHEN CHARINDEX(N'\', d.AccountName) > 0 THEN SUBSTRING(d.AccountName, CHARINDEX(N'\', d.AccountName) + 1, 300) ELSE d.AccountName END AS UserName,
                                LEFT(d.MachineName, CHARINDEX(N'.', d.MachineName + N'.') - 1) AS MachineShort) k
            WHERE h.UserName IS NOT NULL
              AND k.UserName COLLATE Latin1_General_CI_AS = h.UserName
              AND (d.AccountCategory LIKE N'Privileged%' OR NULLIF(LTRIM(RTRIM(d.PrivilegedDomainGroup)), N'') IS NOT NULL)
              AND (   d.MachineName COLLATE Latin1_General_CI_AS IN (h.TargetAddress, h.Machine)
                   OR k.MachineShort COLLATE Latin1_General_CI_AS IN (h.TargetShort, h.MachineShort)
                      AND (CHARINDEX(N'.', d.MachineName) = 0 OR CHARINDEX(N'.', h.TargetAddress) = 0 OR CHARINDEX(N'.', h.Machine) = 0)))
       OR EXISTS (
            SELECT 1 FROM #hccAccount ha
            JOIN web.discovered_account da ON da.ResolvedAccountKey = ha.AccountKey
            WHERE ha.DnaHardCodedKey = h.DnaHardCodedKey AND COALESCE(da.ClassOverride, da.AccountClass) = N'Admin');

    -- Each target's names: its own and its identifiers.
    IF OBJECT_ID('tempdb..#targetName') IS NOT NULL DROP TABLE #targetName;
    SELECT DISTINCT n.TargetKey, n.Name COLLATE Latin1_General_CI_AS AS Name,
           LEFT(n.Name, CHARINDEX(N'.', n.Name + N'.') - 1) COLLATE Latin1_General_CI_AS AS NameShort
    INTO #targetName
    FROM (SELECT TargetKey, LTRIM(RTRIM(TargetName)) AS Name FROM web.dim_target
          UNION SELECT TargetKey, LTRIM(RTRIM(IdentifierValue)) FROM web.target_identifier) n
    WHERE n.Name <> N'';

    BEGIN TRANSACTION;

    DELETE FROM web.risk_finding;

    -- The system the credential was found on.
    INSERT INTO web.risk_finding (FindingType, TargetKey, DnaHardCodedKey)
    SELECT DISTINCT CASE WHEN h.IsPrivileged = 1 THEN N'DnaHardCodedPrivilegedSecret' ELSE N'DnaHardCodedSecret' END, tn.TargetKey, h.DnaHardCodedKey
    FROM #hcc h
    JOIN #targetName tn
      ON h.Machine IS NOT NULL
     AND (   tn.Name = h.Machine
          OR (CHARINDEX(N'.', tn.Name) = 0 AND tn.Name = h.MachineShort)
          OR (CHARINDEX(N'.', h.Machine) = 0 AND tn.NameShort = h.Machine));

    -- The account it names.
    INSERT INTO web.risk_finding (FindingType, AccountKey, DnaHardCodedKey)
    SELECT CASE WHEN h.IsPrivileged = 1 THEN N'DnaHardCodedPrivilegedSecret' ELSE N'DnaHardCodedSecret' END, ha.AccountKey, h.DnaHardCodedKey
    FROM #hccAccount ha JOIN #hcc h ON h.DnaHardCodedKey = ha.DnaHardCodedKey;

    -- DNA accounts linked to a BlueTrack account (D-346).
    INSERT INTO web.risk_finding (FindingType, AccountKey, DnaAccountKey)
    SELECT N'DnaPassTheHash', d.TrackedAccountKey, d.DnaAccountKey
    FROM web.dna_account d
    JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey AND fa.IsDeleted = 0
    WHERE d.PassTheHashVulnerable = N'Yes';

    INSERT INTO web.risk_finding (FindingType, AccountKey, DnaAccountKey)
    SELECT N'DnaNonCompliant', d.TrackedAccountKey, d.DnaAccountKey
    FROM web.dna_account d
    JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey AND fa.IsDeleted = 0
    WHERE d.ComplianceStatus LIKE N'Non-compliant%';

    -- An account with a finding of its own gets a score row, as usp_DeriveAccountTargetMap_FromPendingSafes does.
    MERGE web.account_risk_score AS tgt
    USING (SELECT DISTINCT AccountKey FROM web.risk_finding WHERE AccountKey IS NOT NULL) AS src (AccountKey)
    ON tgt.AccountKey = src.AccountKey
    WHEN NOT MATCHED THEN INSERT (AccountKey, IsRiskScoreStale) VALUES (src.AccountKey, 1);

    DECLARE @AfterCount INT, @AfterSum INT;
    SELECT @AfterCount = COUNT(*), @AfterSum = CHECKSUM_AGG(CHECKSUM(FindingType, TargetKey, AccountKey)) FROM web.risk_finding;
    IF @AfterCount <> @BeforeCount OR ISNULL(@AfterSum, 0) <> ISNULL(@BeforeSum, 0)
    BEGIN
        UPDATE web.dim_access_group SET IsRiskScoreStale = 1;
        UPDATE web.account_risk_score SET IsRiskScoreStale = 1;
    END

    COMMIT TRANSACTION;

    DROP TABLE #hcc;
    DROP TABLE #hccAccount;
    DROP TABLE #targetName;
END
GO


-- As in 23_BlueTrack_TargetLinking.sql, plus D-349's findings: one value per kind of finding per reachable target,
-- and, for an account, per kind on the account itself. A finding's target counts as its primary when it's linked.
CREATE OR ALTER FUNCTION web.ufn_ReachableRiskValues (@TargetSetKey NVARCHAR(20), @EntityKey BIGINT)
RETURNS TABLE
AS
RETURN
(
    WITH ReachableRawTargetKeys AS (
        SELECT agtm.TargetKey
        FROM web.account_access_group_map aagm
        JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey

        UNION
        SELECT ag.FoundOnTargetKey
        FROM web.account_access_group_map aagm
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey AND ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL

        UNION
        SELECT atm.TargetKey
        FROM web.account_target_map atm
        WHERE @TargetSetKey = 'Account' AND atm.AccountKey = @EntityKey

        UNION
        SELECT agtm.TargetKey
        FROM web.access_group_target_map agtm
        WHERE @TargetSetKey = 'AccessGroup' AND agtm.AccessGroupKey = @EntityKey

        UNION
        SELECT ag.FoundOnTargetKey
        FROM web.dim_access_group ag
        WHERE @TargetSetKey = 'AccessGroup' AND ag.AccessGroupKey = @EntityKey AND ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL
    ),
    ReachableTargetKeys AS (
        SELECT DISTINCT COALESCE(lt.PrimaryTargetKey, r.TargetKey) AS TargetKey
        FROM ReachableRawTargetKeys r
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = r.TargetKey
    ),
    ReachableGroupKeys AS (
        SELECT DISTINCT ag.AccessGroupKey
        FROM web.account_access_group_map aagm
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey

        UNION
        SELECT @EntityKey WHERE @TargetSetKey = 'AccessGroup'
    ),
    TargetFindings AS (
        SELECT rf.FindingType, COALESCE(lt.PrimaryTargetKey, rf.TargetKey) AS TargetKey, COUNT(*) AS FindingCount, MIN(rf.RiskFindingKey) AS RiskFindingKey
        FROM web.risk_finding rf
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = rf.TargetKey
        WHERE rf.TargetKey IS NOT NULL
        GROUP BY rf.FindingType, COALESCE(lt.PrimaryTargetKey, rf.TargetKey)
    ),
    AccountFindings AS (
        SELECT rf.FindingType, COUNT(*) AS FindingCount, MIN(rf.RiskFindingKey) AS RiskFindingKey
        FROM web.risk_finding rf
        WHERE @TargetSetKey = 'Account' AND rf.AccountKey = @EntityKey
        GROUP BY rf.FindingType
    )
    SELECT 'Target' AS EntityType, t.TargetKey AS EntityKey, t.TargetName AS EntityName, t.RiskScore AS RiskValue
    FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT 'AccessGroup' AS EntityType, ag.AccessGroupKey AS EntityKey, ag.GroupName AS EntityName, ag.BaseRiskScore AS RiskValue
    FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
    UNION ALL
    SELECT 'Finding', tf.RiskFindingKey,
           ft.DisplayName + N' on ' + t.TargetName + CASE WHEN tf.FindingCount > 1 THEN N' (' + CAST(tf.FindingCount AS NVARCHAR(10)) + N')' ELSE N'' END,
           ft.RiskValue
    FROM TargetFindings tf
    JOIN ReachableTargetKeys rtk ON rtk.TargetKey = tf.TargetKey
    JOIN web.dim_target t ON t.TargetKey = tf.TargetKey
    JOIN web.risk_finding_type ft ON ft.FindingType = tf.FindingType AND ft.IsEnabled = 1 AND ft.RiskValue > 0
    UNION ALL
    SELECT 'Finding', af.RiskFindingKey,
           ft.DisplayName + N' (this account' + CASE WHEN af.FindingCount > 1 THEN N', ' + CAST(af.FindingCount AS NVARCHAR(10)) ELSE N'' END + N')',
           ft.RiskValue
    FROM AccountFindings af
    JOIN web.risk_finding_type ft ON ft.FindingType = af.FindingType AND ft.IsEnabled = 1 AND ft.RiskValue > 0
);
GO

-- As in 23_BlueTrack_TargetLinking.sql, plus the findings on the reachable targets.
CREATE OR ALTER FUNCTION web.ufn_ReachableRiskValues_ForAccessGroupSet (@AccessGroupKeys web.AccessGroupKeyList READONLY)
RETURNS TABLE
AS
RETURN
(
    WITH ReachableRawTargetKeys AS (
        SELECT agtm.TargetKey
        FROM @AccessGroupKeys agk
        JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = agk.AccessGroupKey

        UNION
        SELECT ag.FoundOnTargetKey
        FROM @AccessGroupKeys agk
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = agk.AccessGroupKey
        WHERE ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL
    ),
    ReachableTargetKeys AS (
        SELECT DISTINCT COALESCE(lt.PrimaryTargetKey, r.TargetKey) AS TargetKey
        FROM ReachableRawTargetKeys r
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = r.TargetKey
    ),
    ReachableGroupKeys AS (
        SELECT DISTINCT AccessGroupKey FROM @AccessGroupKeys
    ),
    TargetFindings AS (
        SELECT DISTINCT rf.FindingType, COALESCE(lt.PrimaryTargetKey, rf.TargetKey) AS TargetKey
        FROM web.risk_finding rf
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = rf.TargetKey
        WHERE rf.TargetKey IS NOT NULL
    )
    SELECT t.RiskScore AS RiskValue FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT ag.BaseRiskScore FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
    UNION ALL
    SELECT ft.RiskValue
    FROM TargetFindings tf
    JOIN ReachableTargetKeys rtk ON rtk.TargetKey = tf.TargetKey
    JOIN web.risk_finding_type ft ON ft.FindingType = tf.FindingType AND ft.IsEnabled = 1 AND ft.RiskValue > 0
);
GO


-- As in 06_BlueTrack_Baseline_WebLogic.sql, with the findings brought up to date first (they can mark scores stale).
CREATE OR ALTER PROCEDURE usp_RecalculateRiskScores
AS
BEGIN
    SET NOCOUNT ON;

    EXEC web.usp_DeriveRiskFindings;   -- D-349

    DECLARE @AccessGroupKey INT, @GroupScore INT;
    DECLARE GroupCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT AccessGroupKey FROM web.dim_access_group WHERE IsRiskScoreStale = 1;
    OPEN GroupCursor;
    FETCH NEXT FROM GroupCursor INTO @AccessGroupKey;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC usp_CalculateRiskScore 'AccessGroup', @AccessGroupKey, @GroupScore OUTPUT;
        UPDATE web.dim_access_group
        SET ComputedRiskScore = @GroupScore, RiskScoreCalculatedDate = SYSUTCDATETIME(), IsRiskScoreStale = 0
        WHERE AccessGroupKey = @AccessGroupKey;
        FETCH NEXT FROM GroupCursor INTO @AccessGroupKey;
    END
    CLOSE GroupCursor;
    DEALLOCATE GroupCursor;

    DECLARE @AccountKey BIGINT, @AccountScore INT;
    DECLARE AccountCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT AccountKey FROM web.account_risk_score WHERE IsRiskScoreStale = 1;
    OPEN AccountCursor;
    FETCH NEXT FROM AccountCursor INTO @AccountKey;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC usp_CalculateRiskScore 'Account', @AccountKey, @AccountScore OUTPUT;
        UPDATE web.account_risk_score
        SET ComputedRiskScore = @AccountScore, RiskScoreCalculatedDate = SYSUTCDATETIME(), IsRiskScoreStale = 0
        WHERE AccountKey = @AccountKey;
        FETCH NEXT FROM AccountCursor INTO @AccountKey;
    END
    CLOSE AccountCursor;
    DEALLOCATE AccountCursor;
END
GO


-- As in 06_BlueTrack_Baseline_WebLogic.sql, with D-349's default score for a target made from an address.
CREATE OR ALTER PROCEDURE dbo.usp_DeriveTargetsFromAccountAddress
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OtherTypeKey INT = (SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Other');
    DECLARE @DefaultScore INT = COALESCE((SELECT TOP (1) DefaultAddressTargetRiskScore FROM web.app_config), 0);   -- D-349
    IF @OtherTypeKey IS NULL
    BEGIN
        RAISERROR('usp_DeriveTargetsFromAccountAddress: web.dim_target_type has no ''Other'' type (29_BlueTrack_TargetTypeDimension.sql seeds it).', 16, 1);
        RETURN;
    END

    -- Distinct, non-blank Addresses from active accounts, classified once as
    -- IPAddress (dotted-quad shape) or Hostname (everything else).
    IF OBJECT_ID('tempdb..#NewTargetAddresses') IS NOT NULL DROP TABLE #NewTargetAddresses;
    CREATE TABLE #NewTargetAddresses (
        Address        NVARCHAR(300) NOT NULL PRIMARY KEY,
        IdentifierType NVARCHAR(50)  NOT NULL
    );

    INSERT INTO #NewTargetAddresses (Address, IdentifierType)
    SELECT DISTINCT
        addr.Address,
        CASE
            WHEN addr.Address NOT LIKE '%[^0-9.]%'
                 AND LEN(addr.Address) - LEN(REPLACE(addr.Address, '.', '')) = 3
            THEN 'IPAddress'
            ELSE 'Hostname'
        END
    FROM (
        SELECT LTRIM(RTRIM(fa.Address)) AS Address
        FROM fact_account fa
        WHERE fa.IsDeleted = 0
          AND fa.Address IS NOT NULL
          AND LTRIM(RTRIM(fa.Address)) <> ''
    ) addr
    WHERE NOT EXISTS (SELECT 1 FROM web.target_identifier ti WHERE ti.IdentifierValue = addr.Address);

    DECLARE @NewTargets TABLE (TargetKey INT NOT NULL, TargetName NVARCHAR(300) NOT NULL);

    INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore, Description, DiscoverySource, ModifiedDate)
    OUTPUT inserted.TargetKey, inserted.TargetName INTO @NewTargets (TargetKey, TargetName)
    SELECT
        @OtherTypeKey,
        nta.Address,
        @DefaultScore,
        'Auto-generated from CyberArk account data (fact_account.Address). RiskScore is the default for such targets (Admin > Risk Score, Defaults) -- not yet analyst-reviewed.',
        'CyberArk ETL',
        SYSUTCDATETIME()
    FROM #NewTargetAddresses nta;

    INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue)
    SELECT nt.TargetKey, nta.IdentifierType, nt.TargetName
    FROM @NewTargets nt
    JOIN #NewTargetAddresses nta ON nta.Address = nt.TargetName;

    DROP TABLE #NewTargetAddresses;
END
GO


PRINT '72_BlueTrack_RiskDefaultsAndFindings.sql complete.';
GO
