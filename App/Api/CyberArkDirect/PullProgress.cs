using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>D-337: one step of a pull as recorded (dbo.cyberark_direct_pull_step). ItemsDone and ItemsTotal are null for a step that isn't counted.</summary>
public sealed record PullStepRecord(int StepOrder, string StepName, DateTime StartedAt, DateTime? CompletedAt, int? ItemsDone, int? ItemsTotal);

/// <summary>
/// D-337: where a running pull is. The pull service and <see cref="PvwaClient"/> call <see cref="Begin"/> as each step
/// starts and <see cref="Advance"/> as items are read (from parallel calls too, so it locks); the service saves
/// <see cref="Snapshot"/> every few seconds. A pull that stops leaves its last step without an end, which is the step
/// it was in.
/// </summary>
public sealed class PullProgress(TimeProvider timeProvider)
{
    /// <summary>The step names, as stored; the page translates them, and <see cref="DisplayName"/> is used in the pull's message.</summary>
    public static class Steps
    {
        public const string Credential = "Credential";
        public const string Logon = "Logon";
        public const string Platforms = "Platforms";
        public const string Safes = "Safes";
        public const string BuiltInSafes = "BuiltInSafes";
        public const string Accounts = "Accounts";
        public const string CpmActivity = "CpmActivity";
        public const string AccountDetails = "AccountDetails";
        public const string SafeMembers = "SafeMembers";
        public const string Users = "Users";
        public const string Groups = "Groups";
        public const string Applications = "Applications";
        public const string Logoff = "Logoff";
        public const string ChangeCheck = "ChangeCheck";
        public const string AdGroups = "AdGroups";
        public const string Staging = "Staging";
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [Steps.Credential] = "Fetching the credential",
        [Steps.Logon] = "Logging on to PVWA",
        [Steps.Platforms] = "Platforms",
        [Steps.Safes] = "Safes",
        [Steps.BuiltInSafes] = "CyberArk's built-in Safes",
        [Steps.Accounts] = "Accounts",
        [Steps.CpmActivity] = "CPM activity",
        [Steps.AccountDetails] = "Account details",
        [Steps.SafeMembers] = "Safe members",
        [Steps.Users] = "Users",
        [Steps.Groups] = "Groups",
        [Steps.Applications] = "CyberArk Applications",
        [Steps.Logoff] = "Logging off PVWA",
        [Steps.ChangeCheck] = "Comparing with the last pull",
        [Steps.AdGroups] = "AD members of the Safe groups",
        [Steps.Staging] = "Saving to staging"
    };

    public static string DisplayName(string step) => Names.TryGetValue(step, out var name) ? name : step;

    private readonly object _gate = new();
    private readonly List<Step> _steps = [];
    private long _version;

    private sealed class Step
    {
        public required string Name { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public int? Done { get; set; }
        public int? Total { get; set; }
    }

    /// <summary>Ends the step in progress and starts <paramref name="step"/>; with a <paramref name="total"/>, it counts items from 0.</summary>
    public void Begin(string step, int? total = null)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            EndCurrent(now);
            _steps.Add(new Step { Name = step, StartedAt = now, Done = total is null ? null : 0, Total = total });
            _version++;
        }
    }

    /// <summary>The number of items the step in progress will read, once it's known (a list's first page, or the list).</summary>
    public void SetTotal(int total)
    {
        lock (_gate)
        {
            if (_steps.Count == 0) return;
            var current = _steps[^1];
            current.Total = total;
            current.Done ??= 0;
            _version++;
        }
    }

    public void Advance(int count = 1)
    {
        lock (_gate)
        {
            if (_steps.Count == 0) return;
            var current = _steps[^1];
            current.Done = (current.Done ?? 0) + count;
            _version++;
        }
    }

    /// <summary>Ends the step in progress: the pull finished. A pull that stopped isn't completed, so its last step stays open.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            EndCurrent(timeProvider.GetUtcNow().UtcDateTime);
            _version++;
        }
    }

    private void EndCurrent(DateTime now)
    {
        if (_steps.Count > 0 && _steps[^1].CompletedAt is null) _steps[^1].CompletedAt = now;
    }

    /// <summary>The steps so far, and a number that changes whenever they do (so an unchanged pull isn't saved again).</summary>
    public (long Version, IReadOnlyList<PullStepRecord> Steps) Snapshot()
    {
        lock (_gate)
        {
            return (_version, _steps.Select((s, i) => new PullStepRecord(i + 1, s.Name, s.StartedAt, s.CompletedAt, s.Done, s.Total)).ToList());
        }
    }

    /// <summary>For a stopped pull's message: " It stopped during: Account details (1,200 of 2,454)." Empty before any step.</summary>
    public string StoppedNote()
    {
        lock (_gate)
        {
            if (_steps.Count == 0 || _steps[^1].CompletedAt is not null) return "";
            var current = _steps[^1];
            var count = current switch
            {
                { Done: { } done, Total: { } total } => $" ({ServerTextCulture.Count(done)} of {ServerTextCulture.Count(total)})",
                { Done: { } done } => $" ({ServerTextCulture.Count(done)} read)",
                _ => ""
            };
            return $" It stopped during: {DisplayName(current.Name)}{count}.";
        }
    }
}
