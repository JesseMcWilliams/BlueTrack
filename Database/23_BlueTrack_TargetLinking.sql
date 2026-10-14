/* ============================================================================
   23_BlueTrack_TargetLinking.sql

   RUN THIS AFTER 01-22. Guarded -- safe to re-run.

   D-229: linked targets, like linked feed accounts (D-212). A Target Match
   Review row (an imported target whose only match is an IP address another
   target already has) is resolved as Link, New Target or Ignore. Link makes
   the imported target its own web.dim_target, LINKED to the existing
   (primary) target; the shared IP address stays on the primary, so
   web.target_identifier (UQ_target_identifier) is unchanged. This replaces
   D-225's "move the IP address" (Merged), which could take an address away
   from a target the nightly load derives from CyberArk account addresses.

   - web.target_link: one row per linked target (LinkedTargetKey) naming its
     primary. Unlink keeps the row with UnlinkedDate/UnlinkedBy set, so the
     pair is never linked again automatically.
   - web.vw_linked_target: the targets linked now (not unlinked).
   - web.target_match_review: the incoming row's own data (TargetName,
     TargetTypeKey, RiskScore, Description, DiscoverySource), the target a
     resolution created or linked (ResolvedTargetKey), Resolution 'Linked'
     (old rows keep 'Merged'), and NameMatchKey/SourceMatchKey (lowercase,
     trimmed name and source) for "the same row" checks. Pending duplicates
     (same identifier, source and name) are deleted, keeping the oldest, and
     a filtered unique index stops new ones.
   - web.target_match_review_identifier: every identifier the incoming row
     had (Hostname, FQDN, ...), so Link and New Target can give them to the
     target they create.
   - web.ufn_ReachableRiskValues and ufn_ReachableRiskValues_ForAccessGroupSet:
     a linked target counts as its primary, at the primary's risk score, so a
     pair counts once.
   ============================================================================ */

USE $DatabaseName$;
GO

-- The incoming row's own data on the review row.
IF COL_LENGTH('web.target_match_review', 'TargetName') IS NULL
    ALTER TABLE web.target_match_review ADD TargetName NVARCHAR(300) NULL;
GO
IF COL_LENGTH('web.target_match_review', 'TargetTypeKey') IS NULL
    ALTER TABLE web.target_match_review ADD TargetTypeKey INT NULL
        CONSTRAINT FK_target_match_review_TargetType REFERENCES web.dim_target_type(TargetTypeKey);
GO
IF COL_LENGTH('web.target_match_review', 'RiskScore') IS NULL
    ALTER TABLE web.target_match_review ADD RiskScore INT NULL
        CONSTRAINT CK_target_match_review_RiskScore CHECK (RiskScore IS NULL OR RiskScore BETWEEN 0 AND 1000);
GO
IF COL_LENGTH('web.target_match_review', 'Description') IS NULL
    ALTER TABLE web.target_match_review ADD Description NVARCHAR(1000) NULL;
GO
IF COL_LENGTH('web.target_match_review', 'DiscoverySource') IS NULL
    ALTER TABLE web.target_match_review ADD DiscoverySource NVARCHAR(100) NULL;
GO
-- The target Link or New Target created (cleared if that target is deleted).
IF COL_LENGTH('web.target_match_review', 'ResolvedTargetKey') IS NULL
    ALTER TABLE web.target_match_review ADD ResolvedTargetKey INT NULL
        CONSTRAINT FK_target_match_review_ResolvedTarget REFERENCES web.dim_target(TargetKey) ON DELETE SET NULL;
GO
-- "The same row": source and name compared ignoring case and surrounding spaces.
IF COL_LENGTH('web.target_match_review', 'NameMatchKey') IS NULL
    ALTER TABLE web.target_match_review ADD NameMatchKey AS LOWER(LTRIM(RTRIM(TargetName))) PERSISTED;
GO
IF COL_LENGTH('web.target_match_review', 'SourceMatchKey') IS NULL
    ALTER TABLE web.target_match_review ADD SourceMatchKey AS LOWER(LTRIM(RTRIM(ISNULL(DiscoverySource, N'')))) PERSISTED;
