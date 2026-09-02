using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

public sealed class FieldMetadataRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<FieldMetadataItem>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT fm.FieldMetadataKey, fm.FieldName, fm.DisplayLabel, fm.FieldType, fm.ReferenceTable,
                   fm.IsRequired, fm.RequiredPermission, p.PermissionName AS RequiredPermissionName, fm.DisplayOrder
            FROM web.account_progress_field_metadata fm
            LEFT JOIN web.app_permission p ON p.PermissionKey = fm.RequiredPermission
            ORDER BY fm.DisplayOrder
            """;
        var rows = await connection.QueryAsync<FieldMetadataItem>(sql);
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveFieldMetadataRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.account_progress_field_metadata
                (FieldName, DisplayLabel, FieldType, ReferenceTable, IsRequired, RequiredPermission, DisplayOrder)
            OUTPUT inserted.FieldMetadataKey
            VALUES (@FieldName, @DisplayLabel, @FieldType, @ReferenceTable, @IsRequired, @RequiredPermission, @DisplayOrder)
            """;
        return await connection.QuerySingleAsync<int>(sql, request);
    }

    public async Task UpdateAsync(int fieldMetadataKey, SaveFieldMetadataRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.account_progress_field_metadata
            SET FieldName = @FieldName, DisplayLabel = @DisplayLabel, FieldType = @FieldType,
                ReferenceTable = @ReferenceTable, IsRequired = @IsRequired,
                RequiredPermission = @RequiredPermission, DisplayOrder = @DisplayOrder
            WHERE FieldMetadataKey = @FieldMetadataKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            FieldMetadataKey = fieldMetadataKey,
            request.FieldName,
            request.DisplayLabel,
            request.FieldType,
            request.ReferenceTable,
            request.IsRequired,
            request.RequiredPermission,
            request.DisplayOrder
        });
    }

    public async Task DeleteAsync(int fieldMetadataKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "DELETE FROM web.account_progress_field_metadata WHERE FieldMetadataKey = @FieldMetadataKey";
        await connection.ExecuteAsync(sql, new { FieldMetadataKey = fieldMetadataKey });
    }
}
