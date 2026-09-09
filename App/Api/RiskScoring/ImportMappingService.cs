using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.RiskScoring;

/// <summary>
/// Design_Risk_Scoring.md's "Import Field Mapping" section (D-101-105,
/// D-119 Phase B): resolves an internal field name to whatever source CSV
/// column an admin-configured web.import_mapping_profile says corresponds
/// to it. No profile selected/configured for a FeedType means the CSV's
/// own header names are assumed to already match the internal field names
/// 1:1 -- exactly the case for this app's own generated import-template
/// download, needing zero mapping at all.
/// </summary>
public sealed class ImportMappingService(IDbConnectionFactory connectionFactory)
{
    public sealed record FieldMapping(string SourceColumnName, bool IsRequired, string? DefaultValue);

    public async Task<IReadOnlyDictionary<string, FieldMapping>> GetActiveMappingAsync(string feedType, int? mappingProfileKey)
    {
        using var connection = connectionFactory.Create();

        var profileKey = mappingProfileKey ?? await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT TOP 1 ImportMappingProfileKey FROM web.import_mapping_profile WHERE FeedType = @FeedType AND IsActive = 1 ORDER BY ImportMappingProfileKey",
            new { FeedType = feedType });

        if (profileKey is null)
        {
            return new Dictionary<string, FieldMapping>();
        }

        var rows = await connection.QueryAsync<(string TargetFieldName, string SourceColumnName, bool IsRequired, string? DefaultValue)>(
            "SELECT TargetFieldName, SourceColumnName, IsRequired, DefaultValue FROM web.import_mapping_field WHERE ImportMappingProfileKey = @ProfileKey",
            new { ProfileKey = profileKey });

        return rows.ToDictionary(r => r.TargetFieldName, r => new FieldMapping(r.SourceColumnName, r.IsRequired, r.DefaultValue));
    }

    /// <summary>Falls back to reading targetFieldName directly out of the CSV row when no mapping is configured for it (the generated-template case).</summary>
    public static string? ResolveField(IReadOnlyDictionary<string, string> csvRow, IReadOnlyDictionary<string, FieldMapping> mapping, string targetFieldName)
    {
        if (mapping.TryGetValue(targetFieldName, out var m))
        {
            if (csvRow.TryGetValue(m.SourceColumnName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return m.DefaultValue;
        }

        return csvRow.TryGetValue(targetFieldName, out var direct) && !string.IsNullOrWhiteSpace(direct) ? direct : null;
    }
}
