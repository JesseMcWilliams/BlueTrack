/* ============================================================================
   25_BlueTrack_RiskScoreBands.sql

   RUN THIS AFTER 01-24. D-120: the original 2026-09-09 ask ("configure risk
   level names, map them onto risk scores") finally unblocked now that
   D-119 built a real computed 0-1000 numeric score
   (web.account_risk_score.EffectiveRiskScore). This is a brand-new,
   admin-configurable lookup of named bands over that computed score --
   deliberately NOT tied to dbo.dim_risk_level (the pre-existing, manually
   assigned Low/Medium/High/Critical label with no numeric score, which
   stays exactly as-is -- see Design_Risk_Scoring.md's own explicit note
   that these are two different concepts).

   Follows this project's established drop-and-recreate pattern for a new
   feature's own schema script (see 20_BlueTrack_RiskScoringSchema.sql).
   ============================================================================ */

USE $DatabaseName$;
GO

-- web.dim_risk_score_band: an ordered list of named ranges over
-- EffectiveRiskScore -- mirrors this app's existing dim_risk_level-style
-- "controlled list with a RiskOrder column" pattern. RiskOrder (not
-- BandName) is what sort columns elsewhere key off of, matching
-- dim_risk_level.RiskOrder's own precedent.
IF OBJECT_ID('web.dim_risk_score_band', 'U') IS NOT NULL DROP TABLE web.dim_risk_score_band;
CREATE TABLE web.dim_risk_score_band (
    RiskScoreBandKey  INT IDENTITY(1,1) PRIMARY KEY,
    BandName          NVARCHAR(50)  NOT NULL,
    MinScore          INT NOT NULL,
    MaxScore          INT NOT NULL,
    RiskOrder         INT NOT NULL,
    ModifiedBy        INT NULL REFERENCES web.app_user(UserKey),
    ModifiedDate      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_dim_risk_score_band_range CHECK (MinScore <= MaxScore)
);

INSERT INTO web.dim_risk_score_band (BandName, MinScore, MaxScore, RiskOrder) VALUES
    ('Low', 0, 250, 10),
    ('Medium', 251, 500, 20),
    ('High', 501, 750, 30),
    ('Critical', 751, 1000, 40);
GO

-- New permission, added after 09_BlueTrack_WebSeed.sql's own one-time
-- blanket grant already ran -- explicit catalog insert + explicit grant to
-- the bootstrap Admin role, same as every permission added since
-- (D-98/D-115/D-118/D-119).
IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageRiskScoreBands')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageRiskScoreBands', 'Manage the named bands that translate a computed risk score into a label');
END
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ManageRiskScoreBandsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageRiskScoreBands');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ManageRiskScoreBandsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ManageRiskScoreBandsKey);
END
GO

PRINT '25_BlueTrack_RiskScoreBands.sql complete.';
