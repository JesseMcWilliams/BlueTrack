using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BlueTrack.Api.Errors;

/// <summary>D-194: answers an ApiProblemException with its status and a ProblemDetails body ("detail" and "message" both carry the explanation).</summary>
public sealed class ApiProblemExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ApiProblemException problemException) return false;

        var problem = new ProblemDetails { Status = problemException.Status, Title = problemException.Title, Detail = problemException.Message };
        problem.Extensions["message"] = problemException.Message;
        httpContext.Response.StatusCode = problemException.Status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken);
        return true;
    }
}
