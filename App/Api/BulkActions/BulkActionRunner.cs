using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.BulkActions;

/// <summary>
/// D-190: the shared mechanics of a bulk action on a list -- checks the
/// request (keys present, within app_config.BulkEditMaxAccounts, a reason
/// when one is required), then runs the action for each key in turn so one
/// item's failure is reported and doesn't stop the rest.
/// </summary>
public sealed class BulkActionRunner(AppConfigRepository appConfigRepository)
{
    public const int MaxReasonLength = 1000;

    /// <summary>A skipped item's reason; an action returns this instead of throwing for an expected refusal.</summary>
    public sealed class SkipException(string reason, string? name = null) : Exception(reason)
    {
        public string? Name { get; } = name;
    }

    /// <summary>Null when usable; otherwise why not (a 400 for the whole request).</summary>
    public async Task<string?> ValidateAsync(IReadOnlyList<int>? keys, bool reasonRequired = false, string? reason = null)
    {
        var count = keys?.Distinct().Count() ?? 0;
        if (count == 0) return "Select at least one item.";
        var max = (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;
        if (count > max) return $"At most {max} items can be changed at once; {count} were selected.";
        if (reasonRequired && string.IsNullOrWhiteSpace(reason)) return "A reason is required.";
        if (reason is not null && reason.Trim().Length > MaxReasonLength) return $"The reason must be {MaxReasonLength} characters or fewer.";
        return null;
    }

    public async Task<int> GetMaxItemsAsync() => (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;

    /// <param name="action">Runs one item and returns its name (for reporting); throw SkipException to skip it with a reason.</param>
    public static async Task<BulkActionResult> RunAsync(IReadOnlyList<int> keys, Func<int, Task<string?>> action)
    {
        var distinct = keys.Distinct().ToList();
        var changed = 0;
        var skipped = new List<BulkSkippedItem>();
        foreach (var key in distinct)
        {
            try
            {
                await action(key);
                changed++;
            }
            catch (SkipException ex)
            {
                skipped.Add(new BulkSkippedItem { Key = key, Name = ex.Name, Reason = ex.Message });
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (Errors.SqlConstraintExceptionHandler.Classify(ex) is { } constraint)
            {
                // D-194: a constraint refused it, e.g. deleting a Target that access groups or accounts still point at.
                skipped.Add(new BulkSkippedItem { Key = key, Reason = constraint.Detail });
            }
            catch (Exception ex)
            {
                skipped.Add(new BulkSkippedItem { Key = key, Reason = $"Could not save: {ex.Message}" });
            }
        }
        return new BulkActionResult { Requested = distinct.Count, Changed = changed, Skipped = skipped };
    }
}
