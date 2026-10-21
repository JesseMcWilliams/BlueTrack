using System.Text.Json;
using Dapper;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-212 (phase 4c): possible matches between feed accounts and CyberArk
/// accounts, kept as unconfirmed dbo.account_reconciliation rows (MatchMethod
/// FeedShortName / FeedDnsForward / FeedDnsReverse), and the report's Link and
/// Dismiss. A match stays open while both accounts exist and the feed account
/// isn't linked yet.
/// </summary>
public sealed class PossibleMatchRepository(IDbConnectionFactory connectionFactory)
{
    private const string OpenMatchesSql = """
        FROM dbo.account_reconciliation r
        JOIN dbo.fact_account feed ON feed.AccountKey = r.LegacyAccountKey AND feed.IsDeleted = 0
        JOIN dbo.dim_source_system fs ON fs.SourceSystemKey = feed.SourceSystemKey
        JOIN dbo.fact_account ca ON ca.AccountKey = r.CurrentAccountKey AND ca.IsDeleted = 0
        JOIN dbo.dim_source_system cs ON cs.SourceSystemKey = ca.SourceSystemKey
        WHERE r.MatchMethod IN ('FeedShortName', 'FeedDnsForward', 'FeedDnsReverse')
          AND r.IsConfirmed = 0 AND r.RejectedFlag = @Rejected
          AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = feed.AccountKey)
        """;

