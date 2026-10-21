using System.Globalization;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-221: Reports &gt; Trends, from the daily per-account snapshot
/// dbo.fact_account_progress_history (written by
/// usp_Load_FactAccountProgressHistory, latest in script 15). Each snapshot
/// already leaves out deleted and linked feed accounts as they were that
/// day, so nothing here filters them again. Application and Safe go through
/// the account's current Safe (fact_account.SafeKey) and that Safe's
/// Application (dim_safe.ApplicationKey) -- the snapshot doesn't record the
/// Safe, so an account that moved Safes is counted under its current one.
/// D-238: an account also counts under its own Application
/// (fact_account_progress.ApplicationKey), so the Application filter matches
/// either one.
/// </summary>
public sealed class TrendsRepository(IDbConnectionFactory connectionFactory)
{
    public const string Day = "day";
    public const string Week = "week";

    public async Task<TrendsReport> GetTrendsAsync(DateOnly from, DateOnly to, string granularity, int? applicationKey, int? safeKey)
    {
        using var connection = connectionFactory.Create();

        // @dates: the snapshot each period uses. Week groups by the Monday its
        // ISO week starts (1900-01-01 was a Monday, so DATEDIFF % 7 is the
        // days since Monday whatever SET DATEFIRST says) and takes the week's
        // last snapshot inside the range. A day with no snapshot (the nightly
        // job didn't run) is no period, not a zero.
        //
        // In Scope and Compliant use the KPI Summary's own expressions
        // (ReportsRepository.GetKpiSummaryAsync, D-143), so a snapshot taken
        // today gives today's KPI numbers.
        const string sql = """
            DECLARE @dates TABLE (SnapshotDateKey INT PRIMARY KEY, PeriodStart DATE NOT NULL, SnapshotDate DATE NOT NULL);
            DECLARE @agg TABLE (SnapshotDateKey INT NOT NULL, StageKey INT NOT NULL, StatusKey INT NOT NULL, RiskLevelKey INT NULL, AccountCount INT NOT NULL);

            INSERT INTO @dates (SnapshotDateKey, PeriodStart, SnapshotDate)
            SELECT MAX(d.DateKey), p.PeriodStart, MAX(d.FullDate)
            FROM (SELECT DISTINCT SnapshotDateKey FROM dbo.fact_account_progress_history
                  WHERE SnapshotDateKey BETWEEN @FromKey AND @ToKey) s
            JOIN dbo.dim_date d ON d.DateKey = s.SnapshotDateKey
            CROSS APPLY (SELECT CASE WHEN @IsWeek = 1
                                     THEN DATEADD(DAY, -(DATEDIFF(DAY, '19000101', d.FullDate) % 7), d.FullDate)
                                     ELSE d.FullDate END AS PeriodStart) p
            GROUP BY p.PeriodStart;

            INSERT INTO @agg (SnapshotDateKey, StageKey, StatusKey, RiskLevelKey, AccountCount)
            SELECT h.SnapshotDateKey, h.StageKey, h.StatusKey, h.RiskLevelKey, COUNT(*)
            FROM dbo.fact_account_progress_history h
            JOIN @dates dt ON dt.SnapshotDateKey = h.SnapshotDateKey
            JOIN dbo.fact_account fa ON fa.AccountKey = h.AccountKey
            LEFT JOIN dbo.dim_safe ds ON ds.SafeKey = fa.SafeKey
            LEFT JOIN dbo.fact_account_progress cur ON cur.AccountKey = fa.AccountKey
            WHERE (@SafeKey IS NULL OR fa.SafeKey = @SafeKey)
              AND (@ApplicationKey IS NULL OR ds.ApplicationKey = @ApplicationKey OR cur.ApplicationKey = @ApplicationKey)
            GROUP BY h.SnapshotDateKey, h.StageKey, h.StatusKey, h.RiskLevelKey
            OPTION (RECOMPILE);

            SELECT SnapshotDateKey, PeriodStart, SnapshotDate FROM @dates ORDER BY PeriodStart;

            SELECT
                a.SnapshotDateKey,
                SUM(a.AccountCount) AS TotalAccounts,
                SUM(CASE WHEN ISNULL(sts.StatusName, '') <> 'Risk Accepted / Excluded' THEN a.AccountCount ELSE 0 END) AS InScopeAccounts,
                SUM(CASE WHEN stg.StageOrder >= dbo.fn_MilestoneStageOrder(N'Compliant') AND sts.StatusName = 'Complete' THEN a.AccountCount ELSE 0 END) AS CompliantAccounts   -- D-284
            FROM @agg a
            JOIN dbo.dim_blueprint_stage stg ON stg.StageKey = a.StageKey
            JOIN dbo.dim_progress_status sts ON sts.StatusKey = a.StatusKey
            GROUP BY a.SnapshotDateKey;

            SELECT a.SnapshotDateKey, a.StageKey, SUM(a.AccountCount) AS AccountCount
            FROM @agg a GROUP BY a.SnapshotDateKey, a.StageKey;

            SELECT a.SnapshotDateKey, a.RiskLevelKey, SUM(a.AccountCount) AS AccountCount
            FROM @agg a GROUP BY a.SnapshotDateKey, a.RiskLevelKey;

            SELECT StageKey, StageOrder, StageName FROM dbo.dim_blueprint_stage ORDER BY StageOrder, StageKey;
            SELECT RiskLevelKey, RiskOrder, RiskLevelName FROM dbo.dim_risk_level ORDER BY RiskOrder, RiskLevelKey;
            """;

        var isWeek = string.Equals(granularity, Week, StringComparison.OrdinalIgnoreCase);
        using var grid = await connection.QueryMultipleAsync(sql, new
        {
            FromKey = DateKey(from),
            ToKey = DateKey(to),
            IsWeek = isWeek,
            ApplicationKey = applicationKey,
            SafeKey = safeKey
        });

        var dates = (await grid.ReadAsync<DateRow>()).AsList();
        var totals = (await grid.ReadAsync<TotalRow>()).ToDictionary(t => t.SnapshotDateKey);
        var stageCounts = (await grid.ReadAsync<KeyCountRow>()).AsList();
        var riskCounts = (await grid.ReadAsync<KeyCountRow>()).AsList();
        var stages = (await grid.ReadAsync<StageRow>()).AsList();
        var risks = (await grid.ReadAsync<RiskRow>()).AsList();

        var periods = dates.Select(d =>
        {
            totals.TryGetValue(d.SnapshotDateKey, out var t);
            int inScope = t?.InScopeAccounts ?? 0, compliant = t?.CompliantAccounts ?? 0;
            int Count(List<KeyCountRow> rows, int? key) =>
                rows.Where(r => r.SnapshotDateKey == d.SnapshotDateKey && r.Key == key).Sum(r => r.AccountCount);
            return new TrendsPeriod
            {
                PeriodStart = DateOnly.FromDateTime(d.PeriodStart),
                SnapshotDate = DateOnly.FromDateTime(d.SnapshotDate),
                TotalAccounts = t?.TotalAccounts ?? 0,
                InScopeAccounts = inScope,
                CompliantAccounts = compliant,
                PercentComplete = PercentComplete(compliant, inScope),
                StageCounts = stages.Select(s => Count(stageCounts, s.StageKey)).ToList(),
                RiskCounts = risks.Select(r => (int?)r.RiskLevelKey).Append(null).Select(k => Count(riskCounts, k)).ToList()
            };
        }).ToList();

        return new TrendsReport
        {
            From = from,
            To = to,
            Granularity = isWeek ? Week : Day,
            ApplicationKey = applicationKey,
            SafeKey = safeKey,
            Stages = stages.Select(s => new TrendsSeries(s.StageOrder, s.StageName)).ToList(),
            RiskLevels = risks.Select(r => new TrendsSeries(r.RiskOrder, r.RiskLevelName)).Append(new TrendsSeries(null, NotSet)).ToList(),
            Periods = periods
        };
    }

