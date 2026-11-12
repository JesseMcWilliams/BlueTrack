using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Dna;

/// <summary>D-325 (63_BlueTrack_DnaAccounts.sql): the DNA accounts of the latest upload, and the upload history.</summary>
public sealed class DnaRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// Replaces the stored DNA accounts with <paramref name="rows"/> in one transaction, records the upload, and
    /// matches each account to a tracked one. Returns the new import's key.
    /// </summary>
    public async Task<int> ReplaceAsync(string fileName, int? userKey, IReadOnlyList<DnaAccountRow> rows, string sheetCounts,
        IReadOnlyList<DnaHardCodedRow>? hardCoded = null)
    {
        hardCoded ??= [];
        using var connection = (SqlConnection)connectionFactory.Create();
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("DELETE FROM web.dna_account; DELETE FROM web.dna_hard_coded_credential;", transaction: transaction);
        var importKey = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.dna_import (FileName, ImportedBy, AccountCount, SheetCounts, HardCodedCount) OUTPUT inserted.DnaImportKey
            VALUES (@fileName, @userKey, @count, @sheetCounts, @hardCodedCount)
            """, new { fileName, userKey, count = rows.Count, sheetCounts, hardCodedCount = hardCoded.Count }, transaction);

        // D-345: the hard-coded credentials, as read (their credential text masked). D-355: read straight from the list.
        await BulkInsertAsync(connection, transaction, "web.dna_hard_coded_credential", hardCoded, HardCodedColumns(importKey));
        await BulkInsertAsync(connection, transaction, "web.dna_account", rows, AccountColumns(importKey));

        // An account tracked from DNA before (D-342: source DNA, the same DNA identity, so the same machine) keeps its link;
        // otherwise, D-346 (the user's rules, 2026-11-05), by its Account Type, to a BlueTrack account from another source
        // (not deleted) with the same user name (a DOMAIN\ prefix ignored):
        //   a local account ("Local", "Database Sql Login", "SqlLogin", "Windows Local User") whose address is its machine,
        //     full or short name (SRV01 and SRV01.corp.example match either way; two different full names don't);
        //   a domain account whose domain is given ("Domain: x", "Windows Domain User: x") whose address is that domain or
        //     its first part (corp.example or CORP);
        //   D-347: an account DNA couldn't resolve (its name is its SID: a trusted domain's account, or a deleted one) to an
        //     account whose source ID is that SID (an AD discovered account accepted into BlueTrack is kept by its SID);
        //   anything else (a group, "WindowsUser", "AsymmetricKey", no Account Type) isn't linked.
        // An account tracked from DNA is never matched by name, so the same account on two hosts stays two.
        await connection.ExecuteAsync($"""
            UPDATE d SET TrackedAccountKey = COALESCE(own.AccountKey, m.AccountKey)
            FROM web.dna_account d
            CROSS APPLY (SELECT
                CASE WHEN CHARINDEX(N'\', d.AccountName) > 0 THEN SUBSTRING(d.AccountName, CHARINDEX(N'\', d.AccountName) + 1, 300) ELSE d.AccountName END AS UserName,
                CASE WHEN d.AccountType IN (N'Local', N'Database Sql Login', N'SqlLogin', N'Windows Local User') THEN 1 ELSE 0 END AS IsLocal,
                CASE WHEN d.AccountType LIKE N'Domain:%' OR d.AccountType LIKE N'Windows Domain User:%'
                     THEN NULLIF(LTRIM(RTRIM(SUBSTRING(d.AccountType, CHARINDEX(N':', d.AccountType) + 1, 300))), N'') END AS DomainGiven,
                LEFT(d.MachineName, CHARINDEX(N'.', d.MachineName + N'.') - 1) AS MachineShort
            ) k
            CROSS APPLY (SELECT CASE WHEN k.DomainGiven LIKE N'S-1-%' THEN NULL ELSE k.DomainGiven END AS Domain) dm
            OUTER APPLY (
                SELECT TOP (1) fa.AccountKey
                FROM dbo.fact_account fa
                JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey AND ss.SourceSystemCode = 'DNA'
                WHERE fa.IsDeleted = 0 AND fa.SourceAccountId = {DnaIdentitySql}
            ) own
            OUTER APPLY (
                SELECT TOP (1) fa.AccountKey
                FROM dbo.fact_account fa
                WHERE fa.IsDeleted = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.dim_source_system dss WHERE dss.SourceSystemKey = fa.SourceSystemKey AND dss.SourceSystemCode = 'DNA')
                  AND ((d.IsUnresolvedSid = 1 AND fa.SourceAccountId COLLATE Latin1_General_CI_AS = LTRIM(RTRIM(d.AccountName)) COLLATE Latin1_General_CI_AS)
                   OR (d.IsUnresolvedSid = 0 AND fa.UserName COLLATE Latin1_General_CI_AS = k.UserName COLLATE Latin1_General_CI_AS
                  AND (
                      (k.IsLocal = 1 AND d.MachineName IS NOT NULL AND (
                           fa.Address COLLATE Latin1_General_CI_AS = d.MachineName COLLATE Latin1_General_CI_AS
                        OR (CHARINDEX(N'.', fa.Address) = 0 AND fa.Address COLLATE Latin1_General_CI_AS = k.MachineShort COLLATE Latin1_General_CI_AS)
                        OR (CHARINDEX(N'.', d.MachineName) = 0 AND LEFT(fa.Address, CHARINDEX(N'.', fa.Address + N'.') - 1) COLLATE Latin1_General_CI_AS = d.MachineName COLLATE Latin1_General_CI_AS)))
                   OR (k.IsLocal = 0 AND dm.Domain IS NOT NULL AND (
                           fa.Address COLLATE Latin1_General_CI_AS = dm.Domain COLLATE Latin1_General_CI_AS
                        OR fa.Address COLLATE Latin1_General_CI_AS = LEFT(dm.Domain, CHARINDEX(N'.', dm.Domain + N'.') - 1) COLLATE Latin1_General_CI_AS)))))
                ORDER BY fa.AccountKey
            ) m
            WHERE d.DnaImportKey = @importKey AND COALESCE(own.AccountKey, m.AccountKey) IS NOT NULL
            """, new { importKey }, transaction, commandTimeout: 600);

        transaction.Commit();
        await RefreshRiskFindingsAsync(connection);
        return importKey;
    }

    /// <summary>
    /// D-349: rebuilds web.risk_finding from the DNA data now (hard-coded credentials, and the BlueTrack accounts DNA
    /// accounts are linked to); when the findings change, every risk score is marked stale for the next recalculation.
    /// </summary>
    private static Task RefreshRiskFindingsAsync(System.Data.IDbConnection connection) =>
        connection.ExecuteAsync("EXEC web.usp_DeriveRiskFindings", commandTimeout: 600);

    private static async Task BulkInsertAsync<T>(SqlConnection connection, SqlTransaction transaction, string table,
        IReadOnlyList<T> rows, IReadOnlyList<ListDataColumn<T>> columns)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = table, BulkCopyTimeout = 600 };
        foreach (var column in columns) bulk.ColumnMappings.Add(column.Name, column.Name);
        await using var reader = new ListDataReader<T>(rows, columns);
        await bulk.WriteToServerAsync(reader);
    }

    private static ListDataColumn<DnaAccountRow>[] AccountColumns(int importKey) =>
    [
        new("DnaImportKey", typeof(int), _ => importKey), new("SheetCode", typeof(string), r => r.SheetCode),
        new("IsDomainAccount", typeof(bool), r => r.IsDomainAccount), new("MachineName", typeof(string), r => r.MachineName),
        new("AccountName", typeof(string), r => r.AccountName), new("DisplayName", typeof(string), r => r.DisplayName),
        new("AccountType", typeof(string), r => r.AccountType), new("AccountCategory", typeof(string), r => r.AccountCategory),
        new("AccountGroup", typeof(string), r => r.AccountGroup), new("PrivilegedDomainGroup", typeof(string), r => r.PrivilegedDomainGroup),
        new("InstanceName", typeof(string), r => r.InstanceName), new("DatabaseName", typeof(string), r => r.DatabaseName),
        new("ComplianceStatus", typeof(string), r => r.ComplianceStatus), new("AccountState", typeof(string), r => r.AccountState),
        new("PasswordNeverExpires", typeof(bool), r => r.PasswordNeverExpires), new("PasswordAgeDays", typeof(int), r => r.PasswordAgeDays),
        new("PasswordLastSet", typeof(DateTime), r => r.PasswordLastSet), new("LastLoginDate", typeof(DateTime), r => r.LastLoginDate),
        new("AccountExpirationDate", typeof(DateTime), r => r.AccountExpirationDate), new("MachineType", typeof(string), r => r.MachineType),
        new("OsVersion", typeof(string), r => r.OsVersion), new("AccountDescription", typeof(string), r => r.AccountDescription),
        new("ThreatCause", typeof(string), r => r.ThreatCause), new("PassTheHashVulnerable", typeof(string), r => r.PassTheHashVulnerable),
        new("PassTheHashHashFound", typeof(string), r => r.PassTheHashHashFound), new("ServiceAccountType", typeof(string), r => r.ServiceAccountType),
        new("ServiceAccountDescription", typeof(string), r => r.ServiceAccountDescription), new("SpnDescription", typeof(string), r => r.SpnDescription),
        new("Roles", typeof(string), r => r.Roles), new("DbPermissions", typeof(string), r => r.DbPermissions),
        new("Details", typeof(string), r => r.Details), new("LocalMappedAccount", typeof(string), r => r.LocalMappedAccount),
        new("IsUnresolvedSid", typeof(bool), r => r.IsUnresolvedSid)
    ];

    private static ListDataColumn<DnaHardCodedRow>[] HardCodedColumns(int importKey) =>
    [
        new("DnaImportKey", typeof(int), _ => importKey), new("SheetCode", typeof(string), h => h.SheetCode),
        new("MachineName", typeof(string), h => h.MachineName), new("MachineType", typeof(string), h => h.MachineType),
        new("ApplicationServer", typeof(string), h => h.ApplicationServer), new("ApplicationName", typeof(string), h => h.ApplicationName),
        new("SiteName", typeof(string), h => h.SiteName), new("AccountName", typeof(string), h => h.AccountName),
        new("HardCodedInFile", typeof(string), h => h.HardCodedInFile), new("CredentialLocation", typeof(string), h => h.CredentialLocation),
        new("PasswordLength", typeof(int), h => h.PasswordLength), new("TargetSystemAddress", typeof(string), h => h.TargetSystemAddress),
        new("TargetSystemType", typeof(string), h => h.TargetSystemType), new("OsVersion", typeof(string), h => h.OsVersion),
        new("Details", typeof(string), h => h.Details)
    ];

    /// <summary>D-345: the page's sortable columns of the hard-coded credentials.</summary>
    public static readonly IReadOnlyDictionary<string, string> HardCodedSortable = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MachineName"] = "h.MachineName", ["ApplicationName"] = "h.ApplicationName", ["AccountName"] = "h.AccountName",
        ["HardCodedInFile"] = "h.HardCodedInFile", ["PasswordLength"] = "h.PasswordLength",
        ["TargetSystemAddress"] = "h.TargetSystemAddress", ["TargetSystemType"] = "h.TargetSystemType"
    };

    /// <summary>
    /// D-345: the hard-coded credentials of the latest import: one page (<paramref name="pageSize"/> null: every match, for an
    /// export), searched in machine, application, account, file and target, sorted by <paramref name="sort"/> then machine and file.
    /// </summary>
    public async Task<(IReadOnlyList<DnaHardCoded> Rows, int Matching, int Total)> GetHardCodedAsync(string? search, string? sort, int page, int? pageSize)
    {
        using var connection = connectionFactory.Create();
        var chosen = SortParser.Parse(sort)
            .Where(s => HardCodedSortable.ContainsKey(s.Field))
            .Select(s => (Sql: HardCodedSortable[s.Field], s.Descending))
            .DistinctBy(s => s.Sql)
            .ToList();
        var order = string.Join(", ", chosen.Select(s => s.Sql + (s.Descending ? " DESC" : " ASC"))
            .Concat(new[] { "h.MachineName", "h.HardCodedInFile", "h.DnaHardCodedKey" }.Where(c => chosen.All(s => s.Sql != c))));
        const string filter = """
            FROM web.dna_hard_coded_credential h
            WHERE @search IS NULL OR h.MachineName LIKE @search OR h.ApplicationName LIKE @search OR h.AccountName LIKE @search
               OR h.HardCodedInFile LIKE @search OR h.TargetSystemAddress LIKE @search
            """;
        // D-355: sort and page the keys only, then read the page's wide columns, so SQL Server's memory grant is sized for
        // the sort columns, not every column's declared width.
        var paging = pageSize is null ? "" : $"ORDER BY {order} OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY";
        using var multi = await connection.QueryMultipleAsync($"""
            SELECT h.DnaHardCodedKey, h.SheetCode, h.MachineName, h.MachineType, h.ApplicationServer, h.ApplicationName, h.SiteName, h.AccountName,
                   h.HardCodedInFile, h.CredentialLocation, h.PasswordLength, h.TargetSystemAddress, h.TargetSystemType, h.OsVersion, h.Details
            FROM (
                SELECT h.DnaHardCodedKey, ROW_NUMBER() OVER (ORDER BY {order}) AS Seq
                {filter}
                {paging}
            ) k
            JOIN web.dna_hard_coded_credential h ON h.DnaHardCodedKey = k.DnaHardCodedKey
            ORDER BY k.Seq;
            SELECT COUNT(*) {filter};
            SELECT COUNT(*) FROM web.dna_hard_coded_credential;
            """, new
        {
            search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%",
            offset = (page - 1) * (pageSize ?? 0),
            pageSize
        });
        var rows = (await multi.ReadAsync<DnaHardCoded>()).AsList();
        return (rows, await multi.ReadSingleAsync<int>(), await multi.ReadSingleAsync<int>());
    }

    /// <summary>
    /// D-342: a DNA account's identity, the SourceAccountId of the account tracked from it: its machine and name, and for a
    /// database account also its instance and database. A domain account found on two machines is two accounts (the user,
    /// 2026-11-05: separate, never joined); the same machine and name on two sheets (a Windows scan and a domain scan) is one.
    /// Used against web.dna_account aliased d.
    /// </summary>
    internal const string DnaIdentitySql = """
        LEFT(CONCAT(ISNULL(d.MachineName, N'') + N'|', d.AccountName,
                    CASE WHEN d.InstanceName IS NOT NULL THEN N'|' + d.InstanceName + ISNULL(N'|' + d.DatabaseName, N'') END), 400)
        """;

    /// <summary>
    /// D-342: tracks DNA accounts in BlueTrack: each one not tracked yet becomes an account (source DNA) at the first
    /// stage, Not Started, unless an account from the same DNA identity exists (tracked before, then the DNA row was
    /// replaced by a new import), and the DNA rows link to it. Returns how many DNA rows were newly linked.
    /// </summary>
    public async Task<int> TrackAsync(IReadOnlyCollection<int> dnaAccountKeys, string trackedBy)
    {
        if (dnaAccountKeys.Count == 0) return 0;
        using var connection = (SqlConnection)connectionFactory.Create();
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("CREATE TABLE #keys (DnaAccountKey INT PRIMARY KEY);", transaction: transaction);
        var keys = new DataTable();
        keys.Columns.Add("DnaAccountKey", typeof(int));
        foreach (var k in dnaAccountKeys.Distinct()) keys.Rows.Add(k);
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#keys" })
        {
            await bulk.WriteToServerAsync(keys);
        }
        var linked = await connection.ExecuteScalarAsync<int>($"""
            DECLARE @dna INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'DNA');
            DECLARE @stage INT = dbo.fn_StartStageKey();
            DECLARE @status INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Not Started');
            IF @dna IS NULL THROW 50000, 'The DNA source system is missing (Database/70).', 1;

            SELECT d.DnaAccountKey, {DnaIdentitySql} AS SourceId,
                   CASE WHEN CHARINDEX(N'\', d.AccountName) > 0 THEN SUBSTRING(d.AccountName, CHARINDEX(N'\', d.AccountName) + 1, 300) ELSE d.AccountName END AS UserName,
                   d.MachineName AS Address,
                   COALESCE(CASE WHEN d.AccountType LIKE N'Domain:%' THEN LTRIM(SUBSTRING(d.AccountType, 8, 200)) END,
                            CASE WHEN CHARINDEX(N'\', d.AccountName) > 0 THEN LEFT(d.AccountName, CHARINDEX(N'\', d.AccountName) - 1) END) AS Domain,
                   LEFT(CASE WHEN d.MachineName IS NULL THEN d.AccountName
                             ELSE d.AccountName + N' on ' + d.MachineName + ISNULL(N'\' + d.InstanceName, N'') END, 300) AS DisplayName
            INTO #c
            FROM web.dna_account d
            JOIN #keys k ON k.DnaAccountKey = d.DnaAccountKey
            WHERE d.TrackedAccountKey IS NULL;

            INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, PlatformLogonDomain, IsDeleted, CreatedDate)
            SELECT @dna, c.SourceId, MAX(c.DisplayName), MAX(c.UserName), MAX(c.Address), MAX(c.Domain), 0, CAST(SYSUTCDATETIME() AS DATE)
            FROM #c c
            WHERE NOT EXISTS (SELECT 1 FROM dbo.fact_account fa WHERE fa.SourceSystemKey = @dna AND fa.SourceAccountId = c.SourceId)
            GROUP BY c.SourceId;

            INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
            SELECT fa.AccountKey, @stage, @status
            FROM dbo.fact_account fa
            WHERE fa.SourceSystemKey = @dna AND fa.SourceAccountId IN (SELECT SourceId FROM #c)
              AND NOT EXISTS (SELECT 1 FROM dbo.fact_account_progress p WHERE p.AccountKey = fa.AccountKey);

            UPDATE d SET TrackedAccountKey = fa.AccountKey, TrackedBy = @trackedBy
            FROM web.dna_account d
            JOIN #c c ON c.DnaAccountKey = d.DnaAccountKey
            JOIN dbo.fact_account fa ON fa.SourceSystemKey = @dna AND fa.SourceAccountId = c.SourceId;
            SELECT @@ROWCOUNT;
            """, new { trackedBy = trackedBy.Length > 250 ? trackedBy[..250] : trackedBy }, transaction, commandTimeout: 600);
        transaction.Commit();
        await RefreshRiskFindingsAsync(connection);
        return linked;
    }

    /// <summary>
    /// D-342: the DNA rows not tracked yet that each enabled tracking rule matches (all its conditions), in rule order.
    /// A rule with no condition matches nothing. Fields as <see cref="DnaTrackRuleFields"/>.
    /// </summary>
    public async Task<IReadOnlyList<(int RuleKey, string RuleName, int DnaAccountKey)>> GetRuleMatchesAsync(int? onlyRuleKey = null)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<(int, string, int)>($"""
            SELECT r.DnaTrackRuleKey, r.RuleName, d.DnaAccountKey
            FROM web.dna_track_rule r
            JOIN web.dna_account d ON d.TrackedAccountKey IS NULL
            WHERE (@onlyRuleKey IS NULL AND r.IsEnabled = 1 OR r.DnaTrackRuleKey = @onlyRuleKey)
              AND EXISTS (SELECT 1 FROM web.dna_track_rule_condition c0 WHERE c0.DnaTrackRuleKey = r.DnaTrackRuleKey)
              AND NOT EXISTS (
                  SELECT 1 FROM web.dna_track_rule_condition c
                  OUTER APPLY (SELECT f.TextValue, f.DateValue FROM ({DnaTrackRuleFields.ValuesSql}) f(FieldName, TextValue, DateValue) WHERE f.FieldName = c.FieldName) fv
                  WHERE c.DnaTrackRuleKey = r.DnaTrackRuleKey
                    AND dbo.fn_ProgressConditionMatches(fv.TextValue, fv.DateValue, c.Operator, c.Value) = 0)
            ORDER BY r.DnaTrackRuleKey, d.DnaAccountKey
            """, new { onlyRuleKey }, commandTimeout: 300)).ToList();
    }

    /// <summary>D-343: deletes every DNA account and import. Returns the accounts deleted.</summary>
    public async Task<int> PurgeAllAsync()
    {
        using var connection = connectionFactory.Create();
        var deleted = await connection.ExecuteScalarAsync<int>("""
            DECLARE @n INT = (SELECT COUNT(*) FROM web.dna_account);
            DELETE FROM web.dna_account;
            DELETE FROM web.dna_hard_coded_credential;
            DELETE FROM web.dna_import;
            SELECT @n;
            """, commandTimeout: 300);
        await RefreshRiskFindingsAsync(connection);
        return deleted;
    }

    /// <summary>D-343: deletes the imports (and their accounts) made more than <paramref name="days"/> days ago. Returns (imports, accounts) deleted.</summary>
    public async Task<(int Imports, int Accounts)> PurgeOlderThanAsync(int days)
    {
        using var connection = connectionFactory.Create();
        var purged = await connection.QuerySingleAsync<(int, int)>("""
            DECLARE @before DATETIME2 = DATEADD(DAY, -@days, SYSUTCDATETIME());
            DELETE d FROM web.dna_account d JOIN web.dna_import i ON i.DnaImportKey = d.DnaImportKey WHERE i.ImportedAt < @before;
            DECLARE @accounts INT = @@ROWCOUNT;
            DELETE h FROM web.dna_hard_coded_credential h JOIN web.dna_import i ON i.DnaImportKey = h.DnaImportKey WHERE i.ImportedAt < @before;
            DELETE FROM web.dna_import WHERE ImportedAt < @before;
            SELECT @@ROWCOUNT, @accounts;
            """, new { days }, commandTimeout: 300);
        if (purged.Item1 > 0) await RefreshRiskFindingsAsync(connection);
        return purged;
    }

    /// <summary>D-343, D-344: the retention period and the columns only ViewDnaSensitiveColumns may see.</summary>
    public async Task<DnaSettings> GetSettingsAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT TOP (1) DnaRetentionDays FROM web.app_config;
            SELECT ColumnName FROM web.dna_column_setting WHERE IsRestricted = 1;
            """);
        var days = await multi.ReadSingleOrDefaultAsync<int?>();
        var restricted = (await multi.ReadAsync<string>()).Where(DnaColumns.Restrictable.Contains).ToHashSet(StringComparer.Ordinal);
        return new DnaSettings { RetentionDays = days, RestrictedColumns = DnaColumns.Restrictable.Where(restricted.Contains).ToList() };
    }

    public async Task SaveSettingsAsync(int? retentionDays, IReadOnlyCollection<string> restrictedColumns)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("UPDATE web.app_config SET DnaRetentionDays = @retentionDays;", new { retentionDays }, transaction);
        await connection.ExecuteAsync("DELETE FROM web.dna_column_setting;", transaction: transaction);
        await connection.ExecuteAsync("INSERT INTO web.dna_column_setting (ColumnName, IsRestricted) VALUES (@c, 1);",
            restrictedColumns.Select(c => new { c }), transaction);
        transaction.Commit();
    }

    public async Task<DnaImportSummary?> GetLatestImportAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<DnaImportSummary>("""
            SELECT TOP (1) i.DnaImportKey, i.FileName, i.ImportedAt, u.DisplayName AS ImportedByName, i.AccountCount, i.SheetCounts, i.HardCodedCount,
                   (SELECT COUNT(*) FROM web.dna_account d WHERE d.DnaImportKey = i.DnaImportKey AND d.TrackedAccountKey IS NOT NULL) AS TrackedCount
            FROM web.dna_import i
            LEFT JOIN web.app_user u ON u.UserKey = i.ImportedBy
            ORDER BY i.DnaImportKey DESC
            """);
    }

    /// <summary>
    /// One page of the DNA accounts. D-344: a column in <paramref name="hidden"/> (restricted, and the caller lacks
    /// ViewDnaSensitiveColumns) is neither searched nor sorted on, and the controller blanks it. D-341: sorted by
    /// <paramref name="sort"/> (field:asc|desc, from <see cref="DnaColumns.Sortable"/>), then by finding, machine and account.
    /// </summary>
    public async Task<(IReadOnlyList<DnaAccount> Rows, int Matching, int Total)> GetPageAsync(string? sheet, bool? tracked, string? search, int page, int pageSize,
        string? sort = null, IReadOnlySet<string>? hidden = null, bool? unresolvedSid = null)
    {
        hidden ??= new HashSet<string>();
        using var connection = connectionFactory.Create();
        var parameters = new
        {
            sheet = string.IsNullOrWhiteSpace(sheet) ? null : sheet.Trim(),
            tracked,
            unresolvedSid,
            search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%",
            offset = (page - 1) * pageSize,
            pageSize
        };
        var searched = new[] { "AccountName", "MachineName", "DisplayName" }.Where(c => !hidden.Contains(c)).Select(c => $"d.{c} LIKE @search");
        var filter = $"""
            FROM web.dna_account d
            LEFT JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey
            WHERE (@sheet IS NULL OR d.SheetCode = @sheet)
              AND (@tracked IS NULL OR (@tracked = 1 AND d.TrackedAccountKey IS NOT NULL) OR (@tracked = 0 AND d.TrackedAccountKey IS NULL))
              AND (@unresolvedSid IS NULL OR d.IsUnresolvedSid = @unresolvedSid)
              AND (@search IS NULL OR {string.Join(" OR ", searched)})
            """;
        // The chosen columns (once each), then the default order's columns not already in it.
        var chosen = SortParser.Parse(sort)
            .Where(s => DnaColumns.Sortable.ContainsKey(s.Field) && !hidden.Contains(s.Field))
            .Select(s => (Sql: DnaColumns.Sortable[s.Field], s.Descending))
            .DistinctBy(s => s.Sql)
            .ToList();
        var order = string.Join(", ", chosen.Select(s => s.Sql + (s.Descending ? " DESC" : " ASC"))
            .Concat(new[] { "d.SheetCode", "d.MachineName", "d.AccountName", "d.DnaAccountKey" }.Where(c => chosen.All(s => s.Sql != c))));
        // D-355: sort and page the keys only, then read the page's wide columns, so SQL Server's memory grant is sized for
        // the sort columns, not every column's declared width.
        using var multi = await connection.QueryMultipleAsync($"""
            SELECT d.DnaAccountKey, d.SheetCode, d.IsDomainAccount, d.MachineName, d.AccountName, d.DisplayName, d.AccountType, d.AccountCategory,
                   d.AccountGroup, d.PrivilegedDomainGroup, d.InstanceName, d.DatabaseName, d.ComplianceStatus, d.AccountState,
                   d.PasswordNeverExpires, d.PasswordAgeDays, d.PasswordLastSet, d.LastLoginDate, d.AccountExpirationDate,
                   d.MachineType, d.OsVersion, d.AccountDescription, d.ThreatCause, d.PassTheHashVulnerable, d.PassTheHashHashFound,
                   d.ServiceAccountType, d.ServiceAccountDescription, d.SpnDescription, d.Roles, d.DbPermissions, d.Details, d.LocalMappedAccount,
                   d.TrackedAccountKey, fa.AccountName AS TrackedAccountName, d.TrackedBy, d.IsUnresolvedSid
            FROM (
                SELECT d.DnaAccountKey, ROW_NUMBER() OVER (ORDER BY {order}) AS Seq
                {filter}
                ORDER BY {order}
                OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            ) k
            JOIN web.dna_account d ON d.DnaAccountKey = k.DnaAccountKey
            LEFT JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey
            ORDER BY k.Seq;
            SELECT COUNT(*) {filter};
            SELECT COUNT(*) FROM web.dna_account;
            """, parameters);
        var rows = (await multi.ReadAsync<DnaAccount>()).AsList();
        return (rows, await multi.ReadSingleAsync<int>(), await multi.ReadSingleAsync<int>());
    }

    /// <summary>The findings (sheets) of the latest upload, for the page's filter.</summary>
    public async Task<IReadOnlyList<string>> GetSheetsAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<string>("SELECT SheetCode FROM web.dna_account GROUP BY SheetCode ORDER BY MIN(DnaAccountKey)")).AsList();
    }
}

