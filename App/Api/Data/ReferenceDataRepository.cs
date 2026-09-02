using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the field-metadata-driven Account Progress edit form's Dropdown
/// fields (Design_Interface_Extensibility.md) -- one query per reference
/// table named in account_progress_field_metadata.ReferenceTable. A fixed
/// switch over a small known set of tables, not dynamic SQL: ReferenceTable
/// values come from our own seeded metadata, not user input, but column
/// names differ per table (StageKey/StageName vs SORKey/SORName, etc.), so
/// there's no single generic query shape anyway.
/// </summary>
public sealed class ReferenceDataRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyDictionary<string, string> QueriesByTable = new Dictionary<string, string>
    {
        ["dim_blueprint_stage"] = "SELECT StageKey AS [Key], StageName AS [Name] FROM dbo.dim_blueprint_stage ORDER BY StageOrder",
        ["dim_progress_status"] = "SELECT StatusKey AS [Key], StatusName AS [Name] FROM dbo.dim_progress_status ORDER BY StatusName",
        ["dim_risk_level"] = "SELECT RiskLevelKey AS [Key], RiskLevelName AS [Name] FROM dbo.dim_risk_level ORDER BY RiskOrder",
        ["dim_account_type"] = "SELECT AccountTypeKey AS [Key], AccountTypeName AS [Name] FROM dbo.dim_account_type ORDER BY AccountTypeName",
        ["dim_source_of_record"] = "SELECT SORKey AS [Key], SORName AS [Name] FROM dbo.dim_source_of_record ORDER BY SORName"
    };

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DropdownOption>>> GetAllReferenceDataAsync()
    {
        using var connection = connectionFactory.Create();

        var result = new Dictionary<string, IReadOnlyList<DropdownOption>>();
        foreach (var (table, sql) in QueriesByTable)
        {
            var rows = await connection.QueryAsync<DropdownOption>(sql);
            result[table] = rows.AsList();
        }
        return result;
    }
}
