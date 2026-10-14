using System.Text;
using System.Text.RegularExpressions;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>D-233: the AD attributes account classification may read, and the names it searches on.</summary>
public static class AccountClassificationAttributes
{
    /// <summary>The only attributes an Attribute rule may name (no free LDAP attribute). Each is read as its first value.</summary>
    public static readonly IReadOnlyList<string> Allowed =
    [
        "adminCount", "employeeType",
        .. Enumerable.Range(1, 15).Select(n => $"extensionAttribute{n}"),
        "description", "title", "department"
    ];

    /// <summary>What IAdDirectorySession.FindUsers may search on.</summary>
    public static readonly IReadOnlySet<string> LookupAttributes =
        new HashSet<string>(["sAMAccountName", "employeeID", "employeeNumber"], StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> EmployeeIdAttributes = ["employeeID", "employeeNumber"];

    public static readonly IReadOnlyList<string> NameAttributes = ["sAMAccountName", "userPrincipalName"];

    /// <summary>The LDAP properties one FindUsers search loads.</summary>
    public static readonly IReadOnlyList<string> PropertiesToLoad =
    [
        "sAMAccountName", "distinguishedName", "objectSid", "displayName", "userAccountControl",
        "userPrincipalName", "employeeID", "employeeNumber", .. Allowed
    ];

    /// <summary>The allow-listed spelling of <paramref name="name"/> (case ignored), or null if it isn't allowed.</summary>
    public static string? CanonicalAttribute(string? name) =>
        Allowed.FirstOrDefault(a => string.Equals(a, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>RFC 4515 escaping, so a value can never change an LDAP filter's shape.</summary>
    public static string EscapeFilterValue(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString()
            });
        }
        return escaped.ToString();
    }
}

public static class AccountClasses
{
    public const string Primary = "Primary";
    public const string Secondary = "Secondary";
    public const string Admin = "Admin";
    public const string Unknown = "Unknown";

    /// <summary>What a rule can assign.</summary>
    public static readonly IReadOnlyList<string> RuleClasses = [Primary, Secondary, Admin];

    /// <summary>What a manual override can set (and the report's Class filter).</summary>
    public static readonly IReadOnlyList<string> All = [Primary, Secondary, Admin, Unknown];

