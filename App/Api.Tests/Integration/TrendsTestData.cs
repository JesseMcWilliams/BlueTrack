using Dapper;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-221: the Trends tests' own snapshot history, on fixed February 2026
/// dates nothing else in BlueTrackTest uses (Database/Test/03 seeds the 14
/// days before it runs). One Application, two Safes (A mapped to it, B not)
/// and three accounts (A1, A2 in Safe A; B1 in Safe B), with snapshots on:
///
///   2026-02-02 (Mon)  A1 stage 1 Not Started, no risk  | A2 stage 2 In Progress, Low   | B1 stage 1 Not Started, High
///   2026-02-04 (Wed)  A1 stage 3 In Progress, Low      | A2 stage 5 Complete, Medium   | B1 stage 1 Risk Accepted / Excluded, High
///   2026-02-10 (Tue)  A1 stage 5 Complete, Low         | A2 stage 5 Complete, Medium   | B1 stage 5 In Progress, Critical
///
/// Seed removes anything a failed earlier run left, then inserts; Remove
/// deletes it all again.
/// </summary>
public static class TrendsTestData
{
    public static readonly DateOnly From = new(2026, 2, 1);
    public static readonly DateOnly To = new(2026, 2, 14);

    public sealed record Keys(int ApplicationKey, int SafeAKey, int SafeBKey);

    private const string Prefix = "TrendsTest";

    public static async Task<Keys> SeedAsync()
    {
        await RemoveAsync();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<Keys>("""
            DECLARE @Src INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'DISCOVERY');
            DECLARE @App INT;
            INSERT INTO web.dim_application (ApplicationCode, ApplicationName) VALUES ('TRENDSTEST', 'TrendsTest Application');
            SET @App = SCOPE_IDENTITY();
            INSERT INTO dbo.dim_safe (SourceSystemKey, SafeUrlId, SafeName, ApplicationKey) VALUES (@Src, 'TrendsTestSafeA', 'TrendsTestSafeA', @App);
            DECLARE @SafeA INT = SCOPE_IDENTITY();
            INSERT INTO dbo.dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@Src, 'TrendsTestSafeB', 'TrendsTestSafeB');
            DECLARE @SafeB INT = SCOPE_IDENTITY();

            INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, SafeKey)
            VALUES (@Src, 'TrendsTest-A1', 'TrendsTest-A1', 'TrendsTest-A1', @SafeA),
                   (@Src, 'TrendsTest-A2', 'TrendsTest-A2', 'TrendsTest-A2', @SafeA),
                   (@Src, 'TrendsTest-B1', 'TrendsTest-B1', 'TrendsTest-B1', @SafeB);

            INSERT INTO dbo.fact_account_progress_history (SnapshotDateKey, AccountKey, StageKey, StatusKey, RiskLevelKey)
            SELECT v.DateKey, fa.AccountKey, stg.StageKey, sts.StatusKey, rl.RiskLevelKey
            FROM (VALUES
                (20260202, 'TrendsTest-A1', 1, 'Not Started', NULL),
                (20260202, 'TrendsTest-A2', 2, 'In Progress', 'Low'),
                (20260202, 'TrendsTest-B1', 1, 'Not Started', 'High'),
                (20260204, 'TrendsTest-A1', 3, 'In Progress', 'Low'),
                (20260204, 'TrendsTest-A2', 5, 'Complete', 'Medium'),
                (20260204, 'TrendsTest-B1', 1, 'Risk Accepted / Excluded', 'High'),
                (20260210, 'TrendsTest-A1', 5, 'Complete', 'Low'),
                (20260210, 'TrendsTest-A2', 5, 'Complete', 'Medium'),
                (20260210, 'TrendsTest-B1', 5, 'In Progress', 'Critical')
            ) AS v(DateKey, Account, StageOrder, StatusName, RiskLevelName)
            JOIN dbo.fact_account fa ON fa.SourceSystemKey = @Src AND fa.SourceAccountId = v.Account
            JOIN dbo.dim_blueprint_stage stg ON stg.StageOrder = v.StageOrder
            JOIN dbo.dim_progress_status sts ON sts.StatusName = v.StatusName
            LEFT JOIN dbo.dim_risk_level rl ON rl.RiskLevelName = v.RiskLevelName;

            SELECT @App AS ApplicationKey, @SafeA AS SafeAKey, @SafeB AS SafeBKey;
            """);
    }

    public static async Task RemoveAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync($"""
            DELETE h FROM dbo.fact_account_progress_history h
            JOIN dbo.fact_account fa ON fa.AccountKey = h.AccountKey
            WHERE fa.SourceAccountId LIKE '{Prefix}-%';
            DELETE p FROM dbo.fact_account_progress p
            JOIN dbo.fact_account fa ON fa.AccountKey = p.AccountKey
            WHERE fa.SourceAccountId LIKE '{Prefix}-%';
            DELETE FROM dbo.fact_account WHERE SourceAccountId LIKE '{Prefix}-%';
            DELETE FROM dbo.dim_safe WHERE SafeUrlId LIKE '{Prefix}Safe%';
            DELETE FROM web.dim_application WHERE ApplicationCode = 'TRENDSTEST';
            """);
    }
}
