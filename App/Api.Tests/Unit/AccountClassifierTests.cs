using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-233: AccountClassifier on its own -- each rule type, evaluation order and enable/disable, the default class,
/// owners by stripped name and by employee ID (including an owner that isn't discovered, and an ambiguous ID),
/// overrides, and rule validation (the attribute allow-list).
/// </summary>
public class AccountClassifierTests
{
    private const string Domain = "corp.example";
    private static int _nextRuleKey = 1;

    private static readonly AccountClassificationSettings On = new() { IsEnabled = true };

    private static AccountClassificationRule Rule(string type, string op, string? value, string accountClass, string? attribute = null, bool enabled = true, int order = 1) => new()
    {
        AccountClassificationRuleKey = Interlocked.Increment(ref _nextRuleKey),
        RuleType = type,
        AttributeName = attribute ?? (type == "Name" ? "sAMAccountName" : null),
        Operator = op,
        Value = value,
        AccountClass = accountClass,
        IsEnabled = enabled,
        DisplayOrder = order
    };

    private static int _nextAccountKey = 1;

    private static ClassificationSubject Account(string sam, string? dn = null, string? employeeId = null, Dictionary<string, string>? attributes = null,
        bool enabled = true, string? upn = null, string? classOverride = null, string? ownerOverride = null, bool discovered = true, string? employeeNumber = null) =>
        new(discovered ? Interlocked.Increment(ref _nextAccountKey) : null, Domain, sam, upn, dn ?? $"CN={sam},OU=Users,DC=corp,DC=example",
            $"Name of {sam}", enabled, employeeId, employeeNumber, attributes ?? [], classOverride, ownerOverride);

    private static ClassificationResult One(AccountClassificationSettings settings, IEnumerable<AccountClassificationRule> rules, ClassificationSubject subject,
        IReadOnlyList<ClassificationSubject>? others = null, IReadOnlyList<ClassificationSubject>? lookups = null) =>
        AccountClassifier.Classify(settings, rules, [subject, .. others ?? []], lookups ?? [])
            .Single(r => r.DiscoveredAccountKey == subject.DiscoveredAccountKey);

    [Fact]
    public void AttributeRule_EachOperator()
    {
        var account = Account("svc1", attributes: new() { ["adminCount"] = "1", ["extensionAttribute5"] = "Tier0 Admin" });
        Assert.Equal("Admin", One(On, [Rule("Attribute", "Equals", "1", "Admin", "adminCount")], account).AccountClass);
        Assert.Equal("Admin", One(On, [Rule("Attribute", "Contains", "tier0", "Admin", "extensionAttribute5")], account).AccountClass);
        Assert.Equal("Secondary", One(On, [Rule("Attribute", "StartsWith", "TIER0", "Secondary", "extensionAttribute5")], account).AccountClass);
        Assert.Equal("Admin", One(On, [Rule("Attribute", "IsSet", null, "Admin", "adminCount")], account).AccountClass);
        Assert.Equal("Unknown", One(On, [Rule("Attribute", "IsSet", null, "Admin", "employeeType")], account).AccountClass);
        Assert.Equal("Unknown", One(On, [Rule("Attribute", "Equals", "0", "Admin", "adminCount")], account).AccountClass);
    }

    [Fact]
    public void NameRule_PrefixSuffixPatternAndUpn()
    {
        Assert.Equal("Admin", One(On, [Rule("Name", "StartsWith", "adm.", "Admin")], Account("ADM.jsmith")).AccountClass);
        Assert.Equal("Secondary", One(On, [Rule("Name", "EndsWith", "-a", "Secondary")], Account("jsmith-a")).AccountClass);
        Assert.Equal("Admin", One(On, [Rule("Name", "Pattern", "a?-*", "Admin")], Account("a1-jsmith")).AccountClass);
        Assert.Equal("Unknown", One(On, [Rule("Name", "Pattern", "a?-*", "Admin")], Account("ab1-jsmith")).AccountClass);
        Assert.Equal("Admin", One(On, [Rule("Name", "EndsWith", ".adm@corp.example", "Admin", "userPrincipalName")],
            Account("jsmithadm", upn: "jsmith.adm@corp.example")).AccountClass);
        // Regex characters in the value are literal.
        Assert.Equal("Unknown", One(On, [Rule("Name", "StartsWith", "a.m", "Admin")], Account("axmjsmith")).AccountClass);
    }

