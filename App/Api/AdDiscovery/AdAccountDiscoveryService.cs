using System.DirectoryServices.AccountManagement;
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
/// </summary>
public sealed class AdAccountDiscoveryService(
    LdapConfigRepository ldapConfigRepository,
    LdapContextFactory contextFactory,
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
        using var context = await contextFactory.TryCreateContextAsync(config);
        if (context is null)
        {
            return;
        }

        // Aggregate across every matched Access Group before scoring -- one
        // AD user can belong to several inventoried groups, and the risk
        // score has to reflect all of them together (Database/32's
        // AccessGroupSet function), not one group at a time.
        var matchedUsers = new Dictionary<string, (UserPrincipal Principal, HashSet<int> AccessGroupKeys)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (accessGroupKey, _, groupIdentifier) in accessGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Expected to miss for most groups in a multi-domain setup
                // (a group only exists in the one domain it was created in),
                // and for any GroupIdentifier that isn't actually a real
                // SID/name in this domain (dim_access_group.GroupIdentifier
                // is free text, not validated -- see Design_Decision_Register.md).
                using var group = LdapGroupMemberResolver.FindGroup(context, groupIdentifier);
                if (group is null)
                {
                    continue;
                }

                foreach (var member in group.GetMembers(recursive: true))
                {
                    if (member is not UserPrincipal user || string.IsNullOrWhiteSpace(user.SamAccountName))
                    {
                        member.Dispose();
                        continue;
                    }

                    if (matchedUsers.TryGetValue(user.SamAccountName, out var existing))
                    {
                        existing.AccessGroupKeys.Add(accessGroupKey);
                        user.Dispose(); // already tracking a principal for this SamAccountName
                    }
                    else
                    {
                        matchedUsers[user.SamAccountName] = (user, [accessGroupKey]);
                    }
                }
            }

            foreach (var (samAccountName, (principal, matchedAccessGroupKeys)) in matchedUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ScoreAndStoreAsync(config.DomainName, samAccountName, principal, matchedAccessGroupKeys.ToList());
            }
        }
        finally
        {
            foreach (var (principal, _) in matchedUsers.Values)
            {
                principal.Dispose();
            }
        }
    }

    private async Task ScoreAndStoreAsync(string domainName, string samAccountName, UserPrincipal principal, IReadOnlyList<int> matchedAccessGroupKeys)
    {
        var possibleExistingAccountKey = await discoveredAccountRepository.FindPossibleExistingAccountKeyAsync(domainName, samAccountName);

        var candidate = new DiscoveredAccountCandidate
        {
            DomainName = domainName,
            SamAccountName = samAccountName,
            DistinguishedName = principal.DistinguishedName,
            ObjectSid = principal.Sid?.Value,
            DisplayName = principal.DisplayName,
            IsEnabled = principal.Enabled ?? true,
            MatchedAccessGroupKeys = matchedAccessGroupKeys,
            PossibleExistingAccountKey = possibleExistingAccountKey
        };

        var score = await discoveredAccountRepository.CalculateRiskScoreAsync(matchedAccessGroupKeys);
        var discoveredAccountKey = await discoveredAccountRepository.UpsertCandidateAsync(candidate, score);
        await discoveredAccountRepository.ReplaceAccessGroupMappingsAsync(discoveredAccountKey, matchedAccessGroupKeys);
    }
}