public sealed class DnaImportSummary
{
    public int DnaImportKey { get; init; }
    public required string FileName { get; init; }
    public DateTime ImportedAt { get; init; }
    public string? ImportedByName { get; init; }
    public int AccountCount { get; init; }
    public string? SheetCounts { get; init; }
    /// <summary>D-345: the hard-coded credentials the import held (null for an import before D-345).</summary>
    public int? HardCodedCount { get; init; }
    public int TrackedCount { get; init; }
}

/// <summary>D-345: one hard-coded credential DNA found (web.dna_hard_coded_credential); CredentialLocation is masked.</summary>
public sealed class DnaHardCoded
{
    public int DnaHardCodedKey { get; init; }
    public required string SheetCode { get; init; }
    public string? MachineName { get; init; }
    public string? MachineType { get; init; }
    public string? ApplicationServer { get; init; }
    public string? ApplicationName { get; init; }
    public string? SiteName { get; init; }
    public string? AccountName { get; init; }
    public string? HardCodedInFile { get; init; }
    public string? CredentialLocation { get; init; }
    public int? PasswordLength { get; init; }
    public string? TargetSystemAddress { get; init; }
    public string? TargetSystemType { get; init; }
    public string? OsVersion { get; init; }
    public string? Details { get; init; }
}

