using BlueTrack.Api.Auth;

namespace BlueTrack.Api.ApiAccess;

/// <summary>
/// D-243: what an API key can never have, whatever its role. These are the
/// permissions that would let a key widen its own access or reach the
/// settings that govern sign-in and secrets; an Admin-run script that needs
/// them should sign in as a person instead.
/// </summary>
public static class ApiAccessRules
{
    public const string ProviderType = "ApiAccess";
    public const string AdminRoleName = "Admin";
    public const int MaxLifetimeDays = 365;

    public static readonly IReadOnlySet<string> NeverGranted = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.ManageApiKeys,
        Permissions.ManageIdentityProviders,
        Permissions.ManageGroupRoleMapping,
        Permissions.ManageRolesAndPermissions,
        Permissions.ManageSecretsStore,
        Permissions.ManageApplicationConfiguration,
        Permissions.ManageCredentials,
        Permissions.TriggerBackup,
        Permissions.ReloadRights,
    };

    /// <summary>The identifier an API key's app_user has (web.app_user.ExternalIdentifier).</summary>
    public static string ExternalIdentifierFor(int apiKeyKey) => $"apikey-{apiKeyKey}";
}
