using System.Text.RegularExpressions;

namespace BlueTrack.Api.Data;

/// <summary>D-293: what Admin > Platform Mapping suggests for an unmapped platform. Basis is OtherSource or Keyword.</summary>
public sealed record PlatformMappingSuggestion(
    int AccountTypeKey, string AccountTypeName, int? SorKey, string? SourceOfRecord, string Basis, string Detail);

/// <summary>
/// D-293 (the user's choice, 2026-10-21): a suggestion for each unmapped platform, which the admin accepts or not;
/// nothing is saved by suggesting. First the same platform ID already mapped in another source (Self-Hosted and
/// Privilege Cloud often share platform IDs), then keywords in the platform's ID and name. Group platforms get none.
/// Keyword rules name the account type and source of record by their seeded names; one an admin renamed or
/// removed simply isn't suggested.
/// </summary>
public static partial class PlatformMappingSuggester
{
    private sealed record KeywordRule(string[] Keywords, string AccountType, string? SourceOfRecord);

    // First match wins, so the domain rule comes before the local Windows one ("WinDomain" vs "WinServerLocal").
    // A keyword matches a whole word of the ID or name (split at punctuation and camelCase, digits kept with the
    // word before them: "DB2"), or, from five characters, any part of it -- so short ones like "f5", "aws" or
    // "win" don't match inside longer words. The Infrastructure rule comes before the local OS one, so a platform
    // for CyberArk's own components isn't taken for a server's local accounts.
    private static readonly KeywordRule[] Rules =
    [
        new(["breakglass", "firecall", "emergency"], "Emergency/Break-glass", null),
        new(["windomain", "activedirectory", "domain", "dom"], "Domain Account", "Active Directory"),
        new(["ldap", "directory"], "Domain Account", "LDAP"),
        new(["oracle", "mssql", "sqlserver", "mysql", "mariadb", "postgres", "postgresql", "db2", "informix", "sybase", "mongodb", "hana", "teradata", "snowflake", "redshift", "database"],
            "Database Account", "Local Database"),
        new(["cisco", "juniper", "f5", "bigip", "paloalto", "fortinet", "fortigate", "checkpoint", "arista", "netscaler", "router", "switch", "firewall"],
            "Network Device", null),
        new(["aws", "azure", "gcp", "oci"], "Cloud IAM", null),
        new(["psm", "cpm", "pvwa", "vault", "cyberark"], "Infrastructure (PSM/CPM)", "Local Application"),
        new(["winserverlocal", "windesktoplocal", "winlocal", "windows", "win", "unix", "linux", "lnx", "ssh", "aix", "solaris", "hpux", "as400", "iseries", "esx", "esxi", "vmware"],
            "Local/OS Account", "Local OS"),
        new(["webapp", "website", "api", "app", "service", "schedtask"], "Application/Service Account", "Local Application"),
    ];

    [GeneratedRegex("[A-Z]+[0-9]*(?![a-z])|[A-Z]?[a-z]+[0-9]*|[0-9]+")]
    private static partial Regex WordPattern();

    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant());

    /// <summary>The keyword that matches the platform's ID or name, with its rule, or null.</summary>
    private static (KeywordRule Rule, string Keyword)? MatchKeyword(string platformId, string? platformName)
    {
        var texts = new[] { platformId, platformName ?? "" };
        var words = texts.SelectMany(Words).ToHashSet();
        // Words joined, so "Win Domain" and "WinDomain" both contain "windomain".
        var joined = string.Concat(texts.Select(t => string.Concat(Words(t))));
        foreach (var rule in Rules)
        {
            foreach (var keyword in rule.Keywords)
            {
                if (words.Contains(keyword) || (keyword.Length >= 5 && joined.Contains(keyword, StringComparison.Ordinal)))
                {
                    return (rule, keyword);
                }
            }
        }
        return null;
    }

    /// <summary>Adds a suggestion to each unmapped, non-group row that has one; other rows are returned unchanged.</summary>
    public static IReadOnlyList<PlatformMappingRow> Suggest(IReadOnlyList<PlatformMappingRow> rows, PlatformMappingOptions options)
    {
        var typeByName = options.AccountTypes.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var sorByName = options.SourcesOfRecord.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var mappedById = rows
            .Where(r => !r.IsUnmapped && r.AccountTypeKey is not null)
            .GroupBy(r => r.PlatformId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return rows.Select(row =>
        {
            if (!row.IsUnmapped || string.Equals(row.PlatformType, "group", StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }

            if (mappedById.TryGetValue(row.PlatformId, out var other) && other.PlatformKey != row.PlatformKey)
            {
                return row with
                {
                    Suggestion = new PlatformMappingSuggestion(other.AccountTypeKey!.Value, other.AccountTypeName!, other.SorKey,
                        other.SourceOfRecord, "OtherSource", other.SourceSystemName)
                };
            }

            if (MatchKeyword(row.PlatformId, row.PlatformName) is var (rule, keyword) && typeByName.TryGetValue(rule.AccountType, out var type))
            {
                PlatformMappingChoice? sor = rule.SourceOfRecord is not null && sorByName.TryGetValue(rule.SourceOfRecord, out var found) ? found : null;
                return row with { Suggestion = new PlatformMappingSuggestion(type.Key, type.Name, sor?.Key, sor?.Name, "Keyword", keyword) };
            }

            return row;
        }).ToList();
    }
}