    /// <summary>A feed source's accounts that aren't deleted or linked yet.</summary>
    public async Task<IReadOnlyList<PossibleMatchFinder.Account>> GetUnlinkedFeedAccountsAsync(int sourceSystemKey)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<PossibleMatchFinder.Account>("""
            SELECT fa.AccountKey, fa.UserName, fa.Address
            FROM dbo.fact_account fa
            WHERE fa.SourceSystemKey = @sourceSystemKey AND fa.IsDeleted = 0 AND fa.UserName IS NOT NULL AND fa.Address IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)
            """, new { sourceSystemKey })).ToList();
    }

    /// <summary>CyberArk accounts (not deleted) with any of these user names, ignoring case and spaces.</summary>
    public async Task<IReadOnlyList<PossibleMatchFinder.Account>> GetCyberArkAccountsAsync(IEnumerable<string> userNames)
    {
        var names = userNames.Select(u => u.Trim().ToLowerInvariant()).Distinct().ToList();
        if (names.Count == 0) return [];
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<PossibleMatchFinder.Account>("""
            SELECT fa.AccountKey, fa.UserName, fa.Address
            FROM dbo.fact_account fa
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
            WHERE ss.SourceSystemCode IN ('PRIVCLOUD', 'SELFHOSTED') AND fa.IsDeleted = 0 AND fa.Address IS NOT NULL
              AND LOWER(LTRIM(RTRIM(fa.UserName))) IN (SELECT value FROM OPENJSON(@names))
            """, new { names = JsonSerializer.Serialize(names) })).ToList();
    }

    /// <summary>Adds each pair not already recorded (a rejected pair stays rejected); returns how many were new.</summary>
    public async Task<int> AddAsync(IReadOnlyList<PossibleMatchFinder.Candidate> candidates)
    {
        if (candidates.Count == 0) return 0;
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("""
            INSERT INTO dbo.account_reconciliation (LegacyAccountKey, CurrentAccountKey, MatchMethod, MatchConfidence, Notes)
            SELECT c.FeedAccountKey, c.CyberArkAccountKey, c.MatchMethod, 'Needs Review', LEFT(c.Reason, 1000)
            FROM OPENJSON(@json) WITH (FeedAccountKey BIGINT, CyberArkAccountKey BIGINT, MatchMethod NVARCHAR(50), Reason NVARCHAR(2000)) c
            WHERE NOT EXISTS (SELECT 1 FROM dbo.account_reconciliation r
                              WHERE r.LegacyAccountKey = c.FeedAccountKey AND r.CurrentAccountKey = c.CyberArkAccountKey)
            """, new { json = JsonSerializer.Serialize(candidates) });
    }

    /// <summary>The open matches, or (UI review W5-4, <paramref name="dismissed"/>) the dismissed pairs that could still be restored.</summary>
    public async Task<IReadOnlyList<PossibleMatchRow>> GetOpenAsync(int? page, int? pageSize, bool dismissed = false)
    {
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        return await QueryOpenAsync(PagingParams.Offset(normalizedPage, normalizedPageSize), normalizedPageSize, dismissed);
    }

    /// <summary>D-220: every open match in the report's order (all pages), up to maxRows -- for the export.</summary>
    public Task<IReadOnlyList<PossibleMatchRow>> GetOpenExportRowsAsync(int maxRows) => QueryOpenAsync(0, maxRows);

    private async Task<IReadOnlyList<PossibleMatchRow>> QueryOpenAsync(int offset, int fetch, bool dismissed = false)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<PossibleMatchRow>($"""
            SELECT r.ReconciliationKey, r.MatchMethod, r.Notes AS Reason, r.MatchedDate,
                   feed.AccountKey AS FeedAccountKey, feed.UserName AS FeedUserName, feed.Address AS FeedAddress, fs.SourceSystemName AS FeedSourceName,
                   ca.AccountKey AS CyberArkAccountKey, ca.AccountName AS CyberArkAccountName, ca.UserName AS CyberArkUserName,
                   ca.Address AS CyberArkAddress, cs.SourceSystemName AS CyberArkSourceName
            {OpenMatchesSql}
            ORDER BY r.MatchedDate DESC, r.ReconciliationKey DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """, new { Offset = offset, PageSize = fetch, Rejected = dismissed ? 1 : 0 })).ToList();
    }

    public async Task<int> GetOpenCountAsync(bool dismissed = false)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>($"SELECT COUNT(*) {OpenMatchesSql}", new { Rejected = dismissed ? 1 : 0 });
    }

    public sealed record ConfirmedMatch(long FeedAccountKey, long CyberArkAccountKey, string CyberArkAccountName, int? OldStageKey, int? NewStageKey);

    /// <summary>
    /// Link: confirms the pair and, if the feed account is at Discovered, moves
    /// it to Onboarded to Vault (as an exact match does). Null when the match
    /// isn't open any more (already linked, dismissed, or an account deleted).
    /// </summary>
    public async Task<ConfirmedMatch?> ConfirmAsync(long reconciliationKey, string reviewedBy)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var match = await connection.QuerySingleOrDefaultAsync<(long FeedAccountKey, long CyberArkAccountKey, string CyberArkAccountName)?>($"""
            SELECT feed.AccountKey, ca.AccountKey, ca.AccountName
            {OpenMatchesSql}
              AND r.ReconciliationKey = @reconciliationKey
            """, new { reconciliationKey, Rejected = 0 }, transaction);
        if (match is not { } m)
        {
            return null;
        }

        await connection.ExecuteAsync("""
            UPDATE dbo.account_reconciliation SET IsConfirmed = 1, ReviewedBy = @reviewedBy, ReviewedDate = SYSUTCDATETIME()
            WHERE ReconciliationKey = @reconciliationKey
            """, new { reconciliationKey, reviewedBy }, transaction);

        var moved = await connection.QuerySingleOrDefaultAsync<(int OldStageKey, int NewStageKey)?>("""
            DECLARE @Discovered INT = dbo.fn_StartStageKey();                  -- D-284
            DECLARE @Onboarded INT = dbo.fn_MilestoneStageKey(N'Onboarded');
            UPDATE dbo.fact_account_progress SET CurrentStageKey = @Onboarded, LastUpdated = SYSUTCDATETIME()
            OUTPUT deleted.CurrentStageKey, inserted.CurrentStageKey
            WHERE AccountKey = @feedAccountKey AND CurrentStageKey = @Discovered AND @Onboarded IS NOT NULL;
            """, new { feedAccountKey = m.FeedAccountKey }, transaction);

        transaction.Commit();
        return new ConfirmedMatch(m.FeedAccountKey, m.CyberArkAccountKey, m.CyberArkAccountName, moved?.OldStageKey, moved?.NewStageKey);
    }

    /// <summary>Dismiss: the pair is rejected and never proposed or linked again. False when it isn't open.</summary>
    public async Task<bool> DismissAsync(long reconciliationKey, string reviewedBy)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync($"""
            UPDATE r SET RejectedFlag = 1, ReviewedBy = @reviewedBy, ReviewedDate = SYSUTCDATETIME()
            {OpenMatchesSql}
              AND r.ReconciliationKey = @reconciliationKey
            """, new { reconciliationKey, reviewedBy, Rejected = 0 }) > 0;
    }

    /// <summary>UI review W5-4: Restore puts a dismissed pair back among the open matches. False when it isn't a dismissed pair (any more).</summary>
    public async Task<bool> RestoreAsync(long reconciliationKey, string reviewedBy)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync($"""
            UPDATE r SET RejectedFlag = 0, ReviewedBy = @reviewedBy, ReviewedDate = SYSUTCDATETIME()
            {OpenMatchesSql}
              AND r.ReconciliationKey = @reconciliationKey
            """, new { reconciliationKey, reviewedBy, Rejected = 1 }) > 0;
    }
}
