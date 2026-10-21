using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>D-281: a group that is a Safe member, to read from AD. Source SELFHOSTED: Ref is the Vault group id; PRIVCLOUD: the group's name.</summary>
public sealed record SafeMemberGroup(string Source, string Ref, string Name, string? Dn);

/// <summary>D-281: how one group's AD read went: Found (in DomainName), NotFound in every domain tried, or Failed.</summary>
public sealed record AdGroupRead(SafeMemberGroup Group, string Status, string? DomainName, int? MemberCount, string? Message);

/// <summary>D-281: one AD user of a group, nested groups flattened.</summary>
public sealed record AdGroupMember(SafeMemberGroup Group, string DomainName, DirectoryUser User);

/// <summary>D-281: what the AD read found; null on the snapshot when no LDAP domain is enabled.</summary>
public sealed class AdGroupExpansion
{
    public List<AdGroupRead> Reads { get; } = [];
    public List<AdGroupMember> Members { get; } = [];
}

/// <summary>
/// D-281: reads from AD the users of every group that is a Safe member, so reports can show the people who reach a
/// Safe through a group. Runs inside the nightly Self-Hosted Direct pull (the user's choice, 2026-10-21), after PVWA
/// is read, through the LDAP domains configured on Admin > Credentials &amp; LDAP and the same directory code as AD
/// Account Discovery. A Self-Hosted directory group is looked up by its DN in the domain its DC= parts name (every
/// enabled domain when none matches); a Privilege Cloud group, whose export has no DN, by its name in every domain
/// (best effort). Nested groups are flattened. A domain that can't be bound, or a lookup that throws, marks the group
/// Failed, and the Load keeps the members it had; the pull itself never fails on AD.
/// </summary>
public sealed class SafeGroupAdExpansionService(
    LdapConfigRepository ldapConfigRepository,
    IAdDirectory directory,
    SelfHostedDirectRepository repository,
    ILogger<SafeGroupAdExpansionService> logger)
{
    public async Task<AdGroupExpansion?> ExpandAsync(PvwaSnapshot snapshot, CancellationToken ct)
    {
        var configs = await ldapConfigRepository.GetEnabledAsync();
        if (configs.Count == 0) return null;

        var groups = SelfHostedGroups(snapshot).Concat((await repository.GetPrivilegeCloudSafeMemberGroupsAsync()).Select(n => new SafeMemberGroup("PRIVCLOUD", n, n, null))).ToList();
        var expansion = new AdGroupExpansion();
        var sessions = new Dictionary<int, (IAdDirectorySession? Session, string? Problem)>();
        try
        {
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                var domains = DomainsFor(group, configs);
                string? problem = null;
                var found = false;
                foreach (var config in domains)
                {
                    if (!sessions.TryGetValue(config.LdapConfigKey, out var open))
                    {
                        open = await OpenAsync(config);
                        sessions[config.LdapConfigKey] = open;
                    }
                    if (open.Session is null) { problem ??= open.Problem; continue; }
                    try
                    {
                        var users = open.Session.GetUserMembers(group.Dn ?? group.Name);
                        if (users is null) continue;
                        expansion.Reads.Add(new AdGroupRead(group, "Found", config.DomainName, users.Count, null));
                        expansion.Members.AddRange(users.Select(u => new AdGroupMember(group, config.DomainName, u)));
                        found = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        logger.LogWarning(ex, "AD members of group {Group} not read in {Domain}.", group.Name, config.DomainName);
                        problem ??= $"{config.DomainName}: {ex.Message}";
                    }
                }
                if (!found)
                {
                    expansion.Reads.Add(problem is null
                        ? new AdGroupRead(group, "NotFound", null, 0, $"Not found in {string.Join(", ", domains.Select(d => d.DomainName))}.")
                        : new AdGroupRead(group, "Failed", null, null, problem));
                }
            }
        }
        finally
        {
            foreach (var (session, _) in sessions.Values) session?.Dispose();
        }
        return expansion;
    }

    /// <summary>
    /// D-281: on a site with Self-Hosted off there is no pull, so the nightly service runs this at the pull time
    /// instead: Privilege Cloud's Safe-member groups by name, applied at once. Returns how it went, for the log; null
    /// when no LDAP domain is enabled (nothing changed).
    /// </summary>
    public async Task<AdGroupExpansion?> RunStandaloneAsync(CancellationToken ct)
    {
        var expansion = await ExpandAsync(new PvwaSnapshot(), ct);
        if (expansion is null) return null;
        var tables = new StagingTables();
        SelfHostedStagingMapper.MapAdGroups(expansion, tables, Guid.NewGuid(), "AD (standalone)");
        await repository.ApplyAdGroupsAsync(tables, ct);
        return expansion;
    }

    /// <summary>Self-Hosted groups with a DN (directory groups) that are a member of some Safe.</summary>
    private static IEnumerable<SafeMemberGroup> SelfHostedGroups(PvwaSnapshot snapshot)
    {
        var memberGroupIds = snapshot.Members.Where(m => m.IsGroup).Select(m => m.MemberId).ToHashSet();
        return snapshot.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g.Dn) && memberGroupIds.Contains(g.Id))
            .Select(g => new SafeMemberGroup("SELFHOSTED", g.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), g.Name, g.Dn!.Trim()));
    }

    /// <summary>The domain a DN's DC= parts name (Company.com for ...,DC=Company,DC=com), else every enabled domain.</summary>
    public static IReadOnlyList<LdapConfig> DomainsFor(SafeMemberGroup group, IReadOnlyList<LdapConfig> configs)
    {
        if (group.Dn is null) return configs;
        var dcs = group.Dn.Split(',').Select(p => p.Trim())
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..]);
        var domain = string.Join('.', dcs);
        var matching = configs.Where(c => string.Equals(c.DomainName, domain, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count > 0 ? matching : configs;
    }

    private async Task<(IAdDirectorySession? Session, string? Problem)> OpenAsync(LdapConfig config)
    {
        try
        {
            var session = await directory.OpenAsync(config);
            return (session, session is null ? $"{config.DomainName}: couldn't bind (check the domain on Admin > Credentials & LDAP)." : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Couldn't open domain {Domain} for the Safe group AD read.", config.DomainName);
            return (null, $"{config.DomainName}: {ex.Message}");
        }
    }
}
