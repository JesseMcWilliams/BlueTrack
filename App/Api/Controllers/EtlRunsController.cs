using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-337: Admin > Data Sources, Import and Load: the latest runs of the nightly job's steps, the Load's procedures,
/// and what a run still going is doing in SQL Server. D-348: the stall alert time and Cancel load. All under
/// ManageDataSources like the rest of that page.
/// </summary>
[ApiController]
[Route("api/admin/etl-runs")]
[Authorize(Policy = Permissions.ManageDataSources)]
public sealed class EtlRunsController(
    EtlRunRepository repository,
    RunStallDetector stallDetector,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    TimeProvider timeProvider) : ControllerBase
{
    public const int MinStallMinutes = 5;
    public const int MaxStallMinutes = 1440;

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new
    {
        serverTime = timeProvider.GetUtcNow().UtcDateTime,
        runs = await repository.GetRecentAsync(12),
        running = await repository.GetRunningAsync(),
        stallMinutes = await stallDetector.GetStallMinutesAsync()
    });

    [HttpGet("{etlRunKey:long}/steps")]
    public async Task<IActionResult> Steps(long etlRunKey) => Ok(await repository.GetStepsAsync(etlRunKey));

    /// <summary>D-351: an Import's files and the Self-Hosted Direct pull it checked; 404 for a run that isn't an Import.</summary>
    [HttpGet("{etlRunKey:long}/import")]
    public async Task<IActionResult> Import(long etlRunKey) =>
        await repository.GetImportDetailAsync(etlRunKey) is { } detail ? Ok(detail) : NotFound();

    public sealed class SaveSettingsRequest
    {
        public int StallMinutes { get; init; }
    }

    /// <summary>D-348: how long a pull with no progress, or a Load procedure, Import or Snapshot, may run before it's stalled.</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SaveSettingsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (request.StallMinutes is < MinStallMinutes or > MaxStallMinutes)
            return Problem(title: "Invalid setting", detail: $"Alert after is {MinStallMinutes} to {MaxStallMinutes:N0} minutes.", statusCode: StatusCodes.Status400BadRequest);

        var before = await stallDetector.GetStallMinutesAsync();
        await repository.SaveStallMinutesAsync(request.StallMinutes);
        await auditLogger.LogAsync("ImportLoadSettingsChanged", user.UserKey, "app_config", entityKey: null,
            detail: $"Stall alert time: {before} -> {request.StallMinutes} minutes.");
        return Ok(new { stallMinutes = request.StallMinutes });
    }

    /// <summary>
    /// D-348: stops the Import and Load SQL Agent job and marks the runs it was running Failed. 409 when the Agent
    /// refuses (most often: the job isn't running), with the Agent's message.
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var running = await repository.GetRunningAsync();
        try
        {
            await repository.StopImportLoadJobAsync();
        }
        catch (SqlException ex)
        {
            return Problem(title: "Can't cancel the load", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        var who = user.DisplayName ?? user.ExternalIdentifier;
        var marked = 0;
        try
        {
            marked = await repository.MarkCancelledAsync(
                running.Select(r => r.Activity.EtlRunKey).ToList(), $"Cancelled on Admin > Data Sources by {who}.");
        }
        catch (SqlException)
        {
            // The stopped session still holds the row; Import and Load shows it as ended until it's released.
        }

        var runs = running.Count == 0 ? "no run logged yet"
            : string.Join(", ", running.Select(r => $"{r.Activity.StepName} {r.Activity.EtlRunKey}"
                + (r.CurrentStep is { Status: "Started" } s ? $" at {s.ProcName}" : "")));
        await auditLogger.LogAsync("ImportLoadCancelled", user.UserKey, "etl_run_log", entityKey: null,
            detail: AuditLogger.ClipDetail($"The Import and Load SQL Agent job was stopped ({runs}); {marked} run(s) marked Failed."));
        return Ok(new { marked });
    }
}
