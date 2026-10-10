using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Risk Exceptions list, approval worklist (Active only), and
/// overdue-review worklist (Active and past ReviewDate) pages, plus the
/// create/edit form (Design_Risk-Exception-Tracking.md), and the Account
/// Progress edit form's "link an existing exception" picker (accountKey
/// filter on GetListAsync).
/// </summary>
public sealed class RiskExceptionRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["exceptionID"] = "re.ExceptionID",
        ["scopeName"] = "ScopeName",
        ["approvedByName"] = "COALESCE(au.DisplayName, re.ApprovedByName)",
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
    // D-124 Phase 3: the three GetListAsync-only filter conditions, shared
    // with GetFilteredCountAsync so the filtered-count query mirrors the
    // list query's own WHERE clause exactly.
    private const string ListFilterConditionsSql = """
          AND (@StatusName IS NULL OR des.StatusName = @StatusName)
          AND (@AccountKey IS NULL OR re.AccountKey = @AccountKey)
          AND (@ScopeType IS NULL OR (CASE WHEN re.AccountKey IS NOT NULL THEN 'Account' ELSE 'Application' END) = @ScopeType)
        """;

    /// <summary>D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY.</summary>
    public async Task<IReadOnlyList<RiskExceptionSummary>> GetListAsync(
        string? statusName = null,
        long? accountKey = null,
        string? scopeType = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);

        var sql = ListSqlBase + ListFilterConditionsSql + $"""
            ORDER BY {BuildOrderByClause(sortBy)}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;

        var rows = await connection.QueryAsync<RiskExceptionSummary>(sql, new
        {
            StatusName = statusName,
            AccountKey = accountKey,
            ScopeType = scopeType,
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        });
        return rows.AsList();
    }

    /// <summary>D-121: the grand total row count with no filter applied -- backs the X-Total-Count response header on GetListAsync's own endpoint.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.risk_exception");
    }

    /// <summary>D-124 Phase 3: how many rows match GetListAsync's current filter (ignoring paging) -- backs the new X-Filtered-Count header, distinct from GetTotalCountAsync's unfiltered grand total.</summary>
    public async Task<int> GetFilteredCountAsync(string? statusName = null, long? accountKey = null, string? scopeType = null)
    {
        using var connection = connectionFactory.Create();
        var sql = "SELECT COUNT(*)\n" + FromJoinSql + ListFilterConditionsSql;
        return await connection.QuerySingleAsync<int>(sql, new { StatusName = statusName, AccountKey = accountKey, ScopeType = scopeType });
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
                   re.ApprovedBy, COALESCE(au.DisplayName, re.ApprovedByName) AS ApprovedByName,
                   re.ApprovalDate, re.ReviewDate, des.StatusName, re.ExternalTicketReference,
                   re.SourceTool, re.SourceExceptionId, re.SourceUrl, iu.DisplayName AS ImportedByName, re.ImportedDate
            FROM web.risk_exception re
            JOIN web.dim_exception_status des ON des.ExceptionStatusKey = re.ExceptionStatusKey
            LEFT JOIN web.app_user au ON au.UserKey = re.ApprovedBy
            LEFT JOIN web.app_user iu ON iu.UserKey = re.ImportedBy
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

        var exceptionId = await NextExceptionIdAsync(connection);
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

    /// <summary>D-17: takes the next number in the configured ExceptionID pattern.</summary>
    private static async Task<string> NextExceptionIdAsync(System.Data.IDbConnection connection)
    {
        var config = await connection.QuerySingleAsync<ExceptionIdConfig>("""
            UPDATE web.app_config
            SET ExceptionIdNextSequence = CASE WHEN ExceptionIdSequenceYear = @CurrentYear THEN ExceptionIdNextSequence + 1 ELSE 1 END,
                ExceptionIdSequenceYear = @CurrentYear
            OUTPUT inserted.ExceptionIdPattern, inserted.ExceptionIdNextSequence
            """, new { CurrentYear = DateTime.UtcNow.Year });
        return ExceptionIdGenerator.Generate(config.ExceptionIdPattern, DateTime.UtcNow.Year, config.ExceptionIdNextSequence);
    }

    /// <summary>
    /// D-183: an exception imported from another tool. It gets a BlueTrack
    /// ExceptionID (the configured pattern) and keeps the source's own ID,
    /// tool, link, approver name and dates. Returns the key and the new ID.
    /// </summary>
    public async Task<(int ExceptionKey, string ExceptionId)> CreateImportedAsync(ImportedRiskException exception, int importedByUserKey)
    {
        using var connection = connectionFactory.Create();
        var exceptionId = await NextExceptionIdAsync(connection);
        var key = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.risk_exception
                (ExceptionID, AccountKey, ApplicationKey, Justification, ApprovedBy, ApprovedByName, ApprovalDate, ReviewDate,
                 ExceptionStatusKey, ExternalTicketReference, SourceTool, SourceExceptionId, SourceUrl, ImportedBy, ImportedDate)
            OUTPUT inserted.ExceptionKey
            SELECT @ExceptionID, @AccountKey, @ApplicationKey, @Justification, NULL, @ApprovedByName, @ApprovalDate, @ReviewDate,
                   (SELECT ExceptionStatusKey FROM web.dim_exception_status WHERE StatusName = @StatusName),
                   @ExternalTicketReference, @SourceTool, @SourceExceptionId, @SourceUrl, @ImportedBy, SYSUTCDATETIME()
            """, new
        {
            ExceptionID = exceptionId,
            exception.AccountKey,
            exception.ApplicationKey,
            exception.Justification,
            exception.ApprovedByName,
            exception.ApprovalDate,
            exception.ReviewDate,
            exception.StatusName,
            exception.ExternalTicketReference,
            exception.SourceTool,
            exception.SourceExceptionId,
            exception.SourceUrl,
            ImportedBy = importedByUserKey
        });
        return (key, exceptionId);
    }

    /// <summary>D-183: the BlueTrack ExceptionID already imported for this source tool + ID, if any.</summary>
    public async Task<string?> FindBySourceAsync(string sourceTool, string sourceExceptionId)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT ExceptionID FROM web.risk_exception WHERE SourceTool = @sourceTool AND SourceExceptionId = @sourceExceptionId",
            new { sourceTool, sourceExceptionId });
    }

    /// <summary>
    /// D-183: active accounts whose username and address match exactly
    /// (ignoring case and surrounding spaces). Both are required: an
    /// account with no address is never matched (agreed 2026-10-09).
    /// </summary>
    public async Task<IReadOnlyList<long>> FindAccountKeysAsync(string userName, string address)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<long>("""
            SELECT AccountKey FROM dbo.fact_account
            WHERE IsDeleted = 0 AND UPPER(LTRIM(RTRIM(UserName))) = UPPER(@userName)
              AND UPPER(LTRIM(RTRIM(Address))) = UPPER(@address)
            """, new { userName = userName.Trim(), address = address.Trim() })).AsList();
    }

    /// <summary>Re-approval (design's workflow step 4): extends ReviewDate without changing status.</summary>
    public async Task ExtendReviewAsync(int exceptionKey, DateTime newReviewDate)
    {
        using var connection = connectionFactory.Create();
        const string sql = "UPDATE web.risk_exception SET ReviewDate = @NewReviewDate WHERE ExceptionKey = @ExceptionKey";
        await connection.ExecuteAsync(sql, new { ExceptionKey = exceptionKey, NewReviewDate = newReviewDate });
    }

    /// <summary>
    /// Segregation-of-duties detective report (Option C): every historical
    /// case where the user who linked an exception's ExceptionKey onto an
    /// account's progress record (logged as a FieldEdit audit event on
    /// fact_account_progress) is the same user recorded as that exception's
    /// own ApprovedBy. Reported regardless of the enforcement toggle's
    /// current or past state -- built entirely from the audit trail
    /// AccountProgressController.Update already writes, no new schema
    /// needed for this piece.
    /// </summary>
    public async Task<IReadOnlyList<RiskExceptionSodViolation>> GetSegregationOfDutiesViolationsAsync()
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT
                re.ExceptionKey,
                re.ExceptionID,
                fa.AccountKey,
                fa.AccountName,
                au.DisplayName AS UserDisplayName,
                ae.OccurredAt
            FROM web.audit_field_change afc
            JOIN web.audit_event ae   ON ae.AuditEventKey = afc.AuditEventKey
            JOIN dbo.fact_account fa   ON fa.AccountKey = TRY_CAST(ae.EntityKey AS BIGINT)
            JOIN web.risk_exception re  ON re.ExceptionKey = TRY_CAST(afc.NewValue AS INT)
            JOIN web.app_user au          ON au.UserKey = ae.PerformedByUserKey
            WHERE ae.EntityName = 'fact_account_progress'
              AND afc.FieldName = 'ExceptionKey'
              AND afc.NewValue IS NOT NULL
              AND ae.PerformedByUserKey = re.ApprovedBy
            ORDER BY ae.OccurredAt DESC
            """;

        var rows = await connection.QueryAsync<RiskExceptionSodViolation>(sql);
        return rows.AsList();
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

    // D-124 Phase 3: the FROM/JOIN/WHERE skeleton shared between ListSqlBase
    // (the SELECT-shaped query below) and GetFilteredCountAsync's own plain
    // COUNT(*) -- a COUNT needs no SELECT column list of its own.
    private const string FromJoinSql = """
        FROM web.risk_exception re
        JOIN web.dim_exception_status des ON des.ExceptionStatusKey = re.ExceptionStatusKey
        LEFT JOIN dbo.fact_account fa      ON fa.AccountKey = re.AccountKey
        LEFT JOIN web.dim_application da    ON da.ApplicationKey = re.ApplicationKey
        LEFT JOIN web.app_user au            ON au.UserKey = re.ApprovedBy
        WHERE 1 = 1
        """;

    private const string ListSqlBase = """
        SELECT
            re.ExceptionKey,
            re.ExceptionID,
            CASE WHEN re.AccountKey IS NOT NULL THEN 'Account' ELSE 'Application' END AS ScopeType,
            COALESCE(fa.AccountName, da.ApplicationName) AS ScopeName,
            re.Justification,
            COALESCE(au.DisplayName, re.ApprovedByName) AS ApprovedByName,
            re.ApprovalDate,
            re.ReviewDate,
            des.StatusName,
            re.ExternalTicketReference,
            re.SourceTool, re.SourceExceptionId, re.SourceUrl
        """ + "\n" + FromJoinSql;

    private const string ListSql = ListSqlBase + """
          AND (@StatusName IS NULL OR des.StatusName = @StatusName)
          AND (@AccountKey IS NULL OR re.AccountKey = @AccountKey)
        ORDER BY re.ReviewDate
        """;

    /// <summary>D-190: "Select all matching" -- the keys of exceptions matching the list's filters, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<int>> GetFilteredKeysAsync(string? statusName, string? scopeType, int limit)
    {
        using var connection = connectionFactory.Create();
        var sql = "SELECT TOP (@Limit) re.ExceptionKey\n" + FromJoinSql + ListFilterConditionsSql + "\nORDER BY re.ExceptionID";
        return (await connection.QueryAsync<int>(sql, new { StatusName = statusName, AccountKey = (long?)null, ScopeType = scopeType, Limit = limit })).AsList();
    }
}
