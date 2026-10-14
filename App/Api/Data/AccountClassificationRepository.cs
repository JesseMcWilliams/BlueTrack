using System.Text.Json;
using Dapper;
using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-233 (25_BlueTrack_AccountClassification.sql): the classification settings row and rules, the per-domain
/// owner lookups discovery reads from AD, what the classifier needs from web.discovered_account, and writing
/// its results back (one OPENJSON update, not a row at a time).
/// </summary>
public sealed class AccountClassificationRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<AccountClassificationSettings> GetSettingsAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AccountClassificationSettings>("""
            SELECT c.IsEnabled, c.DefaultAccountClass, c.OwnerByEmployeeId, c.EmployeeIdAttribute, c.ModifiedDate, u.DisplayName AS ModifiedByName
            FROM web.account_classification_config c
            LEFT JOIN web.app_user u ON u.UserKey = c.ModifiedBy
            WHERE c.AccountClassificationConfigKey = 1
            """) ?? new AccountClassificationSettings();
    }

    public async Task SaveSettingsAsync(AccountClassificationSettings settings, int userKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            MERGE web.account_classification_config AS tgt
            USING (SELECT 1 AS AccountClassificationConfigKey) AS src ON tgt.AccountClassificationConfigKey = src.AccountClassificationConfigKey
            WHEN MATCHED THEN UPDATE SET IsEnabled = @IsEnabled, DefaultAccountClass = @DefaultAccountClass, OwnerByEmployeeId = @OwnerByEmployeeId,
                EmployeeIdAttribute = @EmployeeIdAttribute, ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (AccountClassificationConfigKey, IsEnabled, DefaultAccountClass, OwnerByEmployeeId, EmployeeIdAttribute, ModifiedBy, ModifiedDate)
                VALUES (1, @IsEnabled, @DefaultAccountClass, @OwnerByEmployeeId, @EmployeeIdAttribute, @userKey, SYSUTCDATETIME());
            """, new { settings.IsEnabled, settings.DefaultAccountClass, settings.OwnerByEmployeeId, settings.EmployeeIdAttribute, userKey });
    }

    private const string RuleColumns =
        "AccountClassificationRuleKey, RuleType, AttributeName, Operator, Value, AccountClass, IsEnabled, DisplayOrder, CreatedDate, ModifiedDate";

    /// <summary>Every rule (enabled or not) in evaluation order: Attribute, Name, OU, then DisplayOrder.</summary>
    public async Task<IReadOnlyList<AccountClassificationRule>> GetRulesAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<AccountClassificationRule>($"""
            SELECT {RuleColumns} FROM web.account_classification_rule
            ORDER BY CASE RuleType WHEN 'Attribute' THEN 0 WHEN 'Name' THEN 1 ELSE 2 END, DisplayOrder, AccountClassificationRuleKey
            """)).AsList();
    }

    public async Task<AccountClassificationRule?> GetRuleAsync(int ruleKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AccountClassificationRule>(
            $"SELECT {RuleColumns} FROM web.account_classification_rule WHERE AccountClassificationRuleKey = @ruleKey", new { ruleKey });
    }

    /// <summary>A new rule goes last within its type.</summary>
    public async Task<int> CreateRuleAsync(AccountClassificationRule rule, int userKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.account_classification_rule (RuleType, AttributeName, Operator, Value, AccountClass, IsEnabled, DisplayOrder, CreatedBy)
            OUTPUT inserted.AccountClassificationRuleKey
            SELECT @RuleType, @AttributeName, @Operator, @Value, @AccountClass, @IsEnabled,
                   ISNULL((SELECT MAX(DisplayOrder) FROM web.account_classification_rule WITH (UPDLOCK, HOLDLOCK) WHERE RuleType = @RuleType), 0) + 1, @userKey
            """, new { rule.RuleType, rule.AttributeName, rule.Operator, rule.Value, rule.AccountClass, rule.IsEnabled, userKey });
    }

    /// <summary>Changing a rule's type moves it to the end of its new type.</summary>
    public async Task<bool> UpdateRuleAsync(int ruleKey, AccountClassificationRule rule, int userKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("""
            UPDATE r SET
                DisplayOrder = CASE WHEN r.RuleType = @RuleType THEN r.DisplayOrder
                    ELSE ISNULL((SELECT MAX(o.DisplayOrder) FROM web.account_classification_rule o WHERE o.RuleType = @RuleType), 0) + 1 END,
                RuleType = @RuleType, AttributeName = @AttributeName, Operator = @Operator, Value = @Value, AccountClass = @AccountClass,
                IsEnabled = @IsEnabled, ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
            FROM web.account_classification_rule r
            WHERE r.AccountClassificationRuleKey = @ruleKey
            """, new { ruleKey, rule.RuleType, rule.AttributeName, rule.Operator, rule.Value, rule.AccountClass, rule.IsEnabled, userKey }) > 0;
    }

    public async Task<bool> DeleteRuleAsync(int ruleKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM web.account_classification_rule WHERE AccountClassificationRuleKey = @ruleKey", new { ruleKey }) > 0;
    }

    /// <summary>Swaps the rule with its neighbour of the same type (up = earlier). False when it's already first/last or doesn't exist.</summary>
    public async Task<bool> MoveRuleAsync(int ruleKey, bool up, int userKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var rules = (await connection.QueryAsync<(int Key, string RuleType, int DisplayOrder)>("""
            SELECT AccountClassificationRuleKey, RuleType, DisplayOrder FROM web.account_classification_rule WITH (UPDLOCK, HOLDLOCK)
            WHERE RuleType = (SELECT RuleType FROM web.account_classification_rule WHERE AccountClassificationRuleKey = @ruleKey)
            ORDER BY DisplayOrder, AccountClassificationRuleKey
            """, new { ruleKey }, transaction)).ToList();
        var index = rules.FindIndex(r => r.Key == ruleKey);
        var other = up ? index - 1 : index + 1;
        if (index < 0 || other < 0 || other >= rules.Count)
        {
            transaction.Commit();
            return false;
        }

        // Renumber the whole type 1..n with the two swapped, so equal DisplayOrders can't make the move a no-op.
        (rules[index], rules[other]) = (rules[other], rules[index]);
        await connection.ExecuteAsync(
            "UPDATE web.account_classification_rule SET DisplayOrder = @Order, ModifiedBy = CASE WHEN AccountClassificationRuleKey = @ruleKey THEN @userKey ELSE ModifiedBy END, ModifiedDate = CASE WHEN AccountClassificationRuleKey = @ruleKey THEN SYSUTCDATETIME() ELSE ModifiedDate END WHERE AccountClassificationRuleKey = @Key",
            rules.Select((r, i) => new { r.Key, Order = i + 1, ruleKey, userKey }), transaction);
        transaction.Commit();
        return true;
    }

    private sealed record SubjectRow(int DiscoveredAccountKey, string DomainName, string SamAccountName, string? UserPrincipalName,
        string? DistinguishedName, string? DisplayName, bool IsEnabled, string? EmployeeId, string? EmployeeNumber, string? DirectoryAttributes,
        string? ClassOverride, string? OwnerOverrideSamAccountName);

    private sealed record LookupRow(string DomainName, string SamAccountName, string? UserPrincipalName, string? DistinguishedName,
        string? DisplayName, bool IsEnabled, string? EmployeeId, string? EmployeeNumber, string? DirectoryAttributes);

    /// <summary>Every discovered account (any Status) and every owner lookup, as the classifier takes them.</summary>
    public async Task<(IReadOnlyList<ClassificationSubject> Discovered, IReadOnlyList<ClassificationSubject> OwnerLookups)> GetSubjectsAsync()
    {
        using var connection = connectionFactory.Create();
        using var grid = await connection.QueryMultipleAsync("""
            SELECT DiscoveredAccountKey, DomainName, SamAccountName, UserPrincipalName, DistinguishedName, DisplayName, IsEnabled,
                   EmployeeId, EmployeeNumber, DirectoryAttributes, ClassOverride, OwnerOverrideSamAccountName
            FROM web.discovered_account;
            SELECT DomainName, SamAccountName, UserPrincipalName, DistinguishedName, DisplayName, IsEnabled, EmployeeId, EmployeeNumber, DirectoryAttributes
            FROM web.ad_owner_lookup;
            """);
        var discovered = (await grid.ReadAsync<SubjectRow>())
            .Select(r => new ClassificationSubject(r.DiscoveredAccountKey, r.DomainName, r.SamAccountName, r.UserPrincipalName, r.DistinguishedName,
                r.DisplayName, r.IsEnabled, r.EmployeeId, r.EmployeeNumber, ParseAttributes(r.DirectoryAttributes), r.ClassOverride, r.OwnerOverrideSamAccountName))
            .ToList();
        var lookups = (await grid.ReadAsync<LookupRow>())
            .Select(r => new ClassificationSubject(null, r.DomainName, r.SamAccountName, r.UserPrincipalName, r.DistinguishedName,
                r.DisplayName, r.IsEnabled, r.EmployeeId, r.EmployeeNumber, ParseAttributes(r.DirectoryAttributes)))
            .ToList();
        return (discovered, lookups);
    }

    /// <summary>Writes every result in one statement; also clears the class/owner of any discovered account not in <paramref name="results"/>.</summary>
    public async Task<int> SaveResultsAsync(IReadOnlyList<ClassificationResult> results)
    {
        using var connection = connectionFactory.Create();
        var json = JsonSerializer.Serialize(results);
        return await connection.ExecuteAsync("""
            UPDATE da SET
                AccountClass = j.AccountClass, ClassSource = j.ClassSource, ClassRuleKey = j.ClassRuleKey,
                OwnerSamAccountName = j.OwnerSamAccountName, OwnerDisplayName = j.OwnerDisplayName, OwnerSource = j.OwnerSource,
                ClassifiedDate = SYSUTCDATETIME()
            FROM web.discovered_account da
            LEFT JOIN OPENJSON(@json) WITH (
                DiscoveredAccountKey INT '$.DiscoveredAccountKey',
                AccountClass NVARCHAR(20) '$.AccountClass',
                ClassSource NVARCHAR(20) '$.ClassSource',
                ClassRuleKey INT '$.ClassRuleKey',
                OwnerSamAccountName NVARCHAR(300) '$.OwnerSamAccountName',
                OwnerDisplayName NVARCHAR(300) '$.OwnerDisplayName',
                OwnerSource NVARCHAR(20) '$.OwnerSource'
            ) j ON j.DiscoveredAccountKey = da.DiscoveredAccountKey
            """, new { json });
    }

    /// <summary>Replaces one domain's owner lookups with what this discovery run read.</summary>
    public async Task ReplaceOwnerLookupsAsync(string domainName, IReadOnlyList<DirectoryUser> users)
    {
        using var connection = connectionFactory.Create();
        var rows = users
            .GroupBy(u => u.SamAccountName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(u => new
            {
                u.SamAccountName, u.UserPrincipalName, u.DistinguishedName, u.DisplayName, u.IsEnabled, u.EmployeeId, u.EmployeeNumber,
                DirectoryAttributes = SerializeAttributes(u.Attributes)
            });
        await connection.ExecuteAsync("""
            DELETE FROM web.ad_owner_lookup WHERE DomainName = @domainName;
            INSERT INTO web.ad_owner_lookup (DomainName, SamAccountName, UserPrincipalName, DistinguishedName, DisplayName, IsEnabled, EmployeeId, EmployeeNumber, DirectoryAttributes)
            SELECT @domainName, SamAccountName, UserPrincipalName, DistinguishedName, DisplayName, IsEnabled, EmployeeId, EmployeeNumber, DirectoryAttributes
            FROM OPENJSON(@json) WITH (
                SamAccountName NVARCHAR(300), UserPrincipalName NVARCHAR(300), DistinguishedName NVARCHAR(1000), DisplayName NVARCHAR(300),
                IsEnabled BIT, EmployeeId NVARCHAR(100), EmployeeNumber NVARCHAR(100), DirectoryAttributes NVARCHAR(MAX)
            );
            """, new { domainName, json = JsonSerializer.Serialize(rows) });
    }

    private sealed record OverrideRow(int DiscoveredAccountKey, string? ClassOverride, string? OwnerOverrideSamAccountName);

    /// <summary>D-233 per-row override; null for both clears it. False if the account doesn't exist.</summary>
    public async Task<(bool Found, string? OldClass, string? OldOwner)> SaveOverrideAsync(int discoveredAccountKey, string? accountClass, string? ownerSamAccountName, int userKey)
    {
        using var connection = connectionFactory.Create();
        var old = await connection.QuerySingleOrDefaultAsync<OverrideRow>("""
            DECLARE @old TABLE (DiscoveredAccountKey INT, ClassOverride NVARCHAR(20), OwnerOverrideSamAccountName NVARCHAR(300));
            UPDATE web.discovered_account
            SET ClassOverride = @accountClass, OwnerOverrideSamAccountName = @ownerSamAccountName,
                OverrideBy = CASE WHEN @accountClass IS NULL AND @ownerSamAccountName IS NULL THEN NULL ELSE @userKey END,
                OverrideDate = CASE WHEN @accountClass IS NULL AND @ownerSamAccountName IS NULL THEN NULL ELSE SYSUTCDATETIME() END
            OUTPUT inserted.DiscoveredAccountKey, deleted.ClassOverride, deleted.OwnerOverrideSamAccountName INTO @old
            WHERE DiscoveredAccountKey = @discoveredAccountKey;
            SELECT DiscoveredAccountKey, ClassOverride, OwnerOverrideSamAccountName FROM @old;
            """, new { discoveredAccountKey, accountClass, ownerSamAccountName, userKey });
        return old is null ? (false, null, null) : (true, old.ClassOverride, old.OwnerOverrideSamAccountName);
    }

    public static IReadOnlyDictionary<string, string> ParseAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } parsed
                ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string? SerializeAttributes(IReadOnlyDictionary<string, string>? attributes) =>
        attributes is null ? null : JsonSerializer.Serialize(attributes);
}
