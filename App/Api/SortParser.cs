namespace BlueTrack.Api;

/// <summary>
/// Shared parser for D-42's multi-column sort query param, e.g.
/// sort=stageName:asc,ownerName:desc. Used by any controller with a
/// GetList endpoint (AccountProgressController, RiskExceptionsController)
/// -- what's actually safe to sort by is validated downstream in each
/// repository's own column whitelist, not here.
/// </summary>
public static class SortParser
{
    public static IReadOnlyList<(string Field, bool Descending)> Parse(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return [];
        }

        return sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var pieces = part.Split(':', 2);
                var field = pieces[0];
                var descending = pieces.Length > 1 && pieces[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
                return (Field: field, Descending: descending);
            })
            .ToList();
    }
}
