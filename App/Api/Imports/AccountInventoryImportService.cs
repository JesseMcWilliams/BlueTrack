using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Imports;

/// <summary>
/// D-212 (Data Sources phase 4a): an AccountInventory data feed's file -- the
/// accounts another system has -- imported under the feed's source system.
///   - Identity is UserName + Address within that source: SourceAccountId is
///     lowercase "username@address" (both trimmed).
///   - A new account is added at Discovered / Not Started, with OwnerName and
///     Notes from the file. An existing one gets its AccountName, Address,
///     UserName and platform updated; its progress (owner, notes, stage) is
///     BlueTrack's and is never overwritten by the file.
///   - An account of this source missing from the file is marked deleted in
///     the source (D-185); one that comes back is restored.
///   - A file with no valid rows changes nothing, so an empty or broken file
///     can't mark every account deleted.
///   - Phase 4b: afterwards, every account of this source that CyberArk also
///     has (same user name and address) is linked to the CyberArk account and
///     moved to Onboarded to Vault (dbo.usp_Link_FeedAccounts). Feeds run after
///     the nightly CyberArk load, so an account onboarded today links tonight.
/// PlatformName is optional; one that matches no CyberArk platform (by name
/// or ID) still imports the account, without a platform, and is reported.
/// </summary>
public sealed class AccountInventoryImportService(
    AccountInventoryRepository repository,
    AuditLogger auditLogger,
    ILogger<AccountInventoryImportService> logger)
{
    public static readonly string[] Columns = ["UserName", "Address", "AccountName", "PlatformName", "OwnerName", "Notes"];

    // dbo.fact_account / fact_account_progress column sizes (01_BlueTrack_Baseline_CoreSchema.sql).
    private static readonly Dictionary<string, int> MaxLengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserName"] = 300, ["Address"] = 300, ["AccountName"] = 500, ["PlatformName"] = 200, ["OwnerName"] = 300, ["Notes"] = 2000
    };

    private const int SourceAccountIdMaxLength = 100;

    /// <summary>fact_account.SourceAccountId for a feed account (D-212).</summary>
    public static string SourceAccountIdFor(string userName, string address) =>
        $"{userName.Trim()}@{address.Trim()}".ToLowerInvariant();

    public async Task<AccountInventoryImportResult> ImportAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int sourceSystemKey, string sourceSystemName, int userKey)
    {
        var existing = (await repository.GetAccountsAsync(sourceSystemKey))
            .ToDictionary(a => a.SourceAccountId, StringComparer.OrdinalIgnoreCase);
        var platforms = (await repository.GetPlatformsAsync())
            .OrderBy(p => p.SourceSystemCode == "PRIVCLOUD" ? 0 : 1)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var creates = new List<AccountInventoryRepository.NewAccount>();
        var updates = new List<AccountInventoryRepository.ChangedAccount>();
        var errors = new List<ImportRowError>();
        var unchanged = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            try
            {
                var row = rows[i];
                string? Cell(string column)
                {
                    var value = row.TryGetValue(column, out var raw) ? raw.Trim() : "";
                    if (value.Length > MaxLengths[column]) throw new InvalidOperationException($"{column} is longer than {MaxLengths[column]} characters.");
                    return value.Length == 0 ? null : value;
                }

                var userName = Cell("UserName") ?? throw new InvalidOperationException("UserName is required.");
                var address = Cell("Address") ?? throw new InvalidOperationException("Address is required.");
                var sourceAccountId = SourceAccountIdFor(userName, address);
                if (sourceAccountId.Length > SourceAccountIdMaxLength)
                    throw new InvalidOperationException($"UserName and Address together are longer than {SourceAccountIdMaxLength - 1} characters, so the account can't be identified.");
                if (!seen.Add(sourceAccountId))
                    throw new InvalidOperationException($"{userName} on {address} appears earlier in the file.");

                var accountName = Cell("AccountName") ?? $"{userName}@{address}";
                var platformName = Cell("PlatformName");
                int? platformKey = null;
                if (platformName is not null)
                {
                    platformKey = platforms.FirstOrDefault(p =>
                        string.Equals(p.PlatformName, platformName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.PlatformID, platformName, StringComparison.OrdinalIgnoreCase))?.PlatformKey;
                    if (platformKey is null)
                        errors.Add(new ImportRowError { RowNumber = rowNumber, Error = $"PlatformName '{platformName}' isn't a CyberArk platform; the account was imported without one." });
                }

                if (existing.TryGetValue(sourceAccountId, out var account))
                {
                    // A blank PlatformName keeps the platform the account already has.
                    var effectivePlatform = platformName is null ? account.PlatformKey : platformKey;
                    var changed = account.AccountName != accountName || account.Address != address
                        || account.UserName != userName || account.PlatformKey != effectivePlatform;
                    if (changed || account.IsDeletedInSource)
                        updates.Add(new(account.AccountKey, accountName, address, userName, effectivePlatform, Restore: account.IsDeletedInSource));
                    else
                        unchanged++;
                }
                else
                {
                    creates.Add(new(sourceAccountId, accountName, address, userName, platformKey, Cell("OwnerName"), Cell("Notes")));
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ImportRowError { RowNumber = rowNumber, Error = Errors.RowErrorText.For(ex, logger) });
            }
        }

        if (seen.Count == 0)
        {
            throw new InvalidOperationException("The file has no valid account rows, so nothing was changed (no accounts were marked deleted).");
        }

        var deletes = existing.Values.Where(a => !a.IsDeletedInSource && !seen.Contains(a.SourceAccountId)).Select(a => a.AccountKey).ToList();
        await repository.ApplyAsync(sourceSystemKey, creates, updates, deletes);
        var (linked, _) = await repository.LinkAsync(sourceSystemKey, userKey);

        var restored = updates.Count(u => u.Restore);
        var result = new AccountInventoryImportResult
        {
            TotalRows = rows.Count,
            CreatedCount = creates.Count,
            UpdatedCount = updates.Count - restored,
            RestoredCount = restored,
            DeletedCount = deletes.Count,
            UnchangedCount = unchanged,
            LinkedCount = linked,
            Errors = errors.OrderBy(e => e.RowNumber).ToList()
        };
        if (creates.Count + updates.Count + deletes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", userKey, "fact_account", entityKey: null,
                detail: $"Account inventory for '{sourceSystemName}': {result.Summary}");
        }
        return result;
    }
}
