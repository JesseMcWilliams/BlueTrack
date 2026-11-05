using System.Globalization;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-348: what has stopped moving. A Self-Hosted Direct pull still Started whose last progress (its steps, saved as it
/// reads, D-337) is older than web.app_config.RunStallMinutes; a Load procedure, or an Import or Snapshot, still
/// running after that long; or a run whose SQL Server session has ended without finishing it. Used by the RunStalled
/// email and the Deployment page's "Pull and Load" health check.
/// </summary>
public sealed class RunStallDetector(IDbConnectionFactory connectionFactory, EtlRunRepository etlRunRepository)
{
    public async Task<int> GetStallMinutesAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int?>("SELECT TOP (1) RunStallMinutes FROM web.app_config") ?? 30;
    }

    public async Task<RunStallReport> DetectAsync()
    {
        var stalls = new List<RunStall>();
        int minutes;
        DateTime now;
        using (var connection = connectionFactory.Create())
        {
            // The runs' times are SQL Server's (SYSUTCDATETIME), so "now" is too.
            (minutes, now) = await connection.QuerySingleAsync<(int, DateTime)>(
                "SELECT COALESCE((SELECT TOP (1) RunStallMinutes FROM web.app_config), 30), SYSUTCDATETIME()");
            var cutoff = now.AddMinutes(-minutes);
            var pulls = await connection.QueryAsync<(long PullKey, DateTime StartedAt, DateTime? LastProgressAt, string? StepName)>("""
                SELECT p.PullKey, p.StartedAt, p.LastProgressAt,
                       (SELECT TOP (1) s.StepName FROM dbo.cyberark_direct_pull_step s
                        WHERE s.PullKey = p.PullKey ORDER BY s.StepOrder DESC) AS StepName
                FROM dbo.cyberark_direct_pull p
                WHERE p.Status = N'Started' AND p.StartedAt >= DATEADD(DAY, -1, SYSUTCDATETIME())
                """);
            foreach (var p in pulls)
            {
                var last = p.LastProgressAt ?? p.StartedAt;
                if (last >= cutoff) continue;
                stalls.Add(new RunStall
                {
                    Kind = "Pull",
                    DedupKey = "pull:" + p.PullKey.ToString(CultureInfo.InvariantCulture),
                    What = "The Self-Hosted Direct pull " + p.PullKey.ToString(CultureInfo.InvariantCulture)
                        + (p.StepName is null ? "" : $" (at \"{p.StepName}\")"),
                    Since = last,
                    Minutes = (int)(now - last).TotalMinutes,
                    Detail = p.LastProgressAt is null ? "It has read nothing since it started." : "It has read nothing more since then."
                });
            }
        }

        var runCutoff = now.AddMinutes(-minutes);
        foreach (var run in await etlRunRepository.GetRunningAsync())
        {
            var a = run.Activity;
            var key = a.EtlRunKey.ToString(CultureInfo.InvariantCulture);
            if (a.CanSeeSessions && !a.SessionFound)
            {
                stalls.Add(new RunStall
                {
                    Kind = a.StepName, DedupKey = $"run:{key}:ended", What = $"The {a.StepName} (run {key})", Since = a.StartedAt,
                    Minutes = (int)(now - a.StartedAt).TotalMinutes,
                    Detail = "Its SQL Server session has ended, so it will never finish: it was stopped, or SQL Server restarted."
                });
                continue;
            }

            var step = run.CurrentStep is { Status: "Started" } ? run.CurrentStep : null;
            var since = step?.StartedAt ?? a.StartedAt;
            if (since >= runCutoff) continue;
            stalls.Add(new RunStall
            {
                Kind = a.StepName,
                DedupKey = step is null ? $"run:{key}" : $"run:{key}:step:{step.StepOrder.ToString(CultureInfo.InvariantCulture)}",
                What = step is null ? $"The {a.StepName} (run {key})" : $"The Load's {step.ProcName} (run {key}, procedure {step.StepOrder})",
                Since = since,
                Minutes = (int)(now - since).TotalMinutes,
                Detail = Describe(a)
            });
        }

        return new RunStallReport(minutes, stalls);
    }

    /// <summary>What the run's session is waiting on, in a sentence; empty when the procedure that reads it isn't signed.</summary>
    public static string Describe(EtlSessionActivity a)
    {
        if (!a.CanSeeSessions) return "What its SQL Server session is doing can't be read: run the installer's Db.SessionActivity step as a sysadmin.";
        var parts = new List<string>();
        if (a.BlockingSessionId is not null)
            parts.Add($"It is blocked by session {a.BlockingSessionId}"
                + (a.BlockerLogin is null ? "" : $" ({a.BlockerLogin}{(a.BlockerHost is null ? "" : " on " + a.BlockerHost)}{(a.BlockerProgram is null ? "" : ", " + a.BlockerProgram)})") + ".");
        if (a.WaitType is not null)
            parts.Add($"It is waiting on {a.WaitType}{(a.WaitMs is null ? "" : $" for {a.WaitMs / 1000} s")}{(a.WaitResource is null ? "" : $" ({a.WaitResource})")}.");
        else if (a.RequestStatus is not null)
            parts.Add($"Its request is {a.RequestStatus}{(a.Command is null ? "" : $" ({a.Command})")}.");
        if (a.CurrentProcedure is not null) parts.Add($"Running {a.CurrentProcedure}.");
        return parts.Count == 0 ? "Its session is idle between statements." : string.Join(" ", parts);
    }
}

public sealed record RunStallReport(int StallMinutes, IReadOnlyList<RunStall> Stalls);

public sealed class RunStall
{
    /// <summary>Pull, Import, Load or Snapshot.</summary>
    public required string Kind { get; init; }
    public required string DedupKey { get; init; }
    public required string What { get; init; }
    public DateTime Since { get; init; }
    public int Minutes { get; init; }
    public required string Detail { get; init; }
}
