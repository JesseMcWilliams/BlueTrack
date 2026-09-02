using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Secrets Store Configuration admin page. This is the config
/// *record* only (which backend is active, plus its non-secret settings) --
/// no actual backend (Windows DPAPI, CyberArk CP, etc.) is implemented
/// here (Design_Secrets_Storage.md, 14_BlueTrack_SecretsStoreSchema.sql).
/// </summary>
public sealed class SecretsStoreRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<SecretsStoreBackend>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT SecretStoreKey, BackendType, IsActive, BackendSettings FROM web.secrets_store ORDER BY BackendType";
        var rows = await connection.QueryAsync<SecretsStoreBackend>(sql);
        return rows.AsList();
    }

    /// <summary>
    /// Exactly one active backend at a time (Design_Secrets_Storage.md) --
    /// enforced here at the application layer via a transaction, not a
    /// database constraint, consistent with how this project avoids
    /// triggers/CHECK constraints for business rules elsewhere.
    /// </summary>
    public async Task SetActiveAsync(SetActiveSecretsStoreRequest request)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("UPDATE web.secrets_store SET IsActive = 0", transaction: transaction);

        await connection.ExecuteAsync(
            "UPDATE web.secrets_store SET IsActive = 1, BackendSettings = @BackendSettings WHERE BackendType = @BackendType",
            new { request.BackendType, request.BackendSettings }, transaction);

        transaction.Commit();
    }
}
