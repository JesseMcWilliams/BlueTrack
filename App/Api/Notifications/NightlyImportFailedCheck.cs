using System.Globalization;
using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: an Import or Load run (dbo.etl_run_log, written by
/// usp_Import_Configured and usp_RunFullLoad since script 19) that failed,
/// or that started more than StuckAfter ago and never finished (a stopped
/// job or a killed session leaves the row at Started). One email per run:
/// the DedupKey is the run. The SQL Agent job can't call the API, so this
/// is how BlueTrack hears about it: within one check interval (5 minutes).
/// A failed file import's own row in dbo.import_log names the file.
/// Runs older than Lookback are ignored, so an outage of the mail server
/// longer than that drops the alert rather than sending a stale one.
/// </summary>
public sealed class NightlyImportFailedCheck(IDbConnectionFactory connectionFactory, TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "NightlyImportFailed";
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(4);
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(28);

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public static string DedupKeyFor(long etlRunKey) => "run:" + etlRunKey.ToString(CultureInfo.InvariantCulture);

    private sealed class RunRow
    {
        public long EtlRunKey { get; init; }
        public required string StepName { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public required string Status { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ErrorProcedure { get; init; }
        public int? ErrorLine { get; init; }
        public string? StartedBy { get; init; }
        public string? ProgramName { get; init; }
    }

    private sealed class FileRow
    {
        public long EtlRunKey { get; init; }
        public required string SourceFileName { get; init; }
        public string? SourceSystemName { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        using var connection = connectionFactory.Create();
        // Windows are computed on SQL Server's clock, the one etl_run_log is written with.
        const string sql = """
            DECLARE @runs TABLE (EtlRunKey BIGINT PRIMARY KEY, StartedAt DATETIME2 NOT NULL, EndedAt DATETIME2 NOT NULL, StepName NVARCHAR(20) NOT NULL);

            INSERT INTO @runs (EtlRunKey, StartedAt, EndedAt, StepName)
            SELECT r.EtlRunKey, r.StartedAt, COALESCE(r.CompletedAt, SYSUTCDATETIME()), r.StepName
            FROM dbo.etl_run_log r
            WHERE r.StartedAt >= DATEADD(MINUTE, -@LookbackMinutes, SYSUTCDATETIME())
              AND (r.Status = 'Failed'
                   OR (r.Status = 'Started' AND r.StartedAt < DATEADD(MINUTE, -@StuckMinutes, SYSUTCDATETIME())))
              AND NOT EXISTS (
                  SELECT 1 FROM web.notification_log nl
                  JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
                  WHERE nt.NotificationTypeName = @TypeName
                    AND nl.DedupKey = CONCAT('run:', r.EtlRunKey));

            SELECT r.EtlRunKey, r.StepName, r.StartedAt, r.CompletedAt, r.Status, r.ErrorMessage, r.ErrorProcedure, r.ErrorLine,
                   r.StartedBy, r.ProgramName
            FROM dbo.etl_run_log r
            JOIN @runs x ON x.EtlRunKey = r.EtlRunKey
            ORDER BY r.EtlRunKey;

            SELECT x.EtlRunKey, il.SourceFileName, ss.SourceSystemName, il.ErrorMessage
            FROM @runs x
            JOIN dbo.import_log il ON il.StartedAt >= x.StartedAt AND il.StartedAt <= x.EndedAt
            LEFT JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = il.SourceSystemKey
            WHERE x.StepName = 'Import' AND il.Status = 'Failed'
            ORDER BY il.StartedAt;
            """;
        using var grid = await connection.QueryMultipleAsync(sql, new
        {
            TypeName,
            LookbackMinutes = (int)Lookback.TotalMinutes,
            StuckMinutes = (int)StuckAfter.TotalMinutes
        });
        var runs = (await grid.ReadAsync<RunRow>()).AsList();
        var files = (await grid.ReadAsync<FileRow>()).AsList();

        return runs.Select(run => Build(run, files.Where(f => f.EtlRunKey == run.EtlRunKey).ToList())).ToList();
    }

    private NotificationCheckResult Build(RunRow run, IReadOnlyList<FileRow> failedFiles)
    {
        var unfinished = run.Status == "Started";
        var what = unfinished ? $"{run.StepName} has not finished" : $"{run.StepName} failed";
        var email = new NotificationEmail($"BlueTrack: the {run.StepName} step of the Import and Load {(unfinished ? "has not finished" : "failed")}");

        var facts = new List<(string, string)>
        {
            ("Step", run.StepName == "Import" ? "Import (usp_Import_Configured)" : "Load (usp_RunFullLoad)"),
            ("Started (server time)", NotificationEmail.ServerTime(run.StartedAt, timeProvider)),
            (unfinished ? "Finished" : "Failed (server time)",
                unfinished ? $"not after {StuckAfter.TotalHours:0} hours" : NotificationEmail.ServerTime(run.CompletedAt ?? run.StartedAt, timeProvider)),
        };
        if (!unfinished)
        {
            facts.Add(("Error", run.ErrorMessage ?? "(no message recorded)"));
            if (run.ErrorProcedure is not null)
            {
                facts.Add(("Where", run.ErrorLine is null ? run.ErrorProcedure : $"{run.ErrorProcedure}, line {run.ErrorLine.Value.ToString(CultureInfo.InvariantCulture)}"));
            }
        }
        facts.Add(("Started by", string.Join(" / ", new[] { run.StartedBy, run.ProgramName }.Where(v => !string.IsNullOrWhiteSpace(v)))));
        facts.Add(("Run", run.EtlRunKey.ToString(CultureInfo.InvariantCulture)));
        email.Facts(facts);

        if (failedFiles.Count > 0)
        {
            email.Heading("Files that failed to import")
                 .Table(["File", "Source", "Error"], failedFiles.Select(f => (IReadOnlyList<string>)[f.SourceFileName, f.SourceSystemName ?? "", f.ErrorMessage ?? ""]));
        }

        email.Paragraph(unfinished
            ? "The run may have been stopped or may still be running. Check the SQL Server Agent job \"BlueTrack (<database>) - Import and Load\" and its history."
            : run.StepName == "Import"
                ? "When the nightly job's Import step fails, its Load step doesn't run, so BlueTrack keeps showing the previous load's data."
                : "The Load runs in one transaction and was rolled back, so BlueTrack keeps showing the previous load's data.");
        email.SeePage("Admin > Data Sources to check the CyberArk source settings");

        var detail = $"{what}: run {run.EtlRunKey}" + (run.ErrorMessage is null ? "" : $": {run.ErrorMessage}");
        return new NotificationCheckResult(
            Subject: $"BlueTrack: {what}",
            Body: email.Text,
            Detail: detail,
            HtmlBody: email.Html,
            DedupKey: DedupKeyFor(run.EtlRunKey));
    }
}
