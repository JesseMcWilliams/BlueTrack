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
/// D-271: with the EVD as the source and the shadow pull on, a pull is a shadow pull instead: no change guard, only the
/// stg_shd_* tables are replaced, and they are then compared with what the EVD import staged (ShadowComparisonRepository).
/// One pull runs at a time. D-273: a pull still running after DirectPullTimeLimitMinutes is stopped and logged as failed.
/// D-275: so is one an admin cancels (<paramref name="cancelRequested"/> on RunAsync).
/// </summary>
public sealed class SelfHostedDirectPullService(
    CyberArkSourceConfigRepository configRepository,
    SelfHostedDirectRepository repository,
    ShadowComparisonRepository comparisonRepository,
    CredentialRepository credentialRepository,
    SecretsStoreRepository secretsStoreRepository,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<SelfHostedDirectPullService> logger)
{
    public const string HttpClientName = "PvwaDirect";

    private static readonly SemaphoreSlim OnePullAtATime = new(1, 1);

    /// <summary>Which kind of pull the saved settings call for: "Direct", "Shadow" (D-271), or null for none.</summary>
    public static string? ModeFor(Models.CyberArkSourceSettings s) =>
        !s.ImportSelfHosted ? null : s.SelfHostedSource == "Direct" ? "Direct" : s.ShadowPullEnabled ? "Shadow" : null;

    /// <summary>
    /// Runs one pull and returns how it ended; a failure is recorded in the pull log and returned, not thrown.
    /// <paramref name="ct"/> is the application stopping (rethrown); <paramref name="cancelRequested"/> an admin's Cancel (D-275).
    /// </summary>
    public async Task<PullOutcome> RunAsync(string triggeredBy, bool acceptChange, CancellationToken ct, CancellationToken cancelRequested = default)
    {
        var config = await configRepository.GetAsync();
        var s = config.Settings;
        var mode = config.IsConfigured ? ModeFor(s) : null;
        if (mode is null)
            return PullOutcome.Skipped("Self-Hosted Direct isn't the configured Self-Hosted source, and the shadow pull is off.");
        var shadow = mode == "Shadow";

        if (!await OnePullAtATime.WaitAsync(0, ct)) return new PullOutcome("Failed", "A pull is already running.", null);
        try
        {
            var pullKey = await repository.StartPullAsync(triggeredBy, acceptChange && !shadow, shadow);
            using var timeLimit = new CancellationTokenSource(TimeSpan.FromMinutes(s.DirectPullTimeLimitMinutes), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeLimit.Token, cancelRequested);
            try
            {
                return shadow ? await ShadowPullAsync(pullKey, s, linked.Token) : await PullAsync(pullKey, s, acceptChange, linked.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await repository.CompleteAsync(pullKey, "Failed", null, "The pull was cancelled (the application is stopping).", null);
                throw;
            }
            catch (OperationCanceledException) when (cancelRequested.IsCancellationRequested)
            {
                // D-275. Staging is replaced in one transaction, as for the time limit below.
                var message = "The pull was cancelled on Data Sources. "
                    + (shadow ? "The import is unaffected; no comparison was stored." : "The staged data wasn't changed.");
                logger.LogInformation("Self-Hosted Direct pull cancelled.");
                await repository.CompleteAsync(pullKey, "Failed", null, message, null);
                return new PullOutcome("Failed", message, null);
            }
            catch (OperationCanceledException) when (timeLimit.IsCancellationRequested)
            {
                // Staging is replaced in one transaction, so a stopped pull leaves what the Load reads as it was.
                var message = $"The pull was stopped after the {s.DirectPullTimeLimitMinutes}-minute time limit (Edit CyberArk sources). "
                    + (shadow ? "The import is unaffected; no comparison was stored." : "The staged data wasn't changed.");
                logger.LogWarning("Self-Hosted Direct pull stopped: {Message}", message);
                await repository.CompleteAsync(pullKey, "Failed", null, message, null);
                return new PullOutcome("Failed", message, null);
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

    /// <summary>Logs on, reads everything and logs off. Refuses a read with no Safes, which would empty staging.</summary>
    private async Task<PvwaSnapshot> ReadPvwaAsync(Models.CyberArkSourceSettings s, CancellationToken ct)
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
        return snapshot;
    }

    private async Task<PullOutcome> PullAsync(long pullKey, Models.CyberArkSourceSettings s, bool acceptChange, CancellationToken ct)
    {
        var snapshot = await ReadPvwaAsync(s, ct);
        var counts = PullCounts.Of(snapshot);
        var (baseline, baselineLabel) = await BaselineAsync();
        var rejection = PullChangeGuard.Evaluate(baseline, counts, s.DirectMaxChangePercent);
        if (rejection is not null && baselineLabel is not null) rejection = $"{baselineLabel}: {rejection}";
        IReadOnlyList<string> whichSafes = [];
        if (rejection is not null)
        {
            whichSafes = SafeDifference.Describe(await repository.GetStagedSafesAsync(), snapshot.Safes.Select(x => (x.SafeNumber, x.SafeName)));
        }
        if (rejection is not null && !acceptChange)
        {
            var rejected = string.Join(" | ", new[] { rejection }.Concat(whichSafes));
            await repository.CompleteAsync(pullKey, "Rejected", counts, rejected, null, baseline);
            return new PullOutcome("Rejected", rejected, counts);
        }

        var batchId = Guid.NewGuid();
        var mapper = new SelfHostedStagingMapper();
        var host = PvwaClient.ApiRootFor(s.PvwaBaseUrl!).Host;
        var tables = mapper.Map(snapshot, batchId, $"PVWA REST ({host})");
        await repository.StageAsync(tables, ct);

        var notes = new List<string>();
        if (rejection is not null)
        {
            notes.Add($"An admin accepted this change: {rejection}");
            notes.AddRange(whichSafes);
        }
        notes.AddRange(snapshot.Warnings);
        notes.AddRange(tables.Warnings);
        var message = notes.Count == 0 ? null : string.Join(" | ", notes);
        await repository.CompleteAsync(pullKey, "Succeeded", counts, message, batchId, baseline);
        return new PullOutcome("Succeeded", message, counts);
    }

    /// <summary>
    /// What a real pull's counts are compared with, and a label when it isn't the last pull: the last real pull that
    /// Succeeded; for the first one, D-277, the counts the EVD import staged (internal Safes, deleted and dependent
    /// accounts left out); with neither, the last shadow pull (D-271); with none at all, nothing.
    /// </summary>
    private async Task<(PullCounts? Counts, string? Label)> BaselineAsync()
    {
        var lastReal = await repository.GetLastSucceededAsync(includeShadow: false);
        if (lastReal is not null) return (Counts(lastReal), null);
        if (await repository.GetEvdBaselineAsync() is { } evd)
            return (evd.Counts, $"First Direct pull, compared with what the EVD import staged at {evd.StagedAt:yyyy-MM-dd HH:mm} UTC");
        var lastShadow = await repository.GetLastSucceededAsync();
        return lastShadow is null ? (null, null) : (Counts(lastShadow), "First Direct pull, compared with the last shadow pull");

        static PullCounts Counts(DirectPullRecord r) => new(r.SafeCount ?? 0, r.AccountCount ?? 0, r.UserCount ?? 0, r.GroupCount ?? 0, r.MemberCount ?? 0, r.PlatformCount ?? 0);
    }

    /// <summary>
    /// D-271: reads PVWA, replaces only the stg_shd_* tables and compares them with the EVD-staged stg_sh_* tables. No
    /// change guard: nothing the Load reads changes, and the comparison is what an admin looks at.
    /// </summary>
    private async Task<PullOutcome> ShadowPullAsync(long pullKey, Models.CyberArkSourceSettings s, CancellationToken ct)
    {
        var snapshot = await ReadPvwaAsync(s, ct);
        var counts = PullCounts.Of(snapshot);
        var batchId = Guid.NewGuid();
        var mapper = new SelfHostedStagingMapper();
        var tables = mapper.Map(snapshot, batchId, $"PVWA REST ({PvwaClient.ApiRootFor(s.PvwaBaseUrl!).Host})");
        await repository.StageAsync(tables, ct, directOnly: true);
        await comparisonRepository.CompareAsync(pullKey, ct);

        var notes = snapshot.Warnings.Concat(tables.Warnings).ToList();
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

        var backendType = (await credentialRepository.GetAllAsync()).FirstOrDefault(c => c.CredentialKey == s.PvwaCredentialKey.Value)?.BackendType;
        if (backendType is not null)
        {
            var (ready, note) = await CredentialBackendCheck.CheckAsync(secretsStoreRepository, backendType);
            if (ready is not null)
            {
                checks.Add(new Models.CyberArkSourceTestCheck { Item = $"{backendType} backend", Ok = ready.Value, Message = ready.Value ? "Set up." : note! });
                if (ready == false) return checks;   // the credential can't be fetched until it is; the logon below would only repeat that
            }
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
                var (baseline, baselineLabel) = await BaselineAsync();
                if (baseline is not null)
                {
                    var rejection = PullChangeGuard.Evaluate(baseline with { UserCount = 0, GroupCount = 0, MemberCount = 0, PlatformCount = 0 },
                        counts with { UserCount = 0, GroupCount = 0, MemberCount = 0, PlatformCount = 0 }, s.DirectMaxChangePercent);
                    checks.Add(new Models.CyberArkSourceTestCheck
                    {
                        Item = "Change since the last pull",
                        Ok = rejection is null,
                        Message = (baselineLabel is null ? "" : baselineLabel + ". ")
                            + (rejection is null ? $"Within the allowed {s.DirectMaxChangePercent}%." : "A pull now would be rejected: " + rejection)
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
