using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Errors;

/// <summary>
/// D-197: the text shown for one failed row of a CSV import or bulk action.
/// The app's own validation messages are shown as they are; a SQL Server
/// error is not, since its message names tables, columns and constraints
/// (and reached the Analyst role through the imports). A known constraint
/// violation gets SqlConstraintExceptionHandler's wording, any other
/// database error a generic sentence -- the full error goes to the log.
/// </summary>
public static class RowErrorText
{
    public static string For(Exception exception, ILogger? logger = null)
    {
        var sql = exception as SqlException ?? exception.InnerException as SqlException;
        if (sql is null)
        {
            return exception.Message;
        }

        logger?.LogWarning(sql, "A row could not be saved: {Message}", sql.Message);
        return SqlConstraintExceptionHandler.Classify(sql) is { } classified
            ? classified.Detail
            : "This row could not be saved because of a database error. The details are in the application log.";
    }
}
