namespace BlueTrack.Api.Auth;

/// <summary>
/// Mirrors the confirmed permission catalog in web.app_permission
/// (D-05, D-61). The catalog itself lives in the database, not here --
/// these are just typed handles onto it so an [Authorize(Policy = ...)]
/// attribute can't typo a permission name.
/// </summary>
public static class Permissions
{
    public const string ViewDashboard = "ViewDashboard";
    public const string EditAccountProgress = "EditAccountProgress";
    public const string ApproveExceptions = "ApproveExceptions";
    public const string ManageIdentityProviders = "ManageIdentityProviders";
    public const string ManageGroupRoleMapping = "ManageGroupRoleMapping";
    public const string CuratePlatformMapping = "CuratePlatformMapping";
    public const string ConfirmReconciliation = "ConfirmReconciliation";
    public const string ReloadRights = "ReloadRights";
    public const string ManageRolesAndPermissions = "ManageRolesAndPermissions";
    public const string CurateApplicationMapping = "CurateApplicationMapping";
    public const string ManageSecretsStore = "ManageSecretsStore";
    public const string ManageFieldMetadata = "ManageFieldMetadata";
    public const string ViewAuditLog = "ViewAuditLog";
    public const string ManageApplicationConfiguration = "ManageApplicationConfiguration";

    public static readonly IReadOnlyList<string> All =
    [
        ViewDashboard, EditAccountProgress, ApproveExceptions, ManageIdentityProviders,
        ManageGroupRoleMapping, CuratePlatformMapping, ConfirmReconciliation, ReloadRights,
        ManageRolesAndPermissions, CurateApplicationMapping, ManageSecretsStore,
        ManageFieldMetadata, ViewAuditLog, ManageApplicationConfiguration
    ];
}
