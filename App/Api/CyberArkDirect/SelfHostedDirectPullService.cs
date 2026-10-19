using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>How a pull ended. Skipped: Self-Hosted Direct isn't the configured source, so nothing was done.</summary>
public sealed record PullOutcome(string Status, string? Message, PullCounts? Counts)
{
    public static PullOutcome Skipped(string reason) => new("Skipped", reason, null);
}

/// <summary>
/// D-248: one Self-Hosted Direct pull. Logs on to PVWA with the configured credential (fetched at that moment, so
/// a CP or CCP credential is read from the Vault), reads everything, compares the Safe and account counts with the
/// last good pull (<see cref="PullChangeGuard"/>), and only then replaces the stg_sh_* tables, in one transaction.
/// The nightly Import step (usp_Import_Configured) checks the pull's outcome and fails the night when it isn't a
/// recent success; the Load then reads the staging tables as it does after an EVD import.
/// One pull runs at a time.
/// </summary>
public sealed class SelfHostedDirectPullService(
    CyberArkSourceConfigRepository configRepository,
    SelfHostedDirectRepository repository,
    CredentialRepository credentialRepository,
    IHttpClientFactory httpClientFactory,
    ILogger<SelfHostedDirectPullService> logger)
{
    public const string HttpClientName = "PvwaDirect";

    private static readonly SemaphoreSlim OnePullAtATime = new(1, 1);

    /// <summary>Runs one pull and returns how it ended; a failure is recorded in the pull log and returned, not thrown.</summary>
    public async Task<PullOutcome> RunAsync(string triggeredBy, bool acceptChange, CancellationToken ct)
    {
        var config = await configRepository.GetAsync();
        var s = config.Settings;
        if (!config.IsConfigured || !s.ImportSelfHosted || s.SelfHostedSource != "Direct")
            return PullOutcome.Skipped("Self-Hosted Direct isn't the configured Self-Hosted source.");

        if (!await OnePullAtATime.WaitAsync(0, ct)) return new PullOutcome("Failed", "A pull is already running.", null);
        try
        {
            var pullKey = await repository.StartPullAsync(triggeredBy, acceptChange);
            try
            {
                return await PullAsync(pullKey, s, acceptChange, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await repository.CompleteAsync(pullKey, "Failed", null, "The pull was cancelled (the application is stopping).", null);
                throw;
            }
            catch (Exception ex) when (ex is PvwaException or SecretRetrievalException or InvalidOperationException)
            {
                // These messages are written to be shown to an admin and never carry the credential.
                logger.LogWarning(ex, "Self-Hosted Direct pull failed: {Message}", ex.Message);
                await repository.CompleteAsync(pullKey, "Failed", null, ex.Message, null);
                return new PullOutcome("Failed", ex.Message, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Self-Hosted Direct pull failed unexpectedly.");
                var message = $"Unexpected error: {ex.GetType().Name}. See the application log.";
                await repository.CompleteAsync(pullKey, "Failed", null, message, null);
                return new PullOutcome("Failed", message, null);
            }
        }
        finally
        {
            OnePullAtATime.Release();
        }
    }

    private async Task<PullOutcome> PullAsync(long pullKey, Models.CyberArkSourceSettings s, bool acceptChange, CancellationToken ct)
    {
        var (userName, password) = await credentialRepository.ResolveForUseAsync(s.PvwaCredentialKey
            ?? throw new InvalidOperationException("Self-Hosted Direct has no credential set."));
        if (string.IsNullOrEmpty(userName)) throw new InvalidOperationException("The credential has no user name; PVWA needs one to log on.");

        var client = new PvwaClient(httpClientFactory.CreateClient(HttpClientName), PvwaClient.ApiRootFor(s.PvwaBaseUrl!));
        var token = await client.LogonAsync(s.PvwaLogonMethod, userName, password, ct);
        PvwaSnapshot snapshot;
        try
        {
            snapshot = await client.PullAsync(token, ct);
        }
        finally
        {
            await client.LogoffAsync(token, CancellationToken.None);
        }

        if (snapshot.Safes.Count == 0)
            throw new PvwaException("PVWA returned no Safes. The credential may not be a member of any, so nothing was changed.");

        var counts = PullCounts.Of(snapshot);
        var previous = await repository.GetLastSucceededAsync();
        var rejection = PullChangeGuard.Evaluate(
            previous is null ? null : new PullCounts(previous.SafeCount ?? 0, previous.AccountCount ?? 0, previous.UserCount ?? 0,
                previous.GroupCount ?? 0, previous.MemberCount ?? 0, previous.PlatformCount ?? 0),
            counts, s.DirectMaxChangePercent);
        if (rejection is not null && !acceptChange)
        {
            await repository.CompleteAsync(pullKey, "Rejected", counts, rejection, null);
            return new PullOutcome("Rejected", rejection, counts);
        }

        var batchId = Guid.NewGuid();
        var mapper = new SelfHostedStagingMapper(await repository.GetKnownLocationsAsync());
        var host = PvwaClient.ApiRootFor(s.PvwaBaseUrl!).Host;
        var tables = mapper.Map(snapshot, batchId, $"PVWA REST ({host})");
        await repository.StageAsync(tables, ct);

        var notes = new List<string>();
        if (rejection is not null) notes.Add($"An admin accepted this change: {rejection}");
        notes.AddRange(snapshot.Warnings);
        notes.AddRange(tables.Warnings);
        var message = notes.Count == 0 ? null : string.Join(" | ", notes);
        await repository.CompleteAsync(pullKey, "Succeeded", counts, message, batchId);
        return new PullOutcome("Succeeded", message, counts);
    }

    /// <summary>
    /// Test, on unsaved values: logs on and counts what the credential can see, writing nothing. Returns lines for
    /// the page: each check's name, whether it passed, and a message.
    /// </summary>
    public async Task<IReadOnlyList<Models.CyberArkSourceTestCheck>> TestAsync(Models.CyberArkSourceSettings s, CancellationToken ct)
    {
        var checks = new List<Models.CyberArkSourceTestCheck>();
        if (!await configRepository.CredentialExistsAsync(s.PvwaCredentialKey!.Value))
        {
            checks.Add(new Models.CyberArkSourceTestCheck { Item = "Credential", Ok = false, Message = "The credential no longer exists." });
            return checks;
        }

        try
        {
            var (userName, password) = await credentialRepository.ResolveForUseAsync(s.PvwaCredentialKey.Value);
            checks.Add(new Models.CyberArkSourceTestCheck { Item = "Credential", Ok = !string.IsNullOrEmpty(userName), Message = string.IsNullOrEmpty(userName) ? "The credential has no user name." : $"Fetched; user '{userName}'." });
            if (string.IsNullOrEmpty(userName)) return checks;

            var client = new PvwaClient(httpClientFactory.CreateClient(HttpClientName), PvwaClient.ApiRootFor(s.PvwaBaseUrl!));
            var token = await client.LogonAsync(s.PvwaLogonMethod, userName, password, ct);
            checks.Add(new Models.CyberArkSourceTestCheck { Item = "PVWA logon", Ok = true, Message = $"Logged on to {PvwaClient.ApiRootFor(s.PvwaBaseUrl!).Host} with {s.PvwaLogonMethod} authentication." });
            try
            {
                var snapshot = await client.PullAsync(token, ct);
                var counts = PullCounts.Of(snapshot);
                checks.Add(new Models.CyberArkSourceTestCheck
                {
                    Item = "Read",
                    Ok = counts.SafeCount > 0,
                    Message = $"{counts.SafeCount} Safes, {counts.AccountCount} accounts, {counts.MemberCount} Safe members, {counts.UserCount} users, {counts.GroupCount} groups, {counts.PlatformCount} platforms."
                        + (snapshot.Warnings.Count == 0 ? "" : $" {snapshot.Warnings.Count} warning(s): {string.Join(" ", snapshot.Warnings.Take(3))}")
                });
                var previous = await repository.GetLastSucceededAsync();
                if (previous is not null)
                {
                    var rejection = PullChangeGuard.Evaluate(new PullCounts(previous.SafeCount ?? 0, previous.AccountCount ?? 0, 0, 0, 0, 0),
                        counts with { UserCount = 0, GroupCount = 0, MemberCount = 0, PlatformCount = 0 }, s.DirectMaxChangePercent);
                    checks.Add(new Models.CyberArkSourceTestCheck
                    {
                        Item = "Change since the last pull",
                        Ok = rejection is null,
                        Message = rejection is null ? $"Within the allowed {s.DirectMaxChangePercent}%." : "A pull now would be rejected: " + rejection
                    });
                }
            }
            finally
            {
                await client.LogoffAsync(token, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is PvwaException or SecretRetrievalException)
        {
            checks.Add(new Models.CyberArkSourceTestCheck { Item = "PVWA", Ok = false, Message = ex.Message });
        }
        return checks;
    }
}
