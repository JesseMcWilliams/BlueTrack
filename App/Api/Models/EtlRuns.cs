namespace BlueTrack.Api.Models;

/// <summary>D-337: one Import, Load or Snapshot run (dbo.etl_run_log), as Admin > Data Sources lists them.</summary>
public sealed class EtlRunRow
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
    /// <summary>How many procedures the run logged (dbo.etl_run_step): the Load's; 0 for Import and Snapshot.</summary>
    public int StepCount { get; init; }
    /// <summary>D-351: for an Import, how many export files it imported (dbo.import_log); 0 otherwise.</summary>
    public int FileCount { get; init; }
}

/// <summary>D-351: one file an Import imported (dbo.import_log). Status: Started, Succeeded or Failed.</summary>
public sealed class EtlImportFile
{
    public required string SourceFileName { get; init; }
    public string? SourceSystemName { get; init; }
    public int? RowsLoaded { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>D-351: the Self-Hosted Direct pull an Import checked.</summary>
public sealed class EtlImportPull
{
    public long PullKey { get; init; }
    public DateTime CompletedAt { get; init; }
    public required string Status { get; init; }
}

/// <summary>D-351: what an Import did: its files and the pull it checked.</summary>
public sealed class EtlImportDetail
{
    public IReadOnlyList<EtlImportFile> Files { get; init; } = [];
    public EtlImportPull? Pull { get; init; }
}

/// <summary>D-337: one procedure of a Load (dbo.etl_run_step). Status: Started, Succeeded, RolledBack (it finished, then the Load failed) or Failed.</summary>
public sealed class EtlRunStepRow
{
    public int StepOrder { get; init; }
    public required string ProcName { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// D-337: what the SQL Server session of a run still Started is doing (dbo.usp_Get_EtlSessionActivity). With
/// CanSeeSessions false (the procedure isn't signed: the installer's Db.SessionActivity), only the run is known.
/// SessionFound false: its session has ended, so the run will never finish (it was stopped, or SQL Server restarted).
/// </summary>
public sealed class EtlSessionActivity
{
    public long EtlRunKey { get; init; }
    public required string StepName { get; init; }
    public DateTime StartedAt { get; init; }
    public int? SessionId { get; init; }
    public bool CanSeeSessions { get; init; }
    public bool SessionFound { get; init; }
    public string? RequestStatus { get; init; }
    public string? Command { get; init; }
    public string? WaitType { get; init; }
    public long? WaitMs { get; init; }
    public string? WaitResource { get; init; }
    public string? LastWaitType { get; init; }
    public long? RequestElapsedMs { get; init; }
    public int? BlockingSessionId { get; init; }
    public string? CurrentProcedure { get; init; }
    public string? CurrentStatement { get; init; }
    public string? BlockerLogin { get; init; }
    public string? BlockerHost { get; init; }
    public string? BlockerProgram { get; init; }
    public string? BlockerStatus { get; init; }
    public string? BlockerDatabase { get; init; }
    public int? BlockerOpenTransactions { get; init; }
    public DateTime? BlockerLastRequestStart { get; init; }
    public string? BlockerCommand { get; init; }
    public string? BlockerWaitType { get; init; }
}

/// <summary>D-337: a run still Started: its current procedure (a Load's), how many it has finished, and its session.</summary>
public sealed class EtlRunningRun
{
    public required EtlSessionActivity Activity { get; init; }
    public EtlRunStepRow? CurrentStep { get; init; }
    /// <summary>D-351: for a running Import, the export file it is on.</summary>
    public string? CurrentFile { get; init; }
    public int StepsDone { get; init; }
}
