using BlueTrack.Api.Data;
using BlueTrack.Api.Ldap;
using BlueTrack.Api.Models;
using Microsoft.Extensions.Logging;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// AD Account Discovery feature (2026-09-16): finds real AD accounts not yet
/// onboarded into CyberArk (no dbo.fact_account row) by matching their AD
/// group membership against web.dim_access_group's already-inventoried
/// groups, scoring each via Database/32's AccessGroupSet risk-scoring
/// variant, and upserting into web.discovered_account.
///
/// Scoped narrowly by design (confirmed directly): only members of AD groups
/// already in dim_access_group are ever considered -- not every account in a
/// domain -- and "risk from server access" means exactly what dim_access_group
/// already models (an AD group mapped to Target servers with a base risk
/// score), not a fresh per-server local-group enumeration.
///
/// Every enabled web.ldap_config row (Phase A) is processed independently;
/// one domain being unreachable never stops the others -- caught and logged
/// per domain, same isolation principle as
/// NotificationCheckBackgroundService's own per-check try/catch.
///
/// D-214: the directory is read through IAdDirectory (LdapAdDirectory in
/// production), so the multi-domain handling is tested with a fake.
/// </summary>
public sealed class AdAccountDiscoveryService(
    LdapConfigRepository ldapConfigRepository,
    IAdDirectory directory,
    AccessGroupRepository accessGroupRepository,
    DiscoveredAccountRepository discoveredAccountRepository,
    ILogger<AdAccountDiscoveryService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var configs = await ldapConfigRepository.GetEnabledAsync();
        if (configs.Count == 0)
        {
            return;
        }

        var accessGroups = await accessGroupRepository.GetAllIdentifiersAsync();
        if (accessGroups.Count == 0)
        {
            return;
        }

        foreach (var config in configs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessDomainAsync(config, accessGroups, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AD Account Discovery failed for domain '{DomainName}' -- continuing with any other configured domains.", config.DomainName);
            }
        }
    }

    private async Task ProcessDomainAsync(
        LdapConfig config,
        IReadOnlyList<(int AccessGroupKey, string GroupName, string GroupIdentifier)> accessGroups,
        CancellationToken cancellationToken)
    {
        using var session = await directory.OpenAsync(config);
        if (session is null)
        {
            return;
        }

        // Aggregate across every matched Access Group before scoring -- one
        // AD user can belong to several inventoried groups, and the risk
        // score has to reflect all of them together (the AccessGroupSet
        // scoring function), not one group at a time.
        var matchedUsers = new Dictionary<string, (DirectoryUser User, HashSet<int> AccessGroupKeys)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (accessGroupKey, _, groupIdentifier) in accessGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var user in session.GetUserMembers(groupIdentifier) ?? [])
            {
                if (matchedUsers.TryGetValue(user.SamAccountName, out var existing))
                {
                    existing.AccessGroupKeys.Add(accessGroupKey);
                }
                else
                {
                    matchedUsers[user.SamAccountName] = (user, [accessGroupKey]);
                }
            }
        }

        foreach (var (user, matchedAccessGroupKeys) in matchedUsers.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScoreAndStoreAsync(config.DomainName, user, matchedAccessGroupKeys.ToList());
        }
    }

    private async Task ScoreAndStoreAsync(string domainName, DirectoryUser user, IReadOnlyList<int> matchedAccessGroupKeys)
    {
        var possibleExistingAccountKey = await discoveredAccountRepository.FindPossibleExistingAccountKeyAsync(domainName, user.SamAccountName);

        var candidate = new DiscoveredAccountCandidate
        {
            DomainName = domainName,
            SamAccountName = user.SamAccountName,
            DistinguishedName = user.DistinguishedName,
            ObjectSid = user.ObjectSid,
            DisplayName = user.DisplayName,
            IsEnabled = user.IsEnabled,
            MatchedAccessGroupKeys = matchedAccessGroupKeys,
            PossibleExistingAccountKey = possibleExistingAccountKey
        };

        var score = await discoveredAccountRepository.CalculateRiskScoreAsync(matchedAccessGroupKeys);
        var discoveredAccountKey = await discoveredAccountRepository.UpsertCandidateAsync(candidate, score);
        await discoveredAccountRepository.ReplaceAccessGroupMappingsAsync(discoveredAccountKey, matchedAccessGroupKeys);
    }
}
