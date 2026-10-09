using System.Security.Principal;

namespace BlueTrack.Api.Auth;

/// <summary>
/// The Group → Role Mapping admin page's "lookup/test tool"
/// (Design_Authorization-Model.md's Admin UI Requirements): an admin types
/// a friendly Windows group name, this translates it to the SID that
/// GroupIdentifierExtractor will actually see on a token and that
/// identity_group_role_map stores (D-69) -- so nobody has to hand-type a
/// SID to set up a mapping.
/// </summary>
public static class WindowsGroupResolver
{
    public static (string Sid, string ResolvedAccountName)? TryResolve(string groupName)
    {
        try
        {
            var account = new NTAccount(groupName);
            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            var resolvedBack = (NTAccount)sid.Translate(typeof(NTAccount));
            return (sid.Value, resolvedBack.Value);
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>
    /// D-179: the reverse, for display -- the Group → Role Mapping list
    /// stores only SIDs (D-69), so it resolves each back to DOMAIN\Group.
    /// Null when the SID can't be resolved (deleted group, unreachable
    /// domain) or isn't a SID at all; the page then shows the SID alone.
    /// </summary>
    public static string? TryGetAccountName(string sid)
    {
        try
        {
            return ((NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
        {
            // ArgumentException: not SID syntax. SystemException: the domain
            // couldn't be reached (e.g. a broken trust). Display only, so any
            // of these just means "show the SID".
            return null;
        }
    }
}
