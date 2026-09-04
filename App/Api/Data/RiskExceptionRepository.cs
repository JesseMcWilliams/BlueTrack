using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Risk Exceptions list, approval worklist (Active only), and
/// overdue-review worklist (Active and past ReviewDate) pages, plus the
/// create/edit form (Design_Risk_Exception_Tracking.md), and the Account
/// Progress edit form's "link an existing exception" picker (accountKey
/// filter on GetListAsync).
/// </summary>
public sealed class RiskExceptionRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["exceptionID"] = "re.ExceptionID",
        ["scopeName"] = "ScopeName",
        ["approvedByName"] = "au.DisplayName",
        ["approvalDate"] = "re.ApprovalDate",
        ["reviewDate"] = "re.ReviewDate",
        ["statusName"] = "des.StatusName"
    };

    /// <summary>
    /// D-42: stacked filters (status/accountKey/scopeType) plus multi-column
    /// sort, validated against a fixed whitelist -- the requested sort
    /// field comes straight from the query string, so this is the
    /// SQL-injection guard, not just tidiness (same pattern as
    /// AccountProgressRepository).
    /// </summary>
    public async Task<IReadOnlyList<RiskExceptionSummary>> GetListAsync(
        string? statusName = null,
        long? accountKey = null,
        string? scopeType = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null)
    {
        using var connection = connectionFactory.Create();

        var sql = ListSqlBase + $"""
              AND (@StatusName IS NULL OR des.StatusName = @StatusName)
              AND (@AccountKey IS NULL OR re.AccountKey = @AccountKey)
              AND (@ScopeType IS NULL OR (CASE WHEN re.AccountKey IS NOT NULL THEN 'Account' ELSE 'Application' END) = @ScopeType)
            ORDER BY {BuildOrderByClause(sortBy)}
            """;

        var rows = await connection.QueryAsync<RiskExceptionSummary>(sql, new
        {
            StatusName = statusName,
            AccountKey = accountKey,
            ScopeType = scopeType
        });
        return rows.AsList();
    }

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "re.ReviewDate ASC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "re.ReviewDate ASC";
    }

    public async Task<IReadOnlyList<RiskExceptionSummary>> GetActiveAsync()
    {
        using var connection = connectionFactory.Create();
        // ListSql's WHERE clause references @AccountKey too -- must supply
        // it even as null, or SQL Server rejects the batch with "Must
        // declare the scalar variable @AccountKey" (a real regression
        // introduced when D-77 added that filter here without updating
        // this caller, caught while testing D-42's Risk Exceptions sort).
        var rows = await connection.QueryAsync<RiskExceptionSummary>(ListSql, new { StatusName = "Active", AccountKey = (long?)null });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<RiskExceptionSummary>> GetOverdueReviewAsync()
    {
        using var connection = connectionFactory.Create();

        const string sql = ListSqlBase + """
              AND des.StatusName = 'Active'
              AND re.ReviewDate < CAST(SYSUTCDATETIME() AS DATE)
            ORDER BY re.ReviewDate
            """;

        var rows = await connection.QueryAsync<RiskExceptionSummary>(sql);
        return rows.AsList();
    }

    public async Task<RiskExceptionDetail?> GetByKeyAsync(int exceptionKey)
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT re.ExceptionKey, re.ExceptionID, re.AccountKey, re.ApplicationKey, re.Justification,
                   re.ApprovalDate, re.ReviewDate, des.StatusName, re.ExternalTicketReference
            FROM web.risk_exception re
            JOIN web.dim_exception_status des ON des.ExceptionStatusKey = re.ExceptionStatusKey
            WHERE re.ExceptionKey = @ExceptionKey
            """;

        return await connection.QuerySingleOrDefaultAsync<RiskExceptionDetail>(sql, new { ExceptionKey = exceptionKey });
    }

    /// <summary>
    /// Creates a new Active exception, assigning the next ExceptionID per
    /// the org's configured numbering scheme (D-17). Caller
    /// (RiskExceptionsController) is responsible for validating that
    /// exactly one of AccountKey/ApplicationKey is set before calling this.
    /// </summary>
    public async Task<int> CreateAsync(CreateRiskExceptionRequest request, int approvedByUserKey)
    {
        using var connection = connectionFactory.Create();

        var config = await connection.QuerySingleAsync<ExceptionIdConfig>("""
            UPDATE web.app_config
            SET ExceptionIdNextSequence = CASE WHEN ExceptionIdSequenceYear = @CurrentYear THEN ExceptionIdNextSequence + 1 ELSE 1 END,
                ExceptionIdSequenceYear = @CurrentYear
            OUTPUT inserted.ExceptionIdPattern, inserted.ExceptionIdNextSequence
            """, new { CurrentYear = DateTime.UtcNow.Year });

        var exceptionId = ExceptionIdGenerator.Generate(config.ExceptionIdPattern, DateTime.UtcNow.Year, config.ExceptionIdNextSequence);
        var approvalDate = DateTime.UtcNow.Date;

        const string insertSql = """
            INSERT INTO web.risk_exception
                (ExceptionID, AccountKey, ApplicationKey, Justification, ApprovedBy, ApprovalDate, ReviewDate, ExceptionStatusKey, ExternalTicketReference)
            OUTPUT inserted.ExceptionKey
            SELECT @ExceptionID, @AccountKey, @ApplicationKey, @Justification, @ApprovedBy, @ApprovalDate, @ReviewDate,
                   (SELECT ExceptionStatusKey FROM web.dim_exception_status WHERE StatusName = 'Active'), @ExternalTicketReference
            """;

        return await connection.QuerySingleAsync<int>(insertSql, new
        {
            ExceptionID = exceptionId,
            request.AccountKey,
            request.ApplicationKey,
            request.Justification,
            ApprovedBy = approvedByUserKey,
            ApprovalDate = approvalDate,
            request.ReviewDate,
            request.ExternalTicketReference
        });
    }

    /// <summary>Re-approval (design's workflow step 4): extends ReviewDate without changing status.</summary>
    public async Task ExtendReviewAsync(int exceptionKey, DateTime newReviewDate)
    {
        using var connection = connectionFactory.Create();
        const string sql = "UPDATE web.risk_exception SET ReviewDate = @NewReviewDate WHERE ExceptionKey = @ExceptionKey";
        await connection.ExecuteAsync(sql, new { ExceptionKey = exceptionKey, NewReviewDate = newReviewDate });
    }

    /// <summary>Revocation (design's workflow step 4).</summary>
    public async Task RevokeAsync(int exceptionKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.risk_exception
            SET ExceptionStatusKey = (SELECT ExceptionStatusKey FROM web.dim_exception_status WHERE StatusName = 'Revoked')
            WHERE ExceptionKey = @ExceptionKey
            """;
        await connection.ExecuteAsync(sql, new { ExceptionKey = exceptionKey });
    }

    private sealed record ExceptionIdConfig(string ExceptionIdPattern, int ExceptionIdNextSequence);

    private const string ListSqlBase = """
        SELECT
            re.ExceptionKey,
            re.ExceptionID,
            CASE WHEN re.AccountKey IS NOT NULL THEN 'Account' ELSE 'Application' END AS ScopeType,
            COALESCE(fa.AccountName, da.ApplicationName) AS ScopeName,
            re.Justification,
            au.DisplayName AS ApprovedByName,
            re.ApprovalDate,
            re.ReviewDate,
            des.StatusName,
            re.ExternalTicketReference
        FROM web.risk_exception re
        JOIN web.dim_exception_status des ON des.ExceptionStatusKey = re.ExceptionStatusKey
        LEFT JOIN dbo.fact_account fa      ON fa.AccountKey = re.AccountKey
        LEFT JOIN web.dim_application da    ON da.ApplicationKey = re.ApplicationKey
        LEFT JOIN web.app_user au            ON au.UserKey = re.ApprovedBy
        WHERE 1 = 1
        """;

    private const string ListSql = ListSqlBase + """
          AND (@StatusName IS NULL OR des.StatusName = @StatusName)
          AND (@AccountKey IS NULL OR re.AccountKey = @AccountKey)
        ORDER BY re.ReviewDate
        """;
}
