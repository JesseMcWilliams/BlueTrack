namespace BlueTrack.Api.Models;

/// <summary>
/// One (stage, status) cell in the Stage/Status Funnel Summary rollup (D-56).
/// </summary>
public sealed class StageStatusFunnelRow
{
    public int StageOrder { get; init; }
    public required string StageName { get; init; }
    public required string StatusName { get; init; }
    public int AccountCount { get; init; }
}
