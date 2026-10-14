/* ============================================================================
   03_BlueTrack_Test_TrendsHistorySeed.sql

   Test-only synthetic data (Design_Testing-Strategy.md) -- NOT part of the
   real environment's numbered-script sequence. Never run this against
   BlueTrack (or any real environment).

   RUN THIS AFTER 02_BlueTrack_Test_SyntheticAccountData.sql.

   WHAT THIS DOES (D-221): fourteen daily snapshots in
   dbo.fact_account_progress_history for TestAccount01-04, the 14 days
   before the day it runs (never today: usp_Load_FactAccountProgressHistory
   replaces today's snapshot, and a test runs it), so the Reports > Trends
   page has something to draw in the Playwright suite. The accounts move
   forward over the fortnight:
       TestAccount01  Discovered / Not Started, no risk level
       TestAccount02  Discovered / Not Started, no risk level (TestSafe01_Pending)
       TestAccount03  Assessed / Prioritized / In Progress, High; Onboarded
                      to Vault for the last 7 days
       TestAccount04  Onboarded to Vault / In Progress, Medium; Managed /
                      Rotation Enabled from 10 days ago; Verified /
                      Compliant / Complete for the last 3 days

   The xUnit Trends tests seed their own snapshots on fixed 2026 February
   dates and remove them; they don't rely on these rows.

   Guarded -- safe to re-run (a snapshot already there is left alone).
   ============================================================================ */

USE $DatabaseName$;
GO

DECLARE @Discovered INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageOrder = 1);
DECLARE @Assessed INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageOrder = 2);
DECLARE @Onboarded INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageOrder = 3);
DECLARE @Managed INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageOrder = 4);
DECLARE @Compliant INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageOrder = 5);
DECLARE @NotStarted INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');
DECLARE @InProgress INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'In Progress');
DECLARE @Complete INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Complete');
DECLARE @Medium INT = (SELECT RiskLevelKey FROM dim_risk_level WHERE RiskLevelName = 'Medium');
DECLARE @High INT = (SELECT RiskLevelKey FROM dim_risk_level WHERE RiskLevelName = 'High');

;WITH days AS (
    SELECT v.DaysAgo, CAST(FORMAT(DATEADD(DAY, -v.DaysAgo, CAST(GETDATE() AS DATE)), 'yyyyMMdd') AS INT) AS DateKey
    FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14)) AS v(DaysAgo)
),
snap AS (
    SELECT d.DateKey, fa.AccountKey,
        CASE fa.SourceAccountId
            WHEN 'TestAccount03' THEN CASE WHEN d.DaysAgo <= 7 THEN @Onboarded ELSE @Assessed END
            WHEN 'TestAccount04' THEN CASE WHEN d.DaysAgo <= 3 THEN @Compliant WHEN d.DaysAgo <= 10 THEN @Managed ELSE @Onboarded END
            ELSE @Discovered END AS StageKey,
        CASE fa.SourceAccountId
            WHEN 'TestAccount03' THEN @InProgress
            WHEN 'TestAccount04' THEN CASE WHEN d.DaysAgo <= 3 THEN @Complete ELSE @InProgress END
            ELSE @NotStarted END AS StatusKey,
        CASE fa.SourceAccountId WHEN 'TestAccount03' THEN @High WHEN 'TestAccount04' THEN @Medium END AS RiskLevelKey
    FROM days d
    CROSS JOIN fact_account fa
    WHERE fa.SourceAccountId IN ('TestAccount01', 'TestAccount02', 'TestAccount03', 'TestAccount04')
      AND fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY')
      AND EXISTS (SELECT 1 FROM dim_date dd WHERE dd.DateKey = d.DateKey)
)
INSERT INTO fact_account_progress_history (SnapshotDateKey, AccountKey, StageKey, StatusKey, RiskLevelKey)
SELECT s.DateKey, s.AccountKey, s.StageKey, s.StatusKey, s.RiskLevelKey
FROM snap s
WHERE NOT EXISTS (
    SELECT 1 FROM fact_account_progress_history h WHERE h.SnapshotDateKey = s.DateKey AND h.AccountKey = s.AccountKey
);
GO

PRINT 'Synthetic Trends history seeded (TestAccount01-04, the 14 days before today).';
