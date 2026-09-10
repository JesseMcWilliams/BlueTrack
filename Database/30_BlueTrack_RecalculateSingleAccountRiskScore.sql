/* ============================================================================
   30_BlueTrack_RecalculateSingleAccountRiskScore.sql

   RUN THIS AFTER 01-29. Safe to re-run (CREATE OR ALTER for the procedure --
   pure logic, no data of its own, unlike a table).

   D-131: requested directly -- a "Recalculate" button on the Account
   Progress edit screen's Risk Score tab (D-130), scoped to just the one
   account being edited, not the whole system. usp_RecalculateRiskScores
   (Database/23) already exists for a bulk, IsRiskScoreStale-filtered
   refresh; this is a single-account variant that recalculates regardless
   of the stale flag (the user explicitly asked for it right now, not "only
   if it happens to be marked stale") -- reuses the same usp_CalculateRiskScore
   dispatcher (Database/23) that both Phase D algorithms sit behind, so this
   is guaranteed to produce the exact same number the bulk recalculation
   would. MERGE (not a plain UPDATE) mirrors usp_DeriveAccountTargetMap_
   FromPendingSafes' own precedent (Database/22) for lazily creating a
   web.account_risk_score row if this specific account doesn't have one yet.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER PROCEDURE dbo.usp_RecalculateRiskScoreForAccount
    @AccountKey BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AccountScore INT;
    EXEC usp_CalculateRiskScore 'Account', @AccountKey, @AccountScore OUTPUT;

    MERGE web.account_risk_score AS tgt
    USING (SELECT @AccountKey AS AccountKey) AS src
    ON tgt.AccountKey = src.AccountKey
    WHEN MATCHED THEN
        UPDATE SET ComputedRiskScore = @AccountScore, RiskScoreCalculatedDate = SYSUTCDATETIME(), IsRiskScoreStale = 0
    WHEN NOT MATCHED THEN
        INSERT (AccountKey, ComputedRiskScore, RiskScoreCalculatedDate, IsRiskScoreStale)
        VALUES (@AccountKey, @AccountScore, SYSUTCDATETIME(), 0);
END
GO

PRINT '30_BlueTrack_RecalculateSingleAccountRiskScore.sql complete.';