public sealed class DnaAccount
{
    public int DnaAccountKey { get; init; }
    public required string SheetCode { get; init; }
    public bool IsDomainAccount { get; init; }
    public string? MachineName { get; init; }
    public required string AccountName { get; init; }
    public string? DisplayName { get; init; }
    public string? AccountType { get; init; }
    public string? AccountCategory { get; init; }
    public string? AccountGroup { get; init; }
    public string? PrivilegedDomainGroup { get; init; }
    public string? InstanceName { get; init; }
    public string? DatabaseName { get; init; }
    public string? ComplianceStatus { get; init; }
    public string? AccountState { get; init; }
    public bool? PasswordNeverExpires { get; init; }
    public int? PasswordAgeDays { get; init; }
    public DateTime? PasswordLastSet { get; init; }
    public DateTime? LastLoginDate { get; init; }
    public DateTime? AccountExpirationDate { get; init; }
    public string? MachineType { get; init; }
    public string? OsVersion { get; init; }
    public string? AccountDescription { get; init; }
    public string? ThreatCause { get; init; }
    public string? PassTheHashVulnerable { get; init; }
    public string? PassTheHashHashFound { get; init; }
    public string? ServiceAccountType { get; init; }
    public string? ServiceAccountDescription { get; init; }
    public string? SpnDescription { get; init; }
    public string? Roles { get; init; }
    public string? DbPermissions { get; init; }
    public string? Details { get; init; }
    public string? LocalMappedAccount { get; init; }
    public long? TrackedAccountKey { get; init; }
    public string? TrackedAccountName { get; init; }
    /// <summary>D-342: who tracked it (a person, or "Rule: name"); null when it matched a BlueTrack account at import.</summary>
    public string? TrackedBy { get; init; }
    /// <summary>D-347: DNA couldn't resolve the account (its name is its SID): a trusted domain's account, or a deleted one.</summary>
    public bool IsUnresolvedSid { get; init; }
}

/// <summary>D-343, D-344: the DNA settings on Admin > Data Sources.</summary>
public sealed class DnaSettings
{
    public int? RetentionDays { get; init; }
    public IReadOnlyList<string> RestrictedColumns { get; init; } = [];
}
