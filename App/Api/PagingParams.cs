namespace BlueTrack.Api;

/// <summary>
/// D-124 Phase 3: shared page/pageSize normalization for the six paginated
/// list endpoints (Targets, Access Groups, Account Progress, Risk
/// Exceptions, Audit Log, Risk Score report). Caps pageSize server-side --
/// a real guard, not decoration, matching this codebase's existing
/// SQL-injection-guard rigor elsewhere (the SortableColumns whitelist
/// pattern in each repository) -- so a malicious/buggy client can't force
/// an unbounded query by requesting an absurd pageSize. Page is floored at
/// 1 (a caller-supplied 0 or negative page would otherwise produce a
/// negative OFFSET, which SQL Server rejects outright).
/// </summary>
public static class PagingParams
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var normalizedPage = page is > 0 ? page.Value : 1;
        var normalizedPageSize = pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize;
        return (normalizedPage, normalizedPageSize);
    }

    public static int Offset(int page, int pageSize) => (page - 1) * pageSize;
}
