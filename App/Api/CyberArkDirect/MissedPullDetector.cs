using Dapper;
using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-363: whether the latest nightly Self-Hosted Direct pull time passed with no pull starting, and whether the API
/// (whose scheduler starts the pull) was running then. Until now a pull that never started left no trace: only the
/// Import failing half an hour later ("pull more than 20 hours old"). Used by the Pull and Load health check and the
/// SelfHostedPullMissed notification. Two small queries: the pull log by its StartedAt index, and web.api_run (a row
/// per API start). The pull time is taken as set now: a time changed since last night is checked from tonight.
/// </summary>
public sealed class MissedPullDetector(IDbConnectionFactory connectionFactory, CyberArkSourceConfigRepository configRepository, TimeProvider timeProvider)
{
    /// <summary>How long after the pull time to wait for the pull to start (the scheduler wakes every minute).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public enum ApiState { Running, NotRunning, Unknown }

    /// <param name="ScheduledAtLocal">The pull time that passed, server local time.</param>
    /// <param name="ScheduledAtUtc">The same moment in UTC.</param>
    /// <param name="Api">Whether the API was running at that moment: Unknown when web.api_run has no row from before it (just upgraded).</param>
    /// <param name="ApiStartedAfterUtc">When the API next started after the pull time, if it has.</param>
    public sealed record MissedPull(DateTime ScheduledAtLocal, DateTime ScheduledAtUtc, ApiState Api, DateTime? ApiStartedAfterUtc)
    {
        /// <summary>One or two sentences for the health check and the email.</summary>
        public string Describe(TimeZoneInfo zone)
        {
            var first = $"No Self-Hosted Direct pull started at {ScheduledAtLocal:yyyy-MM-dd HH:mm} (server time), so the next Import will fail.";
            var started = ApiStartedAfterUtc is { } at ? TimeZoneInfo.ConvertTimeFromUtc(at, zone).ToString("yyyy-MM-dd HH:mm") : null;
            return Api switch
            {
                ApiState.NotRunning when started is not null => $"{first} The API wasn't running then; it started again at {started}. See API starts on Admin > Deployment.",
                ApiState.NotRunning => $"{first} The API wasn't running then. See API starts on Admin > Deployment.",
                ApiState.Running => $"{first} The API was running, but its pull scheduler didn't start the pull; look for BlueTrack errors in the Windows Application event log.",
                _ => $"{first} See API starts on Admin > Deployment.",
            };
        }
    }

    /// <summary>The latest pull time that passed (more than <see cref="Grace"/> ago) with no pull since; null if none, or Self-Hosted Direct is off.</summary>
    public async Task<MissedPull?> DetectAsync()
    {
        var settings = (await configRepository.GetAsync()).Settings;
        if (!SelfHostedDirectPullService.IsOn(settings) || !FeedSchedule.TryParseTime(settings.DirectPullTime, out var pullTime)) return null;
        return await DetectAsync(pullTime, timeProvider.GetLocalNow().DateTime, timeProvider.LocalTimeZone);
    }

    /// <summary>The check for a given pull time, local now and zone. Public for tests.</summary>
    public async Task<MissedPull?> DetectAsync(TimeOnly pullTime, DateTime nowLocal, TimeZoneInfo zone)
    {
        var scheduledLocal = LatestPassed(pullTime, nowLocal);
        // A time skipped by a daylight-saving change: the scheduler fired at the first wake after it.
        var forUtc = zone.IsInvalidTime(scheduledLocal) ? scheduledLocal.AddHours(1) : scheduledLocal;
        var scheduledUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(forUtc, DateTimeKind.Unspecified), zone);

        using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleAsync<(bool Pulled, bool AnyRunBefore, bool Running, DateTime? StartedAfter)>("""
            SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.cyberark_direct_pull WHERE StartedAt >= DATEADD(MINUTE, -2, @scheduledUtc)) THEN 1 ELSE 0 END AS BIT),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM web.api_run WHERE StartedAt <= @scheduledUtc) THEN 1 ELSE 0 END AS BIT),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM web.api_run WHERE StartedAt <= @scheduledUtc
                                          AND (StoppedAt >= @scheduledUtc OR PullSchedulerCheckedAt >= @scheduledUtc)) THEN 1 ELSE 0 END AS BIT),
                   (SELECT MIN(StartedAt) FROM web.api_run WHERE StartedAt > @scheduledUtc)
            """, new { scheduledUtc });
        if (row.Pulled) return null;
        var api = row.Running ? ApiState.Running : row.AnyRunBefore ? ApiState.NotRunning : ApiState.Unknown;
        return new MissedPull(scheduledLocal, scheduledUtc, api, row.StartedAfter);
    }

    /// <summary>The latest moment at pull time that is at least <see cref="Grace"/> before now.</summary>
    public static DateTime LatestPassed(TimeOnly pullTime, DateTime nowLocal)
    {
        var candidate = nowLocal.Date + pullTime.ToTimeSpan();
        return candidate <= nowLocal - Grace ? candidate : candidate.AddDays(-1);
    }
}
