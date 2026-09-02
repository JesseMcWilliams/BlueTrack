using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Account Progress list/grid page. Only basic single-value
/// filtering is implemented here -- D-42's "multiple simultaneous layers of
/// sort and filter" requirement needs a proper dynamic query builder
/// (or an OData-style endpoint), which is a follow-up build task, not part
/// of this scaffold.
/// </summary>
public sealed class AccountProgressRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<AccountProgressSummary>> GetSummaryListAsync(string? stageName = null)
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT
                fa.AccountKey,
                fa.AccountName,
                stg.StageName,
                sts.StatusName,
                rl.RiskLevelName,
                fap.OwnerName,
                fap.TargetRemediationDate,
                fap.ActualCompletionDate
            FROM dbo.fact_account_progress fap
            JOIN dbo.fact_account fa           ON fa.AccountKey = fap.AccountKey
            JOIN dbo.dim_blueprint_stage stg    ON stg.StageKey = fap.CurrentStageKey
            JOIN dbo.dim_progress_status sts     ON sts.StatusKey = fap.CurrentStatusKey
            LEFT JOIN dbo.dim_risk_level rl         ON rl.RiskLevelKey = fap.RiskLevelKey
            WHERE fa.IsDeleted = 0
              AND (@StageName IS NULL OR stg.StageName = @StageName)
            ORDER BY fa.AccountName
            """;

        var rows = await connection.QueryAsync<AccountProgressSummary>(sql, new { StageName = stageName });
        return rows.AsList();
    }

    public async Task<AccountProgressDetail?> GetDetailAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT
                fap.ProgressKey, fap.AccountKey, fa.AccountName,
                fap.CurrentStageKey, fap.CurrentStatusKey, fap.RiskLevelKey, fap.AccountTypeKey, fap.SORKey,
                fap.OwnerName, fap.BusinessUnit, fap.TargetRemediationDate, fap.ActualCompletionDate, fap.Notes,
                fap.LastUpdated, fap.ExceptionKey
            FROM dbo.fact_account_progress fap
            JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey
            WHERE fap.AccountKey = @AccountKey
            """;
        return await connection.QuerySingleOrDefaultAsync<AccountProgressDetail>(sql, new { AccountKey = accountKey });
    }

    public async Task UpdateAsync(long accountKey, SaveAccountProgressRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE dbo.fact_account_progress
            SET CurrentStageKey = @CurrentStageKey, CurrentStatusKey = @CurrentStatusKey, RiskLevelKey = @RiskLevelKey,
                AccountTypeKey = @AccountTypeKey, SORKey = @SORKey, OwnerName = @OwnerName, BusinessUnit = @BusinessUnit,
                TargetRemediationDate = @TargetRemediationDate, ActualCompletionDate = @ActualCompletionDate,
                Notes = @Notes, LastUpdated = SYSUTCDATETIME()
            WHERE AccountKey = @AccountKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            AccountKey = accountKey,
            request.CurrentStageKey,
            request.CurrentStatusKey,
            request.RiskLevelKey,
            request.AccountTypeKey,
            request.SORKey,
            request.OwnerName,
            request.BusinessUnit,
            request.TargetRemediationDate,
            request.ActualCompletionDate,
            request.Notes
        });
    }

    /// <summary>D-51 rule 1: Complete requires ActualCompletionDate -- looked up by name, not a hardcoded key.</summary>
    public async Task<string?> GetStatusNameAsync(int statusKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT StatusName FROM dbo.dim_progress_status WHERE StatusKey = @StatusKey", new { StatusKey = statusKey });
    }

    /// <summary>D-51 rule 2: a stage regression (lower StageOrder) requires a Reason.</summary>
    public async Task<int?> GetStageOrderAsync(int stageKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT StageOrder FROM dbo.dim_blueprint_stage WHERE StageKey = @StageKey", new { StageKey = stageKey });
    }
}
