namespace BlueTrack.Api.Models;

/// <summary>
/// D-314: GET /api/admin/power-bi -- what someone building a Power BI report on BlueTrack's data needs: where to
/// connect, the objects meant for it (with their columns as the database has them now), and how they relate.
/// </summary>
public sealed class PowerBiAccess
{
    public required string ServerName { get; init; }
    public required string DatabaseName { get; init; }
    /// <summary>D-321: whether the BlueTrack_PowerBI_Reader role exists (the installer's Db.PowerBiAccess step creates it).</summary>
    public bool ReaderRoleExists { get; init; }
    public required IReadOnlyList<PowerBiObject> Objects { get; init; }
    public required IReadOnlyList<PowerBiRelationship> Relationships { get; init; }
}

/// <summary>A view or table to import, with what one row is.</summary>
public sealed class PowerBiObject
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }   // "Fact" or "Dimension"
    public required string Description { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary>A many-to-one relationship to set in Power BI's model: FromObject.FromColumn -> ToObject.ToColumn.</summary>
public sealed record PowerBiRelationship(string FromObject, string FromColumn, string ToObject, string ToColumn);
