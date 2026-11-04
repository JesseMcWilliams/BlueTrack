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
    public async Task<int> ReplaceAsync(string fileName, int? userKey, IReadOnlyList<DnaAccountRow> rows, string sheetCounts)
    {
        using var connection = (SqlConnection)connectionFactory.Create();
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("DELETE FROM web.dna_account;", transaction: transaction);
        var importKey = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.dna_import (FileName, ImportedBy, AccountCount, SheetCounts) OUTPUT inserted.DnaImportKey
            VALUES (@fileName, @userKey, @count, @sheetCounts)
            """, new { fileName, userKey, count = rows.Count, sheetCounts }, transaction);

        var table = new DataTable();
        foreach (var (name, type) in Columns) table.Columns.Add(name, type);
        foreach (var r in rows)
        {
            table.Rows.Add(importKey, r.SheetCode, r.IsDomainAccount, (object?)r.MachineName ?? DBNull.Value, r.AccountName,
                (object?)r.DisplayName ?? DBNull.Value, (object?)r.AccountType ?? DBNull.Value, (object?)r.AccountCategory ?? DBNull.Value,
                (object?)r.AccountGroup ?? DBNull.Value, (object?)r.PrivilegedDomainGroup ?? DBNull.Value, (object?)r.InstanceName ?? DBNull.Value,
                (object?)r.DatabaseName ?? DBNull.Value, (object?)r.ComplianceStatus ?? DBNull.Value, (object?)r.AccountState ?? DBNull.Value,
                (object?)r.PasswordNeverExpires ?? DBNull.Value, (object?)r.PasswordAgeDays ?? DBNull.Value, (object?)r.PasswordLastSet ?? DBNull.Value,
                (object?)r.LastLoginDate ?? DBNull.Value, (object?)r.AccountExpirationDate ?? DBNull.Value);
        }
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "web.dna_account", BulkCopyTimeout = 600 })
        {
            foreach (var (name, _) in Columns) bulk.ColumnMappings.Add(name, name);
            await bulk.WriteToServerAsync(table);
        }

        // A tracked account (not deleted) with the same user name (a DOMAIN\ prefix ignored); a local or database
        // account also on the same machine (its address, or the address's first label).
        await connection.ExecuteAsync("""
            UPDATE d SET TrackedAccountKey = m.AccountKey
            FROM web.dna_account d
            CROSS APPLY (
                SELECT TOP (1) fa.AccountKey
                FROM dbo.fact_account fa
                WHERE fa.IsDeleted = 0
                  AND fa.UserName = CASE WHEN CHARINDEX(N'\', d.AccountName) > 0 THEN SUBSTRING(d.AccountName, CHARINDEX(N'\', d.AccountName) + 1, 300) ELSE d.AccountName END
                  AND (d.IsDomainAccount = 1 OR d.MachineName IS NULL
                       OR fa.Address = d.MachineName OR fa.Address LIKE d.MachineName + N'.%')
                ORDER BY fa.AccountKey
            ) m
            WHERE d.DnaImportKey = @importKey
            """, new { importKey }, transaction, commandTimeout: 600);

        transaction.Commit();
        return importKey;
    }

    private static readonly (string Name, Type Type)[] Columns =
    [
        ("DnaImportKey", typeof(int)), ("SheetCode", typeof(string)), ("IsDomainAccount", typeof(bool)), ("MachineName", typeof(string)),
        ("AccountName", typeof(string)), ("DisplayName", typeof(string)), ("AccountType", typeof(string)), ("AccountCategory", typeof(string)),
        ("AccountGroup", typeof(string)), ("PrivilegedDomainGroup", typeof(string)), ("InstanceName", typeof(string)), ("DatabaseName", typeof(string)),
        ("ComplianceStatus", typeof(string)), ("AccountState", typeof(string)), ("PasswordNeverExpires", typeof(bool)), ("PasswordAgeDays", typeof(int)),
        ("PasswordLastSet", typeof(DateTime)), ("LastLoginDate", typeof(DateTime)), ("AccountExpirationDate", typeof(DateTime))
    ];

    public async Task<DnaImportSummary?> GetLatestImportAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<DnaImportSummary>("""
            SELECT TOP (1) i.DnaImportKey, i.FileName, i.ImportedAt, u.DisplayName AS ImportedByName, i.AccountCount, i.SheetCounts,
                   (SELECT COUNT(*) FROM web.dna_account d WHERE d.DnaImportKey = i.DnaImportKey AND d.TrackedAccountKey IS NOT NULL) AS TrackedCount
            FROM web.dna_import i
            LEFT JOIN web.app_user u ON u.UserKey = i.ImportedBy
            ORDER BY i.DnaImportKey DESC
            """);
    }

    private const string FilterSql = """
        FROM web.dna_account d
        LEFT JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey
        WHERE (@sheet IS NULL OR d.SheetCode = @sheet)
          AND (@tracked IS NULL OR (@tracked = 1 AND d.TrackedAccountKey IS NOT NULL) OR (@tracked = 0 AND d.TrackedAccountKey IS NULL))
          AND (@search IS NULL OR d.AccountName LIKE @search OR d.MachineName LIKE @search OR d.DisplayName LIKE @search)
        """;

    /// <summary>One page of the DNA accounts, by finding, machine and account.</summary>
    public async Task<(IReadOnlyList<DnaAccount> Rows, int Matching, int Total)> GetPageAsync(string? sheet, bool? tracked, string? search, int page, int pageSize)
    {
        using var connection = connectionFactory.Create();
        var parameters = new
        {
            sheet = string.IsNullOrWhiteSpace(sheet) ? null : sheet.Trim(),
            tracked,
            search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%",
            offset = (page - 1) * pageSize,
            pageSize
        };
        using var multi = await connection.QueryMultipleAsync($"""
            SELECT d.DnaAccountKey, d.SheetCode, d.IsDomainAccount, d.MachineName, d.AccountName, d.DisplayName, d.AccountType, d.AccountCategory,
                   d.AccountGroup, d.PrivilegedDomainGroup, d.InstanceName, d.DatabaseName, d.ComplianceStatus, d.AccountState,
                   d.PasswordNeverExpires, d.PasswordAgeDays, d.PasswordLastSet, d.LastLoginDate, d.AccountExpirationDate,
                   d.TrackedAccountKey, fa.AccountName AS TrackedAccountName
            {FilterSql}
            ORDER BY d.SheetCode, d.MachineName, d.AccountName, d.DnaAccountKey
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            SELECT COUNT(*) {FilterSql};
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
    public int TrackedCount { get; init; }
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
    public long? TrackedAccountKey { get; init; }
    public string? TrackedAccountName { get; init; }
}