    [Fact]
    public void OuRule_ContainsAndEndsWith_IgnoringCaseAndSpacesAfterCommas()
    {
        var account = Account("x1", dn: "CN=x1,OU=Admin Accounts,OU=Corp,DC=corp,DC=example");
        Assert.Equal("Admin", One(On, [Rule("OU", "Contains", "ou=admin accounts, ou=corp", "Admin")], account).AccountClass);
        Assert.Equal("Secondary", One(On, [Rule("OU", "EndsWith", "OU=Admin Accounts,OU=Corp,DC=corp,DC=example", "Secondary")], account).AccountClass);
        Assert.Equal("Unknown", One(On, [Rule("OU", "EndsWith", "OU=Admin Accounts", "Secondary")], account).AccountClass);
    }

    [Fact]
    public void AttributeRulesRunFirst_ThenName_ThenOu_EachInOrder_AndDisabledRulesAreSkipped()
    {
        var account = Account("adm.jsmith", dn: "CN=adm.jsmith,OU=Service,DC=corp,DC=example", attributes: new() { ["employeeType"] = "Contractor" });
        var ou = Rule("OU", "Contains", "OU=Service", "Primary", order: 1);
        var name = Rule("Name", "StartsWith", "adm.", "Admin", order: 1);
        var attribute = Rule("Attribute", "Equals", "Contractor", "Secondary", "employeeType", order: 5);

        var result = One(On, [ou, name, attribute], account);
        Assert.Equal(("Secondary", "Rule", attribute.AccountClassificationRuleKey), (result.AccountClass, result.ClassSource, result.ClassRuleKey));

        var disabledAttribute = Rule("Attribute", "Equals", "Contractor", "Secondary", "employeeType", enabled: false);
        Assert.Equal("Admin", One(On, [ou, name, disabledAttribute], account).AccountClass);

        // Within a type, DisplayOrder decides.
        var second = Rule("Name", "StartsWith", "adm", "Secondary", order: 2);
        var first = Rule("Name", "StartsWith", "adm.", "Admin", order: 1);
        Assert.Equal("Admin", One(On, [second, first], account).AccountClass);
    }

    [Fact]
    public void NoMatch_GivesTheDefaultClass_AndClassificationOffGivesNothingButOverrides()
    {
        var plain = Account("jdoe");
        Assert.Equal(("Unknown", "Default"), (One(On, [], plain).AccountClass, One(On, [], plain).ClassSource));
        var primaryDefault = new AccountClassificationSettings { IsEnabled = true, DefaultAccountClass = "Primary" };
        var result = One(primaryDefault, [], plain);
        Assert.Equal(("Primary", "Self", "jdoe"), (result.AccountClass, result.OwnerSource, result.OwnerSamAccountName));

        var off = new AccountClassificationSettings { IsEnabled = false };
        var rule = Rule("Name", "StartsWith", "j", "Admin");
        Assert.Null(One(off, [rule], plain).AccountClass);
        var overridden = One(off, [rule], Account("jdoe2", classOverride: "Secondary", ownerOverride: "jdoe"));
        Assert.Equal(("Secondary", "Manual", "jdoe", "Manual"), (overridden.AccountClass, overridden.ClassSource, overridden.OwnerSamAccountName, overridden.OwnerSource));
    }

