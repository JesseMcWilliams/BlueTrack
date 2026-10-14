using System.DirectoryServices.AccountManagement;
using BlueTrack.Api.Ldap;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>An AD user found as a member of an inventoried group, with what AD Account Discovery stores about it.</summary>
public sealed record DirectoryUser(string SamAccountName, string? DistinguishedName, string? ObjectSid, string? DisplayName, bool IsEnabled);

/// <summary>
/// D-214: the directory AdAccountDiscoveryService reads, one domain at a
/// time -- behind an interface so its multi-domain handling can be tested
/// with a fake (only one real domain exists to test against).
/// </summary>
public interface IAdDirectory
{
    /// <summary>A session against one configured domain; null when the config isn't usable (disabled, or no way to bind).</summary>
    Task<IAdDirectorySession?> OpenAsync(LdapConfig config);
}

public interface IAdDirectorySession : IDisposable
{
    /// <summary>The group's user members, nested groups included; null when this domain has no such group.</summary>
    IReadOnlyList<DirectoryUser>? GetUserMembers(string groupIdentifier);
}

/// <summary>The real directory: System.DirectoryServices.AccountManagement through LdapContextFactory.</summary>
public sealed class LdapAdDirectory(LdapContextFactory contextFactory) : IAdDirectory
{
    public async Task<IAdDirectorySession?> OpenAsync(LdapConfig config)
    {
        var context = await contextFactory.TryCreateContextAsync(config);
        return context is null ? null : new Session(context);
    }

    private sealed class Session(PrincipalContext context) : IAdDirectorySession
    {
        public IReadOnlyList<DirectoryUser>? GetUserMembers(string groupIdentifier)
        {
            // Expected to miss for most groups in a multi-domain setup (a group
            // only exists in the domain it was created in), and for any
            // GroupIdentifier that isn't a real SID/name in this domain
            // (dim_access_group.GroupIdentifier is free text).
            using var group = LdapGroupMemberResolver.FindGroup(context, groupIdentifier);
            if (group is null) return null;

            var users = new List<DirectoryUser>();
            foreach (var member in group.GetMembers(recursive: true))
            {
                using (member)
                {
                    if (member is UserPrincipal user && !string.IsNullOrWhiteSpace(user.SamAccountName))
                    {
                        users.Add(new DirectoryUser(user.SamAccountName, user.DistinguishedName, user.Sid?.Value, user.DisplayName, user.Enabled ?? true));
                    }
                }
            }
            return users;
        }

        public void Dispose() => context.Dispose();
    }
}
