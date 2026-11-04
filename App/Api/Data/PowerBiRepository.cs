using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-314: the Power BI access page. The objects and relationships are the ones the reporting views were built for
/// (02_BlueTrack_Baseline_EtlLoads.sql: the views keep keys so Power BI's model relates them to the dimension
/// tables, D-22); the columns are read from the database, so the page lists what is there now. An object the
/// database doesn't have is left out.
/// </summary>
public sealed class PowerBiRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly (string Schema, string Name, string Kind, string Description)[] Objects =
    [
        ("dbo", "vw_powerbi_accounts", "Fact", "One row per account, as it is now: its progress (stage, status, risk level, dates) and CPM state, with keys to the dimension tables."),
        ("dbo", "vw_powerbi_entitlements", "Fact", "One row per Safe, member and permission: who may do what in which Safe."),
        ("dbo", "fact_account_progress_history", "Fact", "One row per account per daily snapshot: stage, status and risk level over time, for trends."),
        ("dbo", "dim_source_system", "Dimension", "Where an account came from (Privilege Cloud, Self-Hosted, a feed)."),
        ("dbo", "dim_platform", "Dimension", "CyberArk platforms."),
        ("dbo", "dim_safe", "Dimension", "CyberArk Safes, with the BlueTrack application mapped to each."),
        ("web", "dim_application", "Dimension", "BlueTrack applications."),
        ("dbo", "dim_blueprint_stage", "Dimension", "Blueprint stages, in order (StageOrder)."),
        ("dbo", "dim_progress_status", "Dimension", "Progress statuses."),
        ("dbo", "dim_risk_level", "Dimension", "Risk levels."),
        ("dbo", "dim_account_type", "Dimension", "Account types."),
        ("dbo", "dim_source_of_record", "Dimension", "Sources of record."),
        ("dbo", "dim_user", "Dimension", "Vault users."),
        ("dbo", "dim_group", "Dimension", "Vault groups."),
        ("dbo", "dim_permission", "Dimension", "Safe permissions."),
        ("dbo", "dim_date", "Dimension", "One row per day (DateKey is yyyymmdd)."),
    ];

    private static readonly PowerBiRelationship[] Relationships =
    [
        new("vw_powerbi_accounts", "SourceSystemKey", "dim_source_system", "SourceSystemKey"),
        new("vw_powerbi_accounts", "PlatformKey", "dim_platform", "PlatformKey"),
        new("vw_powerbi_accounts", "SafeKey", "dim_safe", "SafeKey"),
        new("vw_powerbi_accounts", "CurrentStageKey", "dim_blueprint_stage", "StageKey"),
        new("vw_powerbi_accounts", "CurrentStatusKey", "dim_progress_status", "StatusKey"),
        new("vw_powerbi_accounts", "RiskLevelKey", "dim_risk_level", "RiskLevelKey"),
        new("vw_powerbi_accounts", "AccountTypeKey", "dim_account_type", "AccountTypeKey"),
        new("vw_powerbi_accounts", "SORKey", "dim_source_of_record", "SORKey"),
        new("vw_powerbi_entitlements", "SafeKey", "dim_safe", "SafeKey"),
        new("vw_powerbi_entitlements", "UserKey", "dim_user", "UserKey"),
        new("vw_powerbi_entitlements", "GroupKey", "dim_group", "GroupKey"),
        new("vw_powerbi_entitlements", "PermissionKey", "dim_permission", "PermissionKey"),
        new("fact_account_progress_history", "SnapshotDateKey", "dim_date", "DateKey"),
        new("fact_account_progress_history", "AccountKey", "vw_powerbi_accounts", "AccountKey"),
        new("fact_account_progress_history", "StageKey", "dim_blueprint_stage", "StageKey"),
        new("fact_account_progress_history", "StatusKey", "dim_progress_status", "StatusKey"),
        new("fact_account_progress_history", "RiskLevelKey", "dim_risk_level", "RiskLevelKey"),
        new("dim_safe", "ApplicationKey", "dim_application", "ApplicationKey"),
    ];

    public async Task<PowerBiAccess> GetAsync()
    {
        using var connection = connectionFactory.Create();
        var (server, database) = await connection.QuerySingleAsync<(string, string)>(
            "SELECT CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(256)), DB_NAME()");
        var columns = (await connection.QueryAsync<(string Schema, string Table, string Column)>("""
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
            """)).ToLookup(c => (c.Schema.ToLowerInvariant(), c.Table.ToLowerInvariant()), c => c.Column);

        var objects = Objects
            .Where(o => columns.Contains((o.Schema, o.Name.ToLowerInvariant())))
            .Select(o => new PowerBiObject
            {
                Schema = o.Schema, Name = o.Name, Kind = o.Kind, Description = o.Description,
                Columns = columns[(o.Schema, o.Name.ToLowerInvariant())].ToList()
            })
            .ToList();
        var present = objects.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PowerBiAccess
        {
            ServerName = server,
            DatabaseName = database,
            Objects = objects,
            Relationships = Relationships.Where(r => present.Contains(r.FromObject) && present.Contains(r.ToObject)).ToList()
        };
    }
}