    public static string? Canonical(IReadOnlyList<string> allowed, string? value) =>
        allowed.FirstOrDefault(c => string.Equals(c, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>One account the classifier looks at: a discovered account (DiscoveredAccountKey set) or one read from AD only to find owners.</summary>
public sealed record ClassificationSubject(
    int? DiscoveredAccountKey,
    string DomainName,
    string SamAccountName,
    string? UserPrincipalName,
    string? DistinguishedName,
    string? DisplayName,
    bool IsEnabled,
    string? EmployeeId,
    string? EmployeeNumber,
    IReadOnlyDictionary<string, string> Attributes,
    string? ClassOverride = null,
    string? OwnerOverrideSamAccountName = null);

/// <summary>What the classifier works out for one discovered account. ClassSource: Rule | Default | Manual; OwnerSource: Self | Name | EmployeeId | Ambiguous | Manual.</summary>
public sealed record ClassificationResult(
    int DiscoveredAccountKey,
    string? AccountClass,
    string? ClassSource,
    int? ClassRuleKey,
    string? OwnerSamAccountName,
    string? OwnerDisplayName,
    string? OwnerSource);

/// <summary>
/// D-233: classifies discovered accounts as Primary / Secondary / Admin / Unknown and finds each one's owner.
/// Pure: everything it needs comes in as arguments, so a reclassify needs no LDAP and the rules are unit tested.
///
/// Class: a manual override wins; otherwise the first enabled rule that matches, Attribute rules first, then
/// Name, then OU, each in DisplayOrder; otherwise the default class (Unknown, or Primary if set).
///
/// Owner (same domain only): a manual override wins; a Primary account owns itself; otherwise the first enabled
/// Secondary/Admin Name rule on sAMAccountName that matches gives the owner by stripping its prefix/suffix (or the
/// text a pattern's single * matched), if that account is known (discovered, or read from AD for owners);
/// otherwise, with owner by employee ID on, the one enabled account sharing the ID whose class is explicitly
/// Primary (rule or override) -- or, failing that and only for an account a rule or override made Secondary or
/// Admin, the one sharing it that matches no rule. More than one candidate is Ambiguous: no owner.
/// </summary>
public static class AccountClassifier
{
    private static readonly IReadOnlyDictionary<string, int> TypeRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Attribute"] = 0, ["Name"] = 1, ["OU"] = 2 };

    /// <summary>Enabled rules in evaluation order.</summary>
    public static IReadOnlyList<AccountClassificationRule> EvaluationOrder(IEnumerable<AccountClassificationRule> rules) =>
        rules.Where(r => r.IsEnabled)
            .OrderBy(r => TypeRank.GetValueOrDefault(r.RuleType, 9))
            .ThenBy(r => r.DisplayOrder)
            .ThenBy(r => r.AccountClassificationRuleKey)
            .ToList();

    public static IReadOnlyList<ClassificationResult> Classify(
        AccountClassificationSettings settings,
        IEnumerable<AccountClassificationRule> rules,
        IReadOnlyList<ClassificationSubject> discovered,
        IReadOnlyList<ClassificationSubject> ownerLookups)
    {
        var results = new List<ClassificationResult>(discovered.Count);

        if (!settings.IsEnabled)
        {
            foreach (var d in discovered)
            {
                results.Add(new ClassificationResult(d.DiscoveredAccountKey!.Value,
                    d.ClassOverride, d.ClassOverride is null ? null : "Manual", null,
                    d.OwnerOverrideSamAccountName, d.OwnerOverrideSamAccountName is null ? null : FindDisplayName(discovered, ownerLookups, d),
                    d.OwnerOverrideSamAccountName is null ? null : "Manual"));
            }
            return results;
        }

        var ordered = EvaluationOrder(rules);
        var ownerNameRules = ordered
            .Where(r => r.RuleType == "Name" && IsSamRule(r) && r.AccountClass != AccountClasses.Primary)
            .ToList();

        foreach (var domain in discovered.GroupBy(d => d.DomainName, StringComparer.OrdinalIgnoreCase))
        {
            // Every account known in this domain: discovered ones win over owner lookups of the same name.
            var known = new Dictionary<string, ClassificationSubject>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ownerLookups.Where(s => string.Equals(s.DomainName, domain.Key, StringComparison.OrdinalIgnoreCase))) known[s.SamAccountName] = s;
            foreach (var s in domain) known[s.SamAccountName] = s;

            var ruleMatch = known.Values.ToDictionary(s => s, s => FirstMatch(ordered, s));

            string EffectiveClass(ClassificationSubject s) =>
                s.ClassOverride ?? ruleMatch[s]?.AccountClass ?? settings.DefaultAccountClass;
            bool ExplicitlyPrimary(ClassificationSubject s) =>
                (s.ClassOverride ?? ruleMatch[s]?.AccountClass) == AccountClasses.Primary;
            bool MatchesNothing(ClassificationSubject s) => s.ClassOverride is null && ruleMatch[s] is null;

            var byEmployeeId = settings.OwnerByEmployeeId
                ? known.Values
                    .Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(EmployeeIdOf(settings, s)))
                    .GroupBy(s => EmployeeIdOf(settings, s)!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase)
                : [];

            foreach (var d in domain)
            {
                var match = ruleMatch[d];
                var accountClass = EffectiveClass(d);
                var classSource = d.ClassOverride is not null ? "Manual" : match is not null ? "Rule" : "Default";
                var classRuleKey = d.ClassOverride is null ? match?.AccountClassificationRuleKey : null;

                string? ownerSam = null, ownerDisplay = null, ownerSource = null;
                if (d.OwnerOverrideSamAccountName is not null)
                {
                    ownerSam = d.OwnerOverrideSamAccountName;
                    ownerDisplay = known.GetValueOrDefault(ownerSam)?.DisplayName;
                    ownerSource = "Manual";
                }
                else if (accountClass == AccountClasses.Primary)
                {
                    (ownerSam, ownerDisplay, ownerSource) = (d.SamAccountName, d.DisplayName, "Self");
                }
                else
                {
                    foreach (var rule in ownerNameRules)
                    {
                        var stripped = OwnerFromName(rule, d.SamAccountName);
                        if (stripped is null || string.Equals(stripped, d.SamAccountName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (known.TryGetValue(stripped, out var owner))
                        {
                            (ownerSam, ownerDisplay, ownerSource) = (owner.SamAccountName, owner.DisplayName, "Name");
                            break;
                        }
                    }

                    var employeeId = EmployeeIdOf(settings, d)?.Trim();
                    if (ownerSource is null && settings.OwnerByEmployeeId && !string.IsNullOrEmpty(employeeId)
                        && byEmployeeId.TryGetValue(employeeId, out var sharing))
                    {
                        var others = sharing.Where(s => !ReferenceEquals(s, d)).ToList();
                        var pool = others.Where(ExplicitlyPrimary).ToList();
                        var dIsSecondaryOrAdmin = accountClass is AccountClasses.Secondary or AccountClasses.Admin && !MatchesNothing(d);
                        if (pool.Count == 0 && dIsSecondaryOrAdmin) pool = others.Where(MatchesNothing).ToList();

                        if (pool.Count == 1)
                        {
                            (ownerSam, ownerDisplay, ownerSource) = (pool[0].SamAccountName, pool[0].DisplayName, "EmployeeId");
                        }
                        else if (pool.Count > 1)
                        {
                            ownerSource = "Ambiguous";
                        }
                    }
                }

                results.Add(new ClassificationResult(d.DiscoveredAccountKey!.Value, accountClass, classSource, classRuleKey, ownerSam, ownerDisplay, ownerSource));
            }
        }

        return results;
    }

    private static string? FindDisplayName(IReadOnlyList<ClassificationSubject> discovered, IReadOnlyList<ClassificationSubject> lookups, ClassificationSubject d) =>
        discovered.Concat(lookups)
            .FirstOrDefault(s => string.Equals(s.DomainName, d.DomainName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.SamAccountName, d.OwnerOverrideSamAccountName, StringComparison.OrdinalIgnoreCase))?.DisplayName;

    private static string? EmployeeIdOf(AccountClassificationSettings settings, ClassificationSubject s) =>
        settings.EmployeeIdAttribute == "employeeNumber" ? s.EmployeeNumber : s.EmployeeId;

    private static bool IsSamRule(AccountClassificationRule rule) =>
        rule.AttributeName is null || string.Equals(rule.AttributeName, "sAMAccountName", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first enabled rule (in evaluation order) the account matches, or null.</summary>
    public static AccountClassificationRule? FirstMatch(IReadOnlyList<AccountClassificationRule> orderedEnabledRules, ClassificationSubject subject) =>
        orderedEnabledRules.FirstOrDefault(rule => Matches(rule, subject));

    public static bool Matches(AccountClassificationRule rule, ClassificationSubject subject)
    {
        var value = rule.Value?.Trim() ?? "";
        switch (rule.RuleType)
        {
            case "Attribute":
            {
                var name = AccountClassificationAttributes.CanonicalAttribute(rule.AttributeName);
                if (name is null) return false;
                var actual = subject.Attributes.FirstOrDefault(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
                if (string.IsNullOrEmpty(actual)) return false;
                return rule.Operator switch
                {
                    "IsSet" => true,
                    "Equals" => string.Equals(actual, value, StringComparison.OrdinalIgnoreCase),
                    "Contains" => value.Length > 0 && actual.Contains(value, StringComparison.OrdinalIgnoreCase),
                    "StartsWith" => value.Length > 0 && actual.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            }
            case "Name":
            {
                var name = IsSamRule(rule) ? subject.SamAccountName : subject.UserPrincipalName;
                return !string.IsNullOrEmpty(name) && NameRegex(rule)?.IsMatch(name) == true;
            }
            case "OU":
            {
                if (string.IsNullOrEmpty(subject.DistinguishedName) || value.Length == 0) return false;
                var dn = NormalizeDn(subject.DistinguishedName);
                var ou = NormalizeDn(value);
                return rule.Operator switch
                {
                    "Contains" => dn.Contains(ou, StringComparison.OrdinalIgnoreCase),
                    "EndsWith" => dn.EndsWith(ou, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            }
            default:
                return false;
        }
    }

    /// <summary>The owner's sAMAccountName a Name rule gives for <paramref name="samAccountName"/>: the name without the prefix/suffix, or what a pattern's single * matched. Null when it doesn't match or nothing is left.</summary>
    public static string? OwnerFromName(AccountClassificationRule rule, string samAccountName)
    {
        if (rule.RuleType != "Name" || !IsSamRule(rule)) return null;
        var regex = NameRegex(rule);
        if (regex is null) return null;
        var match = regex.Match(samAccountName);
        if (!match.Success || match.Groups.Count < 2) return null;
        var owner = match.Groups[1].Value.Trim();
        return owner.Length == 0 ? null : owner;
    }

    /// <summary>StartsWith v: ^v(.*)$; EndsWith v: ^(.*)v$; Pattern: * is any text, ? one character, and the text a single * matched is the owner.</summary>
    private static Regex? NameRegex(AccountClassificationRule rule)
    {
        var value = rule.Value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        string pattern;
        switch (rule.Operator)
        {
            case "StartsWith":
                pattern = "^" + Regex.Escape(value) + "(.*)$";
                break;
            case "EndsWith":
                pattern = "^(.*)" + Regex.Escape(value) + "$";
                break;
            case "Pattern":
                var stars = value.Count(c => c == '*');
                var builder = new StringBuilder("^");
                foreach (var c in value)
                {
                    builder.Append(c switch
                    {
                        '*' => stars == 1 ? "(.*)" : ".*",
                        '?' => ".",
                        _ => Regex.Escape(c.ToString())
                    });
                }
                pattern = builder.Append('$').ToString();
                break;
            default:
                return null;
        }
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static string NormalizeDn(string dn) => Regex.Replace(dn.Trim(), @"\s*,\s*", ",");

    /// <summary>
    /// Validates and normalizes a rule as entered: null error and the cleaned rule, or the error to return as a 400.
    /// The attribute name must be on the allow-list (Attribute) or sAMAccountName/userPrincipalName (Name).
    /// </summary>
    public static (string? Error, AccountClassificationRule? Rule) Validate(SaveAccountClassificationRuleRequest request)
    {
        var ruleType = new[] { "Attribute", "Name", "OU" }.FirstOrDefault(t => string.Equals(t, request.RuleType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (ruleType is null) return ("Rule type must be Attribute, Name or OU.", null);

        var accountClass = AccountClasses.Canonical(AccountClasses.RuleClasses, request.AccountClass);
        if (accountClass is null) return ("Class must be Primary, Secondary or Admin.", null);

        var value = string.IsNullOrWhiteSpace(request.Value) ? null : request.Value.Trim();
        if (value?.Length > 500) return ("Value must be 500 characters or fewer.", null);

        string? attributeName;
        string[] operators;
        switch (ruleType)
        {
            case "Attribute":
                attributeName = AccountClassificationAttributes.CanonicalAttribute(request.AttributeName);
                if (attributeName is null)
                {
                    return ($"Attribute must be one of: {string.Join(", ", AccountClassificationAttributes.Allowed)}.", null);
                }
                operators = ["Equals", "Contains", "StartsWith", "IsSet"];
                break;
            case "Name":
                attributeName = string.IsNullOrWhiteSpace(request.AttributeName)
                    ? "sAMAccountName"
                    : AccountClassificationAttributes.NameAttributes.FirstOrDefault(a => string.Equals(a, request.AttributeName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (attributeName is null) return ("A name rule checks sAMAccountName or userPrincipalName.", null);
                operators = ["StartsWith", "EndsWith", "Pattern"];
                break;
            default:
                attributeName = null;
                operators = ["Contains", "EndsWith"];
                break;
        }

        var op = operators.FirstOrDefault(o => string.Equals(o, request.Operator?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (op is null) return ($"Operator must be one of: {string.Join(", ", operators)}.", null);
        if (op != "IsSet" && value is null) return ("Value is required.", null);
        if (op == "IsSet") value = null;
        if (op == "Pattern" && value!.All(c => c is '*' or '?')) return ("A pattern needs some text besides * and ?.", null);

        return (null, new AccountClassificationRule
        {
            RuleType = ruleType,
            AttributeName = attributeName,
            Operator = op,
            Value = value,
            AccountClass = accountClass,
            IsEnabled = request.IsEnabled
        });
    }

    /// <summary>English summary for audit details and the export's Matched By, e.g. <c>Name: sAMAccountName starts with "adm." -> Admin</c>.</summary>
    public static string Describe(AccountClassificationRule rule)
    {
        var op = rule.Operator switch
        {
            "Equals" => "equals",
            "Contains" => "contains",
            "StartsWith" => "starts with",
            "EndsWith" => "ends with",
            "IsSet" => "is set",
            "Pattern" => "matches",
            _ => rule.Operator
        };
        var subject = rule.RuleType == "OU" ? "distinguished name" : rule.AttributeName;
        var value = rule.Operator == "IsSet" ? "" : $" \"{rule.Value}\"";
        return $"{rule.RuleType}: {subject} {op}{value} -> {rule.AccountClass}";
    }
}