    [Fact]
    public void OwnerByStrippedName_FromADiscoveredAccountOrAnOwnerLookup_ButNotWhenUnknown()
    {
        var rules = new[] { Rule("Name", "StartsWith", "adm.", "Admin"), Rule("Name", "Pattern", "*-sa", "Secondary") };

        var discoveredOwner = One(On, rules, Account("adm.jsmith"), others: [Account("jsmith")]);
        Assert.Equal(("jsmith", "Name of jsmith", "Name"), (discoveredOwner.OwnerSamAccountName, discoveredOwner.OwnerDisplayName, discoveredOwner.OwnerSource));

        // The owner isn't a discovered account: it was read from AD only to be an owner.
        var lookedUp = One(On, rules, Account("bjones-sa"), lookups: [Account("bjones", discovered: false)]);
        Assert.Equal(("bjones", "Name"), (lookedUp.OwnerSamAccountName, lookedUp.OwnerSource));

        var notFound = One(On, rules, Account("adm.nobody"));
        Assert.Equal(("Admin", null, null), (notFound.AccountClass, notFound.OwnerSamAccountName, notFound.OwnerSource));
    }

    [Fact]
    public void OwnerByEmployeeId_ThePrimarySharingIt_OrTheOneMatchingNoRule_AndAmbiguousGivesNoOwner()
    {
        var settings = new AccountClassificationSettings { IsEnabled = true, OwnerByEmployeeId = true };
        var adminRule = Rule("Attribute", "Equals", "1", "Admin", "adminCount");
        var primaryRule = Rule("OU", "Contains", "OU=People", "Primary");
        var admin = Account("t0-jsmith", employeeId: "E100", attributes: new() { ["adminCount"] = "1" });

        // The owner matches no rule and isn't discovered.
        var plainOwner = Account("jsmith", employeeId: "e100", discovered: false);
        var result = One(settings, [adminRule], admin, lookups: [plainOwner]);
        Assert.Equal(("jsmith", "EmployeeId"), (result.OwnerSamAccountName, result.OwnerSource));

        // An explicit Primary wins over one that matches no rule.
        var person = Account("john.smith", employeeId: "E100", dn: "CN=john.smith,OU=People,DC=corp,DC=example");
        result = One(settings, [adminRule, primaryRule], admin, others: [person], lookups: [plainOwner]);
        Assert.Equal(("john.smith", "EmployeeId"), (result.OwnerSamAccountName, result.OwnerSource));

        // Two accounts matching no rule share the ID: ambiguous, no owner.
        result = One(settings, [adminRule], admin, lookups: [plainOwner, Account("jsmith2", employeeId: "E100", discovered: false)]);
        Assert.Equal((null, "Ambiguous"), (result.OwnerSamAccountName, result.OwnerSource));

        // A disabled account is never an owner.
        result = One(settings, [adminRule], admin, lookups: [Account("jsmith", employeeId: "E100", discovered: false, enabled: false)]);
        Assert.Null(result.OwnerSource);

        // employeeNumber instead of employeeID.
        var byNumber = new AccountClassificationSettings { IsEnabled = true, OwnerByEmployeeId = true, EmployeeIdAttribute = "employeeNumber" };
        result = One(byNumber, [adminRule], Account("t0-x", employeeNumber: "77", attributes: new() { ["adminCount"] = "1" }),
            lookups: [Account("x", employeeNumber: "77", discovered: false)]);
        Assert.Equal("x", result.OwnerSamAccountName);

        // Off: no owner by ID.
        result = One(On, [adminRule], admin, lookups: [plainOwner]);
        Assert.Null(result.OwnerSamAccountName);
    }

    [Fact]
    public void AnUnknownAccount_GetsAnOwnerOnlyFromAnExplicitPrimary()
    {
        var settings = new AccountClassificationSettings { IsEnabled = true, OwnerByEmployeeId = true };
        var unknown = Account("odd", employeeId: "E5");
        Assert.Null(One(settings, [], unknown, others: [Account("other", employeeId: "E5")]).OwnerSamAccountName);

        var primaryRule = Rule("Name", "Pattern", "p.*", "Primary");
        var result = One(settings, [primaryRule], unknown, others: [Account("p.odd", employeeId: "E5")]);
        Assert.Equal(("Unknown", "p.odd", "EmployeeId"), (result.AccountClass, result.OwnerSamAccountName, result.OwnerSource));
    }

