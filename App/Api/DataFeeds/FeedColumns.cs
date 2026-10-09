using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-181: the columns each import can't do without, for the Data Sources
/// Test button. Mirrors the "X is required" checks in the import services
/// (App/Api/Imports/); a column that's only sometimes needed (e.g.
/// ApplicationName, required only for a new application) isn't listed.
/// Each entry is a set of alternatives, any one of which will do
/// (AccountName or AccountKey).
/// </summary>
public static class FeedColumns
{
    private static readonly Dictionary<string, string[][]> RequiredFields = new()
    {
        ["TargetInventory"] = [["TargetType"], ["TargetName"], ["RiskScore"]],
        ["AccessGroupInventory"] = [["GroupName"], ["GroupIdentifier"], ["BaseRiskScore"]],
        ["AccessGroupTargetMap"] = [["GroupIdentifier"], ["TargetIdentifierType"], ["TargetIdentifierValue"]],
        ["AccountAccessGroupMembership"] = [["AccountName", "AccountKey"], ["GroupIdentifier"]],
        ["AccountTargetMap"] = [["AccountName", "AccountKey"], ["TargetIdentifierType"], ["TargetIdentifierValue"]],
        ["Applications"] = [["ApplicationCode"]],
        ["SafeAssignments"] = [["SafeName"], ["Application"]]
    };

    /// <summary>
    /// The required columns missing from <paramref name="header"/>, as the
    /// page shows them ("RiskScore", "AccountName or AccountKey"). A field
    /// the mapping profile maps is looked for under its source column name;
    /// one the profile gives a default value for is never missing.
    /// </summary>
    public static IReadOnlyList<string> FindMissing(string feedType, IReadOnlyList<string> header,
        IReadOnlyDictionary<string, ImportMappingService.FieldMapping> mapping)
    {
        if (!RequiredFields.TryGetValue(feedType, out var required)) return [];
        var present = new HashSet<string>(header.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);

        bool Satisfied(string field) => mapping.TryGetValue(field, out var m)
            ? m.DefaultValue is not null || present.Contains(m.SourceColumnName)
            : present.Contains(field);

        string ColumnName(string field) => mapping.TryGetValue(field, out var m) ? m.SourceColumnName : field;

        return required
            .Where(alternatives => !alternatives.Any(Satisfied))
            .Select(alternatives => string.Join(" or ", alternatives.Select(ColumnName)))
            .ToList();
    }
}
