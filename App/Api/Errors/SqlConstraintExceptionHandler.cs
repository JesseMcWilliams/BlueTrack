using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Errors;

/// <summary>
/// D-194: turns a database constraint violation that reaches the top of a
/// request into the status that describes it, instead of a 500:
///   - unique key / unique index (2627, 2601): 409 Conflict -- "already exists";
///   - a DELETE refused by a foreign key (547, REFERENCE): 409 Conflict --
///     other records still use it;
///   - an INSERT/UPDATE whose foreign key points at nothing (547, FOREIGN KEY):
///     400 -- the request names something that doesn't exist;
///   - a CHECK constraint (547, CHECK): 400 -- an invalid value.
/// Endpoints that can explain a conflict better (e.g. Access Groups' duplicate
/// check) still do so themselves; this is the safety net for everything else.
/// The body is ProblemDetails with the explanation in both "detail" and
/// "message", since pages read one or the other.
/// </summary>
public sealed class SqlConstraintExceptionHandler(ILogger<SqlConstraintExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var sql = exception as SqlException ?? exception.InnerException as SqlException;
        if (sql is null || Classify(sql) is not var (status, title, detail)) return false;

        logger.LogInformation("Constraint violation on {Method} {Path} answered with {Status}: {Message}",
            httpContext.Request.Method, httpContext.Request.Path, status, sql.Message);
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["message"] = detail;
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>The status, title and explanation for a constraint error; null for any other SQL error (left as a 500).</summary>
    public static (int Status, string Title, string Detail)? Classify(SqlException sql)
    {
        switch (sql.Number)
        {
            case 2627:
            case 2601:
                return (StatusCodes.Status409Conflict, "Already exists",
                    "Something with the same name or identifier already exists, so this can't be saved.");
            case 547 when sql.Message.Contains("REFERENCE constraint", StringComparison.OrdinalIgnoreCase):
                return (StatusCodes.Status409Conflict, "Still in use",
                    "Other records still use it, so it can't be deleted. Remove or reassign them first.");
            case 547 when sql.Message.Contains("FOREIGN KEY constraint", StringComparison.OrdinalIgnoreCase):
                return (StatusCodes.Status400BadRequest, "Unknown reference",
                    "The request refers to something that doesn't exist.");
            case 547 when sql.Message.Contains("CHECK constraint", StringComparison.OrdinalIgnoreCase):
                return (StatusCodes.Status400BadRequest, "Invalid value",
                    "A value isn't allowed (it breaks one of the database's rules for this record).");
            default:
                return null;
        }
    }
}
