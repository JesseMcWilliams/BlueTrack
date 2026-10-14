namespace BlueTrack.Api.Data;

/// <summary>
/// D-225: builds a SQL Server LIKE pattern from user-typed text, always
/// passed as a parameter. %, _ and [ are LIKE wildcards in SQL Server, so
/// each is wrapped in brackets ([%], [_], [[]) to match literally -- no
/// ESCAPE clause needed. [ is replaced first so the brackets added for the
/// other two aren't escaped again.
/// </summary>
public static class SqlLike
{
    public static string Escape(string text) =>
        text.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    /// <summary>A "contains" pattern: %text%.</summary>
    public static string Contains(string text) => $"%{Escape(text)}%";

    /// <summary>A "starts with" pattern: text%.</summary>
    public static string StartsWith(string text) => $"{Escape(text)}%";
}
