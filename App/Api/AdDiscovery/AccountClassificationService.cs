using BlueTrack.Api.Data;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// D-233: recomputes every discovered account's class and owner from what is stored (the attributes discovery
/// read, the owner lookups, the rules, the settings and the overrides) -- no LDAP. Runs at the end of each
/// discovery run, after every settings/rule change and override, and from the page's Reclassify now button.
/// </summary>
public sealed class AccountClassificationService(AccountClassificationRepository repository)
{
    /// <summary>The number of discovered accounts written.</summary>
    public async Task<int> ReclassifyAsync()
    {
        var settings = await repository.GetSettingsAsync();
        var rules = await repository.GetRulesAsync();
        var (discovered, ownerLookups) = await repository.GetSubjectsAsync();
        var results = AccountClassifier.Classify(settings, rules, discovered, ownerLookups);
        return await repository.SaveResultsAsync(results);
    }
}
