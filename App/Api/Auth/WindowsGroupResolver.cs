using System.Security.Principal;

namespace BlueTrack.Api.Auth;

/// <summary>
/// The Group → Role Mapping admin page's "lookup/test tool"
/// (Design_Authorization_Model.md's Admin UI Requirements): an admin types
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
}
