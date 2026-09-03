namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-48's error-code categorization (Design_Secrets_Storage.md), confirmed
/// against CyberArk's official Application Provider Messages reference.
/// The .NET SDK surfaces the code via PSDKException.Reason (confirmed by
/// reflecting NetStandardPasswordSDK.dll directly -- D-48 flagged this as
/// unconfirmed until the SDK was actually integrated).
/// </summary>
public enum CyberArkErrorCategory
{
    NotFound,
    AccessDenied,
    AmbiguousQuery,
    PasswordChangeInProgress,
    VaultConnectivity,
    Other
}

public static class CyberArkErrorClassifier
{
    private static readonly IReadOnlyDictionary<string, CyberArkErrorCategory> CodeCategories = new Dictionary<string, CyberArkErrorCategory>
    {
        ["APPAP004E"] = CyberArkErrorCategory.NotFound,
        ["APPAP249E"] = CyberArkErrorCategory.NotFound,
        ["APPAP324E"] = CyberArkErrorCategory.NotFound,

        ["APPAP008E"] = CyberArkErrorCategory.AccessDenied,
        ["APPAP087E"] = CyberArkErrorCategory.AccessDenied,
        ["APPAP132E"] = CyberArkErrorCategory.AccessDenied,
        ["APPAP133E"] = CyberArkErrorCategory.AccessDenied,

        ["APPAP227E"] = CyberArkErrorCategory.AmbiguousQuery,
        ["APPAP228E"] = CyberArkErrorCategory.AmbiguousQuery,
        ["APPAP229E"] = CyberArkErrorCategory.AmbiguousQuery,
        ["APPAP230E"] = CyberArkErrorCategory.AmbiguousQuery,
        ["APPAP251E"] = CyberArkErrorCategory.AmbiguousQuery,

        ["APPAP282E"] = CyberArkErrorCategory.PasswordChangeInProgress,
        ["APPAP286E"] = CyberArkErrorCategory.PasswordChangeInProgress,

        ["APPAP007E"] = CyberArkErrorCategory.VaultConnectivity,
        ["APPBC007E"] = CyberArkErrorCategory.VaultConnectivity,
        ["APPAP096W"] = CyberArkErrorCategory.VaultConnectivity,
        ["APPAP289E"] = CyberArkErrorCategory.VaultConnectivity,
        ["APPAP291E"] = CyberArkErrorCategory.VaultConnectivity,
        ["APPAP292E"] = CyberArkErrorCategory.VaultConnectivity,
        ["APPAP297E"] = CyberArkErrorCategory.VaultConnectivity
    };

    /// <summary>Only VaultConnectivity and PasswordChangeInProgress are transient (D-48) -- worth falling back to a cached secret for.</summary>
    public static bool IsTransient(CyberArkErrorCategory category) =>
        category is CyberArkErrorCategory.VaultConnectivity or CyberArkErrorCategory.PasswordChangeInProgress;

    public static CyberArkErrorCategory Classify(string? reasonOrMessage)
    {
        if (string.IsNullOrEmpty(reasonOrMessage))
        {
            return CyberArkErrorCategory.Other;
        }

        foreach (var (code, category) in CodeCategories)
        {
            if (reasonOrMessage.Contains(code, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return CyberArkErrorCategory.Other;
    }
}