    /// <summary>Applications (web.dim_application) and the Safes that hold at least one account.</summary>
    public async Task<TrendsFilterOptions> GetFilterOptionsAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ApplicationKey, ApplicationCode, ApplicationName FROM web.dim_application ORDER BY ApplicationName, ApplicationKey;

            SELECT ds.SafeKey, ds.SafeName, src.SourceSystemName, ds.ApplicationKey
            FROM dbo.dim_safe ds
            JOIN dbo.dim_source_system src ON src.SourceSystemKey = ds.SourceSystemKey
            WHERE EXISTS (SELECT 1 FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey)
            ORDER BY ds.SafeName, src.SourceSystemName, ds.SafeKey;
            """;
        using var grid = await connection.QueryMultipleAsync(sql);
        return new TrendsFilterOptions
        {
            Applications = (await grid.ReadAsync<ApplicationSummary>()).AsList(),
            Safes = (await grid.ReadAsync<TrendsSafeOption>()).AsList()
        };
    }

    public const string NotSet = "Not set";

    /// <summary>One decimal, rounded half away from zero (as the KPI Summary export); null with nothing in scope.</summary>
    public static double? PercentComplete(int compliant, int inScope) =>
        inScope == 0 ? null : Math.Round((double)compliant / inScope * 100, 1, MidpointRounding.AwayFromZero);

    private static int DateKey(DateOnly date) => int.Parse(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private sealed class DateRow
    {
        public int SnapshotDateKey { get; init; }
        public DateTime PeriodStart { get; init; }
        public DateTime SnapshotDate { get; init; }
    }

    private sealed class TotalRow
    {
        public int SnapshotDateKey { get; init; }
        public int TotalAccounts { get; init; }
        public int InScopeAccounts { get; init; }
        public int CompliantAccounts { get; init; }
    }

    private sealed class KeyCountRow
    {
        public int SnapshotDateKey { get; init; }
        public int? StageKey { get; init; }
        public int? RiskLevelKey { get; init; }
        public int AccountCount { get; init; }
        public int? Key => StageKey ?? RiskLevelKey;
    }

    private sealed class StageRow
    {
        public int StageKey { get; init; }
        public int StageOrder { get; init; }
        public string StageName { get; init; } = "";
    }

    private sealed class RiskRow
    {
        public int RiskLevelKey { get; init; }
        public int RiskOrder { get; init; }
        public string RiskLevelName { get; init; } = "";
    }
}
