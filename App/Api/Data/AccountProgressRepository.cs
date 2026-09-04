using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Account Progress list/grid page, including D-42's "multiple
/// simultaneous layers of sort and filter" (stacked filters plus
/// multi-column sort, applied together). Sort columns are validated
/// against a fixed whitelist (SortableColumns) rather than interpolating
/// the requested field name directly -- the field names themselves come
/// from the frontend's query string, so this is the SQL-injection guard,
/// not just a defensive nicety.
/// </summary>
public sealed class AccountProgressRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["accountName"] = "fa.AccountName",
        ["stageName"] = "stg.StageOrder",
        ["statusName"] = "sts.StatusName",
        ["riskLevelName"] = "rl.RiskOrder",
        ["ownerName"] = "fap.OwnerName",
        ["targetRemediationDate"] = "fap.TargetRemediationDate",
        ["actualCompletionDate"] = "fap.ActualCompletionDate"
    };

    public async Task<IReadOnlyList<AccountProgressSummary>> GetSummaryListAsync(
        string? stageName = null,
        string? statusName = null,
        string? riskLevelName = null,
        string? ownerContains = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null)
    {
        using var connection = connectionFactory.Create();

        var sql = $"""
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
              AND (@StatusName IS NULL OR sts.StatusName = @StatusName)
              AND (@RiskLevelName IS NULL OR rl.RiskLevelName = @RiskLevelName)
              AND (@OwnerContains IS NULL OR fap.OwnerName LIKE '%' + @OwnerContains + '%')
            ORDER BY {BuildOrderByClause(sortBy)}
            """;

        var rows = await connection.QueryAsync<AccountProgressSummary>(sql, new
        {
            StageName = stageName,
            StatusName = statusName,
            RiskLevelName = riskLevelName,
            OwnerContains = ownerContains
        });
        return rows.AsList();
    }

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "fa.AccountName ASC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "fa.AccountName ASC";
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

    /// <summary>
    /// exceptionKey is passed separately from the rest of the request,
    /// rather than read off SaveAccountProgressRequest.ExceptionKey
    /// directly -- the caller (AccountProgressController) validates and
    /// resolves it first (required + must be an Active exception on this
    /// account when status = Risk Accepted / Excluded; cleared otherwise).
    /// </summary>
    public async Task UpdateAsync(long accountKey, SaveAccountProgressRequest request, int? exceptionKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE dbo.fact_account_progress
            SET CurrentStageKey = @CurrentStageKey, CurrentStatusKey = @CurrentStatusKey, RiskLevelKey = @RiskLevelKey,
                AccountTypeKey = @AccountTypeKey, SORKey = @SORKey, OwnerName = @OwnerName, BusinessUnit = @BusinessUnit,
                TargetRemediationDate = @TargetRemediationDate, ActualCompletionDate = @ActualCompletionDate,
                Notes = @Notes, ExceptionKey = @ExceptionKey, LastUpdated = SYSUTCDATETIME()
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
            request.Notes,
            ExceptionKey = exceptionKey
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

    /// <summary>D-81: Active application-scoped exceptions covering this account, computed live (web.vw_account_application_exception).</summary>
    public async Task<IReadOnlyList<ApplicationScopedException>> GetApplicationScopedExceptionsAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ExceptionID, ApplicationKey, ApplicationName, ReviewDate
            FROM web.vw_account_application_exception
            WHERE AccountKey = @AccountKey
            """;
        var rows = await connection.QueryAsync<ApplicationScopedException>(sql, new { AccountKey = accountKey });
        return rows.AsList();
    }
}
