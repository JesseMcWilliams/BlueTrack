using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Backs the Import Mapping Profiles admin page (Design_Risk_Scoring.md, D-101-105, D-119 Phase B).</summary>
public sealed class ImportMappingProfileRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<ImportMappingProfileSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var profiles = (await connection.QueryAsync<(int ImportMappingProfileKey, string FeedType, string ProfileName, bool IsActive, string? Description)>(
            "SELECT ImportMappingProfileKey, FeedType, ProfileName, IsActive, Description FROM web.import_mapping_profile ORDER BY FeedType, ProfileName")).ToList();
        if (profiles.Count == 0)
        {
            return [];
        }

        var fields = await connection.QueryAsync<(int ImportMappingProfileKey, string SourceColumnName, string TargetFieldName, bool IsRequired, string? DefaultValue)>(
            "SELECT ImportMappingProfileKey, SourceColumnName, TargetFieldName, IsRequired, DefaultValue FROM web.import_mapping_field");
        var fieldsByProfile = fields.GroupBy(f => f.ImportMappingProfileKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ImportMappingFieldSummary>)g.Select(f => new ImportMappingFieldSummary
            {
                SourceColumnName = f.SourceColumnName,
                TargetFieldName = f.TargetFieldName,
                IsRequired = f.IsRequired,
                DefaultValue = f.DefaultValue
            }).ToList());

        return profiles.Select(p => new ImportMappingProfileSummary
        {
            ImportMappingProfileKey = p.ImportMappingProfileKey,
            FeedType = p.FeedType,
            ProfileName = p.ProfileName,
            IsActive = p.IsActive,
            Description = p.Description,
            Fields = fieldsByProfile.GetValueOrDefault(p.ImportMappingProfileKey, [])
        }).ToList();
    }

    public async Task<int> CreateAsync(SaveImportMappingProfileRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var profileKey = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.import_mapping_profile (FeedType, ProfileName, IsActive, Description, CreatedBy, ModifiedBy, ModifiedDate)
            OUTPUT inserted.ImportMappingProfileKey
            VALUES (@FeedType, @ProfileName, @IsActive, @Description, @ModifiedBy, @ModifiedBy, SYSUTCDATETIME())
            """, new { request.FeedType, request.ProfileName, request.IsActive, request.Description, ModifiedBy = modifiedByUserKey }, transaction);

        await InsertFieldsAsync(connection, transaction, profileKey, request.Fields);

        transaction.Commit();
        return profileKey;
    }

    public async Task UpdateAsync(int profileKey, SaveImportMappingProfileRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("""
            UPDATE web.import_mapping_profile
            SET FeedType = @FeedType, ProfileName = @ProfileName, IsActive = @IsActive, Description = @Description,
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            WHERE ImportMappingProfileKey = @ProfileKey
            """, new { ProfileKey = profileKey, request.FeedType, request.ProfileName, request.IsActive, request.Description, ModifiedBy = modifiedByUserKey }, transaction);

        await connection.ExecuteAsync("DELETE FROM web.import_mapping_field WHERE ImportMappingProfileKey = @ProfileKey", new { ProfileKey = profileKey }, transaction);
        await InsertFieldsAsync(connection, transaction, profileKey, request.Fields);

        transaction.Commit();
    }

    public async Task DeleteAsync(int profileKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM web.import_mapping_field WHERE ImportMappingProfileKey = @ProfileKey", new { ProfileKey = profileKey }, transaction);
        await connection.ExecuteAsync("DELETE FROM web.import_mapping_profile WHERE ImportMappingProfileKey = @ProfileKey", new { ProfileKey = profileKey }, transaction);
        transaction.Commit();
    }

    private static async Task InsertFieldsAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, int profileKey, IReadOnlyList<SaveImportMappingFieldRequest> fields)
    {
        const string sql = "INSERT INTO web.import_mapping_field (ImportMappingProfileKey, SourceColumnName, TargetFieldName, IsRequired, DefaultValue) VALUES (@ProfileKey, @SourceColumnName, @TargetFieldName, @IsRequired, @DefaultValue)";
        foreach (var field in fields)
        {
            await connection.ExecuteAsync(sql, new { ProfileKey = profileKey, field.SourceColumnName, field.TargetFieldName, field.IsRequired, field.DefaultValue }, transaction);
        }
    }
}
