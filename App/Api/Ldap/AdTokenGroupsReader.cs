using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Ldap;

/// <summary>D-303: a Windows user's current group SIDs, read live from the directory. Swapped for a stub in tests.</summary>
public interface IDirectoryGroupReader
{
    /// <summary>
    /// The user's security group SIDs, nested included, as AD would put them in a new sign-in's token; null when no
    /// enabled LDAP domain (Admin &gt; Credentials &amp; LDAP) is set up or knows the user. Throws when the directory
    /// can't be read, so the caller can fall back to the sign-in's groups and say so.
    /// </summary>
    Task<IReadOnlyList<string>?> ReadGroupSidsAsync(string userSid);
}

/// <summary>
/// D-303: reads the user's constructed <c>tokenGroups</c> attribute, which AD computes from transitive membership.
/// Checked against the lab domain on 2026-11-03: it holds every domain group SID of the user's sign-in token,
/// the primary group (Domain Users) included, plus the domain's Builtin groups the user is in. Each enabled
/// domain is tried in turn, as LdapGroupMemberResolver does: a SID resolves only in its own domain.
/// </summary>
public sealed class AdTokenGroupsReader(LdapConfigRepository ldapConfigRepository, LdapContextFactory contextFactory) : IDirectoryGroupReader
{
    public async Task<IReadOnlyList<string>?> ReadGroupSidsAsync(string userSid)
    {
        foreach (var config in await ldapConfigRepository.GetEnabledAsync())
        {
            using var context = await contextFactory.TryCreateContextAsync(config);
            if (context is null) continue;
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.Sid, userSid);
            if (user?.GetUnderlyingObject() is not DirectoryEntry entry) continue;

            // tokenGroups is constructed: it is only returned by a base-scope read that asks for it by name.
            entry.RefreshCache(["tokenGroups"]);
            return entry.Properties["tokenGroups"].Cast<byte[]>()
                .Select(bytes => new SecurityIdentifier(bytes, 0).Value)
                .ToList();
        }
        return null;
    }
}
