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
    public required IReadOnlyList<ImportRowError> Errors { get; init; }

    public string Summary =>
        $"{CreatedCount} created, {UpdatedCount} updated, {RestoredCount} restored, {DeletedCount} marked deleted, {UnchangedCount} unchanged, {LinkedCount} linked to CyberArk, {Errors.Count} errors";
}
