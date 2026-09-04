using Dapper;
using System.Text.Json.Nodes;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Secrets Store Configuration admin page. This is the config
/// *record* only (which backend is active, plus its non-secret settings) --
/// the actual backends (Windows DPAPI, CyberArk CP/CCP/Conjur, Azure Key
/// Vault, AWS Secrets Manager) live in App/Api/Secrets
/// (Design_Secrets_Storage.md, 14_BlueTrack_SecretsStoreSchema.sql).
/// </summary>
public sealed class SecretsStoreRepository(IDbConnectionFactory connectionFactory, ILocalSecretProtector localSecretProtector)
{
    private const string ProtectedCredentialKey = "ProtectedCredential";
    private const string RedactedPlaceholder = "***";

    /// <summary>Redacted (D-84) -- for the admin API/UI. Never returns a usable ProtectedCredential value.</summary>
    public async Task<IReadOnlyList<SecretsStoreBackend>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT SecretStoreKey, BackendType, IsActive, BackendSettings FROM web.secrets_store ORDER BY BackendType";
        var rows = await connection.QueryAsync<SecretsStoreBackend>(sql);
        return rows.Select(Redact).ToList();
    }

    /// <summary>
    /// Unredacted -- only for provider implementations (D-84) that need the
    /// real ProtectedCredential value to Unprotect() and authenticate with.
    /// Never expose this path's result to the admin API/UI.
    /// </summary>
    public async Task<SecretsStoreBackend?> GetByTypeAsync(string backendType)
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT SecretStoreKey, BackendType, IsActive, BackendSettings FROM web.secrets_store WHERE BackendType = @BackendType";
        return await connection.QuerySingleOrDefaultAsync<SecretsStoreBackend>(sql, new { BackendType = backendType });
    }

    /// <summary>
    /// Exactly one active backend at a time (Design_Secrets_Storage.md) --
    /// enforced here at the application layer via a transaction, not a
    /// database constraint, consistent with how this project avoids
    /// triggers/CHECK constraints for business rules elsewhere.
    ///
    /// D-84: if PlaintextCredential is supplied, it's DPAPI-protected here
    /// and merged into BackendSettings as "ProtectedCredential" -- the
    /// plaintext itself is never persisted or logged. See
    /// SetActiveSecretsStoreRequest's own doc comment.
    /// </summary>
    public async Task SetActiveAsync(SetActiveSecretsStoreRequest request)
    {
        var backendSettings = request.BackendSettings;
        if (!string.IsNullOrWhiteSpace(request.PlaintextCredential))
        {
            var settingsNode = string.IsNullOrWhiteSpace(backendSettings)
                ? new JsonObject()
                : JsonNode.Parse(backendSettings)?.AsObject() ?? new JsonObject();
            settingsNode[ProtectedCredentialKey] = localSecretProtector.Protect(request.PlaintextCredential);
            backendSettings = settingsNode.ToJsonString();
        }

        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("UPDATE web.secrets_store SET IsActive = 0", transaction: transaction);

        await connection.ExecuteAsync(
            "UPDATE web.secrets_store SET IsActive = 1, BackendSettings = @BackendSettings WHERE BackendType = @BackendType",
            new { request.BackendType, BackendSettings = backendSettings }, transaction);

        transaction.Commit();
    }

    private static SecretsStoreBackend Redact(SecretsStoreBackend backend)
    {
        if (string.IsNullOrWhiteSpace(backend.BackendSettings))
        {
            return backend;
        }

        JsonObject? settingsNode;
        try
        {
            settingsNode = JsonNode.Parse(backend.BackendSettings)?.AsObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return backend;
        }

        if (settingsNode is null || !settingsNode.ContainsKey(ProtectedCredentialKey))
        {
            return backend;
        }

        settingsNode[ProtectedCredentialKey] = RedactedPlaceholder;
        return new SecretsStoreBackend
        {
            SecretStoreKey = backend.SecretStoreKey,
            BackendType = backend.BackendType,
            IsActive = backend.IsActive,
            BackendSettings = settingsNode.ToJsonString()
        };
    }
}
