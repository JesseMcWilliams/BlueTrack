using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Indexes added for the nightly pull and Load after a customer's resource report (2026-11-18). Each one turns a
/// scan-per-row into a seek-per-row on a staging table that the pull or the Load joins on.
/// </summary>
public class PerformanceIndexesTests
{
    [Theory]
    [InlineData("dbo.stg_shd_accounts", "IX_stg_shd_accounts_AccountId", "AccountId")]   // D-368
    public async Task TheIndexExists_OnTheJoinColumns(string table, string index, string keyColumns)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var keys = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @index
            """, new { table, index });
        Assert.Equal(keyColumns, keys);
    }
}
