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
///
/// D-233: when account classification is on, each domain also gets one
/// batched read of the matched users' attributes (UPN, employee ID/number,
/// the allow-listed rule attributes: ceil(users / 50) LDAP searches) and the
/// owner lookups (names left after stripping a Secondary/Admin name rule's
/// prefix/suffix, and the shared employee IDs, each capped at
/// MaxOwnerLookupValuesPerDomain values, 50 per search); after every domain,
/// AccountClassificationService reclassifies from what was stored.
/// </summary>
public sealed class AdAccountDiscoveryService(
    LdapConfigRepository ldapConfigRepository,
    IAdDirectory directory,
    AccessGroupRepository accessGroupRepository,
    DiscoveredAccountRepository discoveredAccountRepository,
    AccountClassificationRepository classificationRepository,
    AccountClassificationService classificationService,
    ILogger<AdAccountDiscoveryService> logger)
{
    /// <summary>D-233: at most this many names, and this many employee IDs, are looked up for owners per domain per run.</summary>
    public const int MaxOwnerLookupValuesPerDomain = 1000;

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

        var settings = await classificationRepository.GetSettingsAsync();
        IReadOnlyList<AccountClassificationRule> ownerNameRules = settings.IsEnabled
            ? AccountClassifier.EvaluationOrder(await classificationRepository.GetRulesAsync())
                .Where(r => r.RuleType == "Name" && r.AccountClass != AccountClasses.Primary)
                .ToList()
            : [];

        foreach (var config in configs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessDomainAsync(config, accessGroups, settings, ownerNameRules, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AD Account Discovery failed for domain '{DomainName}' -- continuing with any other configured domains.", config.DomainName);
            }
        }

        try
        {
            await classificationService.ReclassifyAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Account classification after AD Account Discovery failed -- the accounts were stored; Reclassify now can retry it.");
        }
    }

    private async Task ProcessDomainAsync(
        LdapConfig config,
        IReadOnlyList<(int AccessGroupKey, string GroupName, string GroupIdentifier)> accessGroups,
        AccountClassificationSettings settings,
        IReadOnlyList<AccountClassificationRule> ownerNameRules,
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

        // D-233: one batched read of the classification attributes, then the owner lookups.
        var attributesRead = settings.IsEnabled && matchedUsers.Count > 0;
        if (attributesRead)
        {
            var detailed = session.FindUsers("sAMAccountName", matchedUsers.Keys.ToList())
                .GroupBy(u => u.SamAccountName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var sam in matchedUsers.Keys.ToList())
            {
                var (user, groups) = matchedUsers[sam];
                var detail = detailed.GetValueOrDefault(sam);
                matchedUsers[sam] = (user with
                {
                    UserPrincipalName = detail?.UserPrincipalName,
                    EmployeeId = detail?.EmployeeId,
                    EmployeeNumber = detail?.EmployeeNumber,
                    Attributes = detail?.Attributes ?? new Dictionary<string, string>()
                }, groups);
            }

            var ownerLookups = ReadOwnerLookups(config.DomainName, session, settings, ownerNameRules, matchedUsers.Values.Select(v => v.User).ToList());
            await classificationRepository.ReplaceOwnerLookupsAsync(config.DomainName, ownerLookups);
        }

        foreach (var (user, matchedAccessGroupKeys) in matchedUsers.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScoreAndStoreAsync(config.DomainName, user, matchedAccessGroupKeys.ToList(), attributesRead);
        }
    }

    /// <summary>
    /// D-233: the accounts outside the discovered set that could be an owner -- the names a Secondary/Admin
    /// name rule leaves after stripping, and (owner by employee ID on) every account sharing a discovered
    /// account's employee ID. Each list of values is capped at MaxOwnerLookupValuesPerDomain.
    /// </summary>
    private List<DirectoryUser> ReadOwnerLookups(
        string domainName,
        IAdDirectorySession session,
        AccountClassificationSettings settings,
        IReadOnlyList<AccountClassificationRule> ownerNameRules,
        IReadOnlyList<DirectoryUser> discovered)
    {
        var discoveredNames = new HashSet<string>(discovered.Select(u => u.SamAccountName), StringComparer.OrdinalIgnoreCase);
        var found = new List<DirectoryUser>();

        var names = discovered
            .SelectMany(u => ownerNameRules.Select(r => AccountClassifier.OwnerFromName(r, u.SamAccountName)))
            .Where(n => n is not null && !discoveredNames.Contains(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count > 0)
        {
            found.AddRange(session.FindUsers("sAMAccountName", Cap(names, "owner names", domainName)));
        }

        if (settings.OwnerByEmployeeId)
        {
            var byNumber = settings.EmployeeIdAttribute == "employeeNumber";
            var ids = discovered
                .Select(u => byNumber ? u.EmployeeNumber : u.EmployeeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count > 0)
            {
                found.AddRange(session.FindUsers(byNumber ? "employeeNumber" : "employeeID", Cap(ids, "employee IDs", domainName)));
            }
        }

        return found.Where(u => !discoveredNames.Contains(u.SamAccountName)).ToList();
    }

    private List<string> Cap(List<string> values, string what, string domainName)
    {
        if (values.Count <= MaxOwnerLookupValuesPerDomain) return values;
        logger.LogWarning("Account classification: {Count} {What} to look up in '{DomainName}'; only the first {Max} were read this run.",
            values.Count, what, domainName, MaxOwnerLookupValuesPerDomain);
        return values.Take(MaxOwnerLookupValuesPerDomain).ToList();
    }

    private async Task ScoreAndStoreAsync(string domainName, DirectoryUser user, IReadOnlyList<int> matchedAccessGroupKeys, bool attributesRead)
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
            AttributesRead = attributesRead,
            UserPrincipalName = user.UserPrincipalName,
            EmployeeId = user.EmployeeId,
            EmployeeNumber = user.EmployeeNumber,
            DirectoryAttributes = AccountClassificationRepository.SerializeAttributes(user.Attributes),
            MatchedAccessGroupKeys = matchedAccessGroupKeys,
            PossibleExistingAccountKey = possibleExistingAccountKey
        };

        var score = await discoveredAccountRepository.CalculateRiskScoreAsync(matchedAccessGroupKeys);
        var discoveredAccountKey = await discoveredAccountRepository.UpsertCandidateAsync(candidate, score);
        await discoveredAccountRepository.ReplaceAccessGroupMappingsAsync(discoveredAccountKey, matchedAccessGroupKeys);
    }
}
