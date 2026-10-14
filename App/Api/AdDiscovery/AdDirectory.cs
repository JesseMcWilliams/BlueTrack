using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using System.Text;
using BlueTrack.Api.Ldap;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// An AD user found as a member of an inventoried group, with what AD Account Discovery stores about it.
/// D-233: the init-only properties are filled only by IAdDirectorySession.FindUsers (account classification
/// reads them); group membership alone leaves them null.
/// </summary>
public sealed record DirectoryUser(string SamAccountName, string? DistinguishedName, string? ObjectSid, string? DisplayName, bool IsEnabled)
{
    public string? UserPrincipalName { get; init; }
    public string? EmployeeId { get; init; }
    public string? EmployeeNumber { get; init; }

    /// <summary>The allow-listed attributes (AccountClassificationAttributes.Allowed) that have a value, keyed by their LDAP name.</summary>
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

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

    /// <summary>
    /// D-233: the users whose <paramref name="attributeName"/> (sAMAccountName, employeeID or employeeNumber)
    /// equals one of <paramref name="values"/>, with UserPrincipalName, EmployeeId, EmployeeNumber and the
    /// allow-listed attributes filled. Batched by the implementation; the caller caps how many values it asks for.
    /// </summary>
    IReadOnlyList<DirectoryUser> FindUsers(string attributeName, IReadOnlyCollection<string> values);
}

/// <summary>The real directory: System.DirectoryServices.AccountManagement through LdapContextFactory.</summary>
public sealed class LdapAdDirectory(LdapContextFactory contextFactory) : IAdDirectory
{
    /// <summary>Values per LDAP query in FindUsers: one (|(attr=v1)(attr=v2)...) filter each.</summary>
    public const int FindBatchSize = 50;

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

        public IReadOnlyList<DirectoryUser> FindUsers(string attributeName, IReadOnlyCollection<string> values)
        {
            if (!AccountClassificationAttributes.LookupAttributes.Contains(attributeName))
            {
                throw new ArgumentException($"'{attributeName}' can't be searched on.", nameof(attributeName));
            }

            var results = new List<DirectoryUser>();
            var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0) return results;

            // The PrincipalSearcher's underlying DirectorySearcher is bound the
            // same way as the context (server, search base, credentials, SSL),
            // so these searches need no second connection setup.
            using var queryFilter = new UserPrincipal(context);
            using var principalSearcher = new PrincipalSearcher(queryFilter);
            if (principalSearcher.GetUnderlyingSearcher() is not DirectorySearcher searcher) return results;

            searcher.PropertiesToLoad.Clear();
            foreach (var property in AccountClassificationAttributes.PropertiesToLoad) searcher.PropertiesToLoad.Add(property);
            searcher.PageSize = 500;
            searcher.SizeLimit = 0;

            foreach (var batch in distinct.Chunk(FindBatchSize))
            {
                var filter = new StringBuilder("(&(objectCategory=person)(objectClass=user)(|");
                foreach (var value in batch) filter.Append('(').Append(attributeName).Append('=').Append(AccountClassificationAttributes.EscapeFilterValue(value)).Append(')');
                filter.Append("))");
                searcher.Filter = filter.ToString();

                using var found = searcher.FindAll();
                foreach (SearchResult result in found)
                {
                    var user = ToDirectoryUser(result);
                    if (user is not null) results.Add(user);
                }
            }
            return results;
        }

        private static DirectoryUser? ToDirectoryUser(SearchResult result)
        {
            string? First(string name) =>
                result.Properties.Contains(name) && result.Properties[name].Count > 0 ? Convert.ToString(result.Properties[name][0]) : null;

            var sam = First("sAMAccountName");
            if (string.IsNullOrWhiteSpace(sam)) return null;

            string? sid = null;
            if (result.Properties.Contains("objectSid") && result.Properties["objectSid"][0] is byte[] sidBytes)
            {
                sid = new SecurityIdentifier(sidBytes, 0).Value;
            }

            // userAccountControl bit 0x2 is ACCOUNTDISABLE.
            var enabled = !int.TryParse(First("userAccountControl"), out var uac) || (uac & 0x2) == 0;

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in AccountClassificationAttributes.Allowed)
            {
                var value = First(name);
                if (!string.IsNullOrEmpty(value)) attributes[name] = value;
            }

            return new DirectoryUser(sam, First("distinguishedName"), sid, First("displayName"), enabled)
            {
                UserPrincipalName = First("userPrincipalName"),
                EmployeeId = First("employeeID"),
                EmployeeNumber = First("employeeNumber"),
                Attributes = attributes
            };
        }

        public void Dispose() => context.Dispose();
    }
}
