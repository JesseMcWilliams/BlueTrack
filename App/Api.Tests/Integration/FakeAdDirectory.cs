using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-214/D-233: an IAdDirectory with no domain behind it. Each domain has groups (group identifier to its user
/// members) and, optionally, more users that are in no inventoried group (owners that aren't discovered).
/// FindUsers searches every user of the domain, like an LDAP search would, and records each call.
/// </summary>
public sealed class FakeAdDirectory(
    Dictionary<string, Dictionary<string, DirectoryUser[]>> groupsByDomain,
    Dictionary<string, DirectoryUser[]>? otherUsersByDomain = null,
    string? failingDomain = null) : IAdDirectory
{
    public List<(string Domain, string Attribute, int ValueCount)> FindCalls { get; } = [];

    public Task<IAdDirectorySession?> OpenAsync(LdapConfig config)
    {
        if (config.DomainName == failingDomain) throw new InvalidOperationException("Domain controller unreachable (test).");
        if (!groupsByDomain.TryGetValue(config.DomainName, out var groups)) return Task.FromResult<IAdDirectorySession?>(null);
        var others = otherUsersByDomain?.GetValueOrDefault(config.DomainName) ?? [];
        return Task.FromResult<IAdDirectorySession?>(new Session(this, config.DomainName, groups, others));
    }

    private sealed class Session(FakeAdDirectory owner, string domain, Dictionary<string, DirectoryUser[]> groups, DirectoryUser[] others) : IAdDirectorySession
    {
        public IReadOnlyList<DirectoryUser>? GetUserMembers(string groupIdentifier) =>
            groups.GetValueOrDefault(groupIdentifier)?.Select(u => new DirectoryUser(u.SamAccountName, u.DistinguishedName, u.ObjectSid, u.DisplayName, u.IsEnabled)).ToList();

        public IReadOnlyList<DirectoryUser> FindUsers(string attributeName, IReadOnlyCollection<string> values)
        {
            owner.FindCalls.Add((domain, attributeName, values.Count));
            var wanted = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            return groups.Values.SelectMany(g => g).Concat(others)
                .GroupBy(u => u.SamAccountName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Where(u => attributeName switch
                {
                    "sAMAccountName" => wanted.Contains(u.SamAccountName),
                    "employeeID" => u.EmployeeId is not null && wanted.Contains(u.EmployeeId),
                    "employeeNumber" => u.EmployeeNumber is not null && wanted.Contains(u.EmployeeNumber),
                    _ => throw new ArgumentException(attributeName)
                })
                .ToList();
        }

        public void Dispose() { }
    }
}
