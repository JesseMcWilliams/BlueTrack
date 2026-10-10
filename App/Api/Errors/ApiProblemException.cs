namespace BlueTrack.Api.Errors;

/// <summary>
/// D-194: thrown from a repository (or anywhere below a controller) when a
/// request can't be carried out for a reason the caller should see -- the
/// record is missing (404), the request is invalid (400), or the record's
/// current state conflicts with it (409). ApiProblemExceptionHandler turns it
/// into a ProblemDetails response with the explanation in both "detail" and
/// "message". Use the factory methods so the status always matches the reason.
/// </summary>
public sealed class ApiProblemException : Exception
{
    private ApiProblemException(int status, string title, string detail) : base(detail)
    {
        Status = status;
        Title = title;
    }

    public int Status { get; }
    public string Title { get; }

    public static ApiProblemException NotFound(string detail) => new(StatusCodes.Status404NotFound, "Not found", detail);
    public static ApiProblemException BadRequest(string detail) => new(StatusCodes.Status400BadRequest, "Invalid request", detail);
    public static ApiProblemException Conflict(string detail) => new(StatusCodes.Status409Conflict, "Conflict", detail);
}