    [Fact]
    public void ManualOverride_WinsForClassAndOwner_AndAnOverriddenPrimaryCanOwnOthers()
    {
        var rules = new[] { Rule("Name", "StartsWith", "adm.", "Admin") };
        var result = One(On, rules, Account("adm.jsmith", classOverride: "Secondary", ownerOverride: "jsmith"), others: [Account("jsmith")]);
        Assert.Equal(("Secondary", "Manual", null, "jsmith", "Name of jsmith", "Manual"),
            (result.AccountClass, result.ClassSource, result.ClassRuleKey, result.OwnerSamAccountName, result.OwnerDisplayName, result.OwnerSource));

        var settings = new AccountClassificationSettings { IsEnabled = true, OwnerByEmployeeId = true };
        var adminRule = Rule("Attribute", "IsSet", null, "Admin", "adminCount");
        result = One(settings, [adminRule], Account("a-1", employeeId: "9", attributes: new() { ["adminCount"] = "1" }),
            others: [Account("ambig1", employeeId: "9"), Account("chosen", employeeId: "9", classOverride: "Primary")]);
        Assert.Equal("chosen", result.OwnerSamAccountName);
    }

    [Fact]
    public void OwnersStayWithinTheirDomain()
    {
        var rules = new[] { Rule("Name", "StartsWith", "adm.", "Admin") };
        var admin = Account("adm.jsmith");
        var otherDomainOwner = Account("jsmith") with { DomainName = "other.example" };
        Assert.Null(AccountClassifier.Classify(On, rules, [admin, otherDomainOwner], [])
            .Single(r => r.DiscoveredAccountKey == admin.DiscoveredAccountKey).OwnerSamAccountName);
    }

    [Theory]
    [InlineData("Attribute", "userAccountControl", "Equals", "1", "Admin", "Attribute must be one of")]
    [InlineData("Attribute", "(objectClass=*)", "Equals", "1", "Admin", "Attribute must be one of")]
    [InlineData("Attribute", "adminCount", "Pattern", "1", "Admin", "Operator must be one of")]
    [InlineData("Name", "mail", "StartsWith", "adm.", "Admin", "sAMAccountName or userPrincipalName")]
    [InlineData("OU", null, "Equals", "OU=x", "Admin", "Operator must be one of")]
    [InlineData("Name", null, "StartsWith", "", "Admin", "Value is required")]
    [InlineData("Name", null, "Pattern", "**", "Admin", "besides * and ?")]
    [InlineData("Name", null, "StartsWith", "adm.", "Unknown", "Class must be")]
    [InlineData("Group", null, "Equals", "x", "Admin", "Rule type must be")]
    public void Validate_RejectsWhatIsNotAllowed(string type, string? attribute, string op, string value, string accountClass, string expected)
    {
        var (error, rule) = AccountClassifier.Validate(new SaveAccountClassificationRuleRequest
        {
            RuleType = type, AttributeName = attribute, Operator = op, Value = value, AccountClass = accountClass
        });
        Assert.Null(rule);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Validate_NormalizesCase_AndIsSetDropsTheValue()
    {
        var (error, rule) = AccountClassifier.Validate(new SaveAccountClassificationRuleRequest
        {
            RuleType = "attribute", AttributeName = "EXTENSIONATTRIBUTE15", Operator = "isset", Value = "ignored", AccountClass = "admin"
        });
        Assert.Null(error);
        Assert.Equal(("Attribute", "extensionAttribute15", "IsSet", null, "Admin"), (rule!.RuleType, rule.AttributeName, rule.Operator, rule.Value, rule.AccountClass));
        Assert.Equal("Attribute: extensionAttribute15 is set -> Admin", AccountClassifier.Describe(rule));
    }

    [Fact]
    public void LdapFilterValues_AreEscaped()
    {
        Assert.Equal(@"a\2a\28b\29\5c", AccountClassificationAttributes.EscapeFilterValue(@"a*(b)\"));
        Assert.Equal(20, AccountClassificationAttributes.Allowed.Count);
    }
}