GO

-- 'Linked' replaces 'Merged' (kept for rows resolved before D-229).
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID('web.target_match_review')
                 AND name = 'CK_target_match_review_Resolution' AND definition LIKE '%Linked%')
BEGIN
    IF OBJECT_ID('web.CK_target_match_review_Resolution', 'C') IS NOT NULL
        ALTER TABLE web.target_match_review DROP CONSTRAINT CK_target_match_review_Resolution;
    ALTER TABLE web.target_match_review ADD CONSTRAINT CK_target_match_review_Resolution
        CHECK (Resolution IS NULL OR Resolution IN ('Merged', 'Linked', 'NewTarget', 'Ignored'));
END
GO

IF OBJECT_ID('web.target_match_review_identifier', 'U') IS NULL
    CREATE TABLE web.target_match_review_identifier (
        TargetMatchReviewKey  INT           NOT NULL REFERENCES web.target_match_review(TargetMatchReviewKey) ON DELETE CASCADE,
        IdentifierType        NVARCHAR(50)  NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
        IdentifierValue       NVARCHAR(300) NOT NULL,
        CONSTRAINT PK_target_match_review_identifier PRIMARY KEY (TargetMatchReviewKey, IdentifierType, IdentifierValue)
    );
GO

-- Pending duplicates (the same identifier, source and name): keep the oldest.
;WITH ranked AS (
    SELECT TargetMatchReviewKey,
           ROW_NUMBER() OVER (PARTITION BY IdentifierType, IdentifierValue, SourceMatchKey, NameMatchKey
                              ORDER BY CreatedDate, TargetMatchReviewKey) AS rn
    FROM web.target_match_review
    WHERE Resolution IS NULL
)
DELETE r
FROM web.target_match_review r
JOIN ranked d ON d.TargetMatchReviewKey = r.TargetMatchReviewKey
WHERE d.rn > 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('web.target_match_review') AND name = 'UX_target_match_review_Pending')
    CREATE UNIQUE NONCLUSTERED INDEX UX_target_match_review_Pending
        ON web.target_match_review (IdentifierType, IdentifierValue, SourceMatchKey, NameMatchKey)
        WHERE Resolution IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('web.target_match_review') AND name = 'IX_target_match_review_Identifier')
    CREATE NONCLUSTERED INDEX IX_target_match_review_Identifier
        ON web.target_match_review (IdentifierType, IdentifierValue)
        INCLUDE (Resolution, SourceMatchKey, NameMatchKey, ResolvedTargetKey);
GO

IF OBJECT_ID('web.target_link', 'U') IS NULL
    CREATE TABLE web.target_link (
        LinkedTargetKey       INT           NOT NULL PRIMARY KEY REFERENCES web.dim_target(TargetKey),
        PrimaryTargetKey      INT           NOT NULL REFERENCES web.dim_target(TargetKey),
        LinkMethod            NVARCHAR(30)  NOT NULL,
        TargetMatchReviewKey  INT           NULL REFERENCES web.target_match_review(TargetMatchReviewKey) ON DELETE SET NULL,
        LinkedBy              INT           NULL REFERENCES web.app_user(UserKey),
        LinkedDate            DATETIME2     NOT NULL CONSTRAINT DF_target_link_LinkedDate DEFAULT SYSUTCDATETIME(),
        UnlinkedBy            INT           NULL REFERENCES web.app_user(UserKey),
        UnlinkedDate          DATETIME2     NULL,
        CONSTRAINT CK_target_link_NotSelf CHECK (LinkedTargetKey <> PrimaryTargetKey),
        CONSTRAINT CK_target_link_Method CHECK (LinkMethod IN ('MatchReview'))
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('web.target_link') AND name = 'IX_target_link_Primary')
    CREATE NONCLUSTERED INDEX IX_target_link_Primary ON web.target_link (PrimaryTargetKey) INCLUDE (UnlinkedDate);
