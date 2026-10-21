using System.Data;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-232: manual resolutions of Unresolved Entitlement Members
/// (web.entitlement_member_resolution), applied by the nightly load before the
/// ID and name matches, and at once by dbo.usp_Apply_EntitlementMemberResolution.
/// D-279: every call names the member's source (PRIVCLOUD or SELFHOSTED), and
/// a member links only to a user or group of that same source.
/// </summary>
public sealed class EntitlementMemberResolutionRepository(IDbConnectionFactory connectionFactory)
{
    public const int SearchResultLimit = 20;

    public const string PrivilegeCloud = "PRIVCLOUD";
    public const string SelfHosted = "SELFHOSTED";

    private const string SourceKeySql = "(SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = @sourceCode)";

    /// <summary>How a member currently stands: the name, how it resolved and to whom. Null when no current row has that member ID.</summary>
    public sealed record MemberState(string? MemberName, string? ResolutionMethod, string? ResolvedToName);

    public async Task<MemberState?> GetMemberAsync(string sourceCode, string memberType, string sourceMemberId, IDbConnection? connection = null, IDbTransaction? transaction = null)
    {
        using var owned = connection is null ? connectionFactory.Create() : null;
        var conn = connection ?? owned!;
        return await conn.QueryFirstOrDefaultAsync<MemberState>($"""
            SELECT TOP (1) v.MemberName, v.ResolutionMethod, v.ResolvedToName
            FROM dbo.vw_unresolved_entitlement_members v
            WHERE v.SourceSystemKey = {SourceKeySql} AND v.MemberType = @memberType AND v.UnresolvedMemberId = @sourceMemberId
            ORDER BY v.EntitlementKey
            """, new { sourceCode, memberType, sourceMemberId }, transaction);
    }

    /// <summary>The name of a user (memberType User) or group (Group) of the source, or null when the source has none with that key.</summary>
    public async Task<string?> GetPrincipalNameAsync(string sourceCode, string memberType, int key)
    {
        using var connection = connectionFactory.Create();
        var sql = memberType == "User"
            ? $"SELECT Username FROM dbo.dim_user WHERE UserKey = @key AND SourceSystemKey = {SourceKeySql}"
            : $"SELECT GroupName FROM dbo.dim_group WHERE GroupKey = @key AND SourceSystemKey = {SourceKeySql}";
        return await connection.QuerySingleOrDefaultAsync<string>(sql, new { sourceCode, key });
    }

    /// <summary>
    /// Saves (or replaces) the member's manual resolution and applies it to its
    /// current rows. Returns the member before and after, or null when no current
    /// row has that member ID.
    /// </summary>
    public async Task<(MemberState Before, MemberState After)?> ResolveAsync(
        string sourceCode, string memberType, string sourceMemberId, string resolution, int? userKey, int? groupKey, string? note, int resolvedBy)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var before = await GetMemberAsync(sourceCode, memberType, sourceMemberId, connection, transaction);
        if (before is null)
        {
            return null;
        }

        await connection.ExecuteAsync($"""
            DECLARE @src INT = {SourceKeySql};
            MERGE web.entitlement_member_resolution AS tgt
            USING (SELECT @src AS SourceSystemKey, @memberType AS MemberType, @sourceMemberId AS SourceMemberId) AS s
                ON tgt.SourceSystemKey = s.SourceSystemKey AND tgt.MemberType = s.MemberType AND tgt.SourceMemberId = s.SourceMemberId
            WHEN MATCHED THEN
                UPDATE SET MemberName = @memberName, Resolution = @resolution, UserKey = @userKey, GroupKey = @groupKey,
                           Note = @note, ResolvedBy = @resolvedBy, ResolvedDate = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (SourceSystemKey, MemberType, SourceMemberId, MemberName, Resolution, UserKey, GroupKey, Note, ResolvedBy)
                VALUES (@src, @memberType, @sourceMemberId, @memberName, @resolution, @userKey, @groupKey, @note, @resolvedBy);
            EXEC dbo.usp_Apply_EntitlementMemberResolution @SourceSystemKey = @src, @MemberType = @memberType, @SourceMemberId = @sourceMemberId;
            """, new { sourceCode, memberType, sourceMemberId, memberName = before.MemberName, resolution, userKey, groupKey, note, resolvedBy }, transaction);

        var after = (await GetMemberAsync(sourceCode, memberType, sourceMemberId, connection, transaction))!;
        transaction.Commit();
        return (before, after);
    }

    /// <summary>
    /// Removes the member's manual resolution, so it resolves by ID or name again
    /// (or stays unresolved). Returns the member before and after, or null when
    /// there was no manual resolution to remove.
    /// </summary>
    public async Task<(MemberState Before, MemberState After)?> UnresolveAsync(string sourceCode, string memberType, string sourceMemberId)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var before = await GetMemberAsync(sourceCode, memberType, sourceMemberId, connection, transaction);
        var deleted = await connection.ExecuteAsync($"""
            DELETE FROM web.entitlement_member_resolution
            WHERE SourceSystemKey = {SourceKeySql} AND MemberType = @memberType AND SourceMemberId = @sourceMemberId
            """, new { sourceCode, memberType, sourceMemberId }, transaction);
        if (deleted == 0)
        {
            return null;
        }

        await connection.ExecuteAsync($"""
            DECLARE @src INT = {SourceKeySql};
            EXEC dbo.usp_Apply_EntitlementMemberResolution @SourceSystemKey = @src, @MemberType = @memberType, @SourceMemberId = @sourceMemberId;
            """, new { sourceCode, memberType, sourceMemberId }, transaction);

        var after = await GetMemberAsync(sourceCode, memberType, sourceMemberId, connection, transaction);
        transaction.Commit();
        var none = new MemberState(null, null, null);
        return (before ?? none, after ?? none);
    }

    /// <summary>
    /// The source's users (memberType User: user name, e-mail, first or last
    /// name) or groups (Group: group name) containing the text, names starting
    /// with it first, at most <see cref="SearchResultLimit"/>.
    /// </summary>
    public async Task<IReadOnlyList<EntitlementPrincipal>> SearchAsync(string sourceCode, string memberType, string text)
    {
        using var connection = connectionFactory.Create();
        var sql = memberType == "User"
            ? $"""
                SELECT TOP (@Limit) u.UserKey AS [Key], u.Username AS Name, u.Email AS Detail
                FROM dbo.dim_user u
                WHERE u.SourceSystemKey = {SourceKeySql}
                  AND (u.Username LIKE @Contains OR u.Email LIKE @Contains OR u.FirstName LIKE @Contains OR u.LastName LIKE @Contains)
                ORDER BY CASE WHEN u.Username LIKE @StartsWith THEN 0 ELSE 1 END, u.Username, u.UserKey
                """
            : $"""
                SELECT TOP (@Limit) g.GroupKey AS [Key], g.GroupName AS Name, g.Description AS Detail
                FROM dbo.dim_group g
                WHERE g.SourceSystemKey = {SourceKeySql} AND g.GroupName LIKE @Contains
                ORDER BY CASE WHEN g.GroupName LIKE @StartsWith THEN 0 ELSE 1 END, g.GroupName, g.GroupKey
                """;
        return (await connection.QueryAsync<EntitlementPrincipal>(sql, new
        {
            sourceCode,
            Limit = SearchResultLimit,
            Contains = SqlLike.Contains(text),
            StartsWith = SqlLike.StartsWith(text)
        })).ToList();
    }
}
