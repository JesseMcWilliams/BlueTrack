namespace BlueTrack.Api.Security;

/// <summary>
/// D-197: cross-site request forgery defence. Windows sign-in (and the SAML
/// session cookie, which must be SameSite=None) goes with any request the
/// browser makes, including a form another site submits, so a malicious page
/// could otherwise make a signed-in user import a file or change data.
///
/// Every POST/PUT/PATCH/DELETE under /api must carry
/// "X-Requested-With: XMLHttpRequest". A plain HTML form can't set a request
/// header, and a script on another site can't add one without a CORS
/// preflight, which this API never grants (no CORS policy is registered). The
/// SPA's fetch wrapper (App/Web/src/utils/apiBase.js) adds it to every call.
///
/// Exempt: the SAML assertion consumer service, which the identity provider's
/// sign-in page posts to as a normal form (its own signature protects it), and
/// (D-243) the API token endpoint and any request that carries an
/// "Authorization: Bearer" header: a browser attaches ambient credentials
/// (cookies, Windows sign-in) by itself, but never that header, and another
/// site can't add it without a CORS preflight, which is never granted.
/// Paths outside /api (e.g. the OIDC callback, /signin-oidc) are not checked.
/// </summary>
public sealed class RequireApiRequestHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "XMLHttpRequest";

    private static readonly PathString ApiPrefix = new("/api");
    private static readonly PathString[] Exempt = [new("/api/auth/saml/acs"), new("/api/auth/token")];

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresHeader(context.Request) && !HasHeader(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Missing request header",
                status = StatusCodes.Status403Forbidden,
                detail = $"Requests that change data must send the {HeaderName}: {HeaderValue} header.",
            });
            return;
        }

        await next(context);
    }

    private static bool RequiresHeader(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && request.Path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase)
        && !Exempt.Any(path => request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        && !ApiAccess.ApiTokenAuthenticationMiddleware.HasBearerToken(request);

    private static bool HasHeader(HttpRequest request) =>
        string.Equals(request.Headers[HeaderName], HeaderValue, StringComparison.OrdinalIgnoreCase);
}