GO

CREATE OR ALTER VIEW web.vw_linked_target AS
SELECT l.LinkedTargetKey AS TargetKey, l.PrimaryTargetKey, l.LinkMethod, l.LinkedDate, l.LinkedBy
FROM web.target_link l
WHERE l.UnlinkedDate IS NULL;
GO


-- As in 06_BlueTrack_Baseline_WebLogic.sql, except that each reachable
-- target is replaced by its primary when it's linked (D-229): a linked pair
-- counts once, at the primary's RiskScore.
CREATE OR ALTER FUNCTION web.ufn_ReachableRiskValues (@TargetSetKey NVARCHAR(20), @EntityKey BIGINT)
RETURNS TABLE
AS
RETURN
(
    WITH ReachableRawTargetKeys AS (
        -- Account: every Target reachable through any Access Group it belongs to...
        SELECT agtm.TargetKey
        FROM web.account_access_group_map aagm
        JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey

        UNION
        -- ...plus, for any Local-scope group it belongs to, the target that group itself lives on...
        SELECT ag.FoundOnTargetKey
        FROM web.account_access_group_map aagm
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey AND ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL

        UNION
        -- ...plus every Target reached directly, bypassing any group.
        SELECT atm.TargetKey
        FROM web.account_target_map atm
        WHERE @TargetSetKey = 'Account' AND atm.AccountKey = @EntityKey

        UNION
        -- Access Group: every Target it's mapped to...
        SELECT agtm.TargetKey
        FROM web.access_group_target_map agtm
        WHERE @TargetSetKey = 'AccessGroup' AND agtm.AccessGroupKey = @EntityKey

        UNION
        -- ...plus, if Local scope, the target it lives on.
        SELECT ag.FoundOnTargetKey
        FROM web.dim_access_group ag
        WHERE @TargetSetKey = 'AccessGroup' AND ag.AccessGroupKey = @EntityKey AND ag.GroupScope = 'Local' AND ag.FoundOnTargetKey IS NOT NULL
    ),
    ReachableTargetKeys AS (
        -- D-229: a linked target is its primary.
        SELECT DISTINCT COALESCE(lt.PrimaryTargetKey, r.TargetKey) AS TargetKey
        FROM ReachableRawTargetKeys r
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = r.TargetKey
    ),
    ReachableGroupKeys AS (
        -- Account: every Access Group it belongs to, for that group's own BaseRiskScore.
        SELECT DISTINCT ag.AccessGroupKey
        FROM web.account_access_group_map aagm
        JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey
        WHERE @TargetSetKey = 'Account' AND aagm.AccountKey = @EntityKey

        UNION
        -- Access Group: itself, for its own BaseRiskScore.
        SELECT @EntityKey WHERE @TargetSetKey = 'AccessGroup'
    )
    SELECT 'Target' AS EntityType, t.TargetKey AS EntityKey, t.TargetName AS EntityName, t.RiskScore AS RiskValue
    FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT 'AccessGroup' AS EntityType, ag.AccessGroupKey AS EntityKey, ag.GroupName AS EntityName, ag.BaseRiskScore AS RiskValue
    FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
);
GO

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
        -- D-229: a linked target is its primary.
        SELECT DISTINCT COALESCE(lt.PrimaryTargetKey, r.TargetKey) AS TargetKey
        FROM ReachableRawTargetKeys r
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = r.TargetKey
    ),
    ReachableGroupKeys AS (
        SELECT DISTINCT AccessGroupKey FROM @AccessGroupKeys
    )
    SELECT t.RiskScore AS RiskValue FROM ReachableTargetKeys rtk JOIN web.dim_target t ON t.TargetKey = rtk.TargetKey
    UNION ALL
    SELECT ag.BaseRiskScore FROM ReachableGroupKeys rgk JOIN web.dim_access_group ag ON ag.AccessGroupKey = rgk.AccessGroupKey
);
GO

PRINT '23_BlueTrack_TargetLinking.sql complete.';
GO
