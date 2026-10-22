using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.Models;

/// <summary>
/// D-212: result of an AccountInventory feed's import. A row whose PlatformName
/// matched no platform is imported and also listed in Errors.
/// </summary>
public sealed class AccountInventoryImportResult
{
    public int TotalRows { get; init; }
    public int CreatedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int RestoredCount { get; init; }
    public int DeletedCount { get; init; }
    public int UnchangedCount { get; init; }
    /// <summary>Phase 4b: feed accounts newly linked to a CyberArk account by this run.</summary>
    public int LinkedCount { get; init; }
    /// <summary>Phase 4c: new possible matches proposed for the Possible Account Matches report.</summary>
    public int PossibleMatchCount { get; init; }
    /// <summary>D-237: rows skipped because the feed skips group rows and the UserName is a group.</summary>
    public int SkippedGroupCount { get; init; }
    public required IReadOnlyList<ImportRowError> Errors { get; init; }

    public string Summary =>
        $"{ServerTextCulture.Count(CreatedCount)} created, {ServerTextCulture.Count(UpdatedCount)} updated, {ServerTextCulture.Count(RestoredCount)} restored, {ServerTextCulture.Count(DeletedCount)} marked deleted, {ServerTextCulture.Count(UnchangedCount)} unchanged, {ServerTextCulture.Count(LinkedCount)} linked to CyberArk, {ServerTextCulture.Count(PossibleMatchCount)} possible matches, {ServerTextCulture.Count(Errors.Count)} errors{(SkippedGroupCount > 0 ? $", {ServerTextCulture.Count(SkippedGroupCount)} group rows skipped" : "")}";
}
