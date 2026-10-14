namespace BlueTrack.Api.Notifications;

/// <summary>A page of the SPA: its name as people see it in the menus, and its route (App/Web/src/router/index.js).</summary>
public sealed record SitePage(string Name, string Path);

/// <summary>
/// D-224: web.app_config.SiteUrl, the address people open BlueTrack at
/// (the site root, where the SPA is served; the API is under /BlueTrack),
/// and the pages the emails link to under it.
/// </summary>
public static class SiteLinks
{
    public const int MaxLength = 500;

    public static readonly SitePage OverdueReviews = new("Risk Exceptions > Overdue Reviews", "/exceptions/overdue");
    public static readonly SitePage CyberArkSources = new("Admin > Data Sources > CyberArk Sources", "/admin/data-sources/cyberark");
    public static readonly SitePage KpiSummary = new("Reports > KPI Summary", "/reports/kpi-summary");
    public static readonly SitePage Trends = new("Reports > Trends", "/reports/trends");
    public static readonly SitePage OverdueAtRisk = new("Reports > Overdue / At-Risk", "/reports/overdue");
    public static readonly SitePage AuditLog = new("Admin > Audit Log", "/admin/audit-log");
    public static readonly SitePage Notifications = new("Admin > Notifications", "/admin/notifications");

    /// <summary>
    /// Checks a Site URL as entered and returns it in the stored form, or an
    /// error. Blank means "not set" (null). It must be an absolute https URL
    /// with no user name, query or fragment; http is allowed only for
    /// localhost (a developer's own machine), since the emails would
    /// otherwise send people to a page over an unencrypted connection. The
    /// stored form drops the trailing "/" and a default port, and lower-cases
    /// the host.
    /// </summary>
    public static bool TryNormalize(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0) return true;

        if (trimmed.Length > MaxLength)
        {
            error = $"The Site URL can be at most {MaxLength} characters.";
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "The Site URL must be a full address starting with https://, e.g. https://bluetrack.company.com.";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "The Site URL must use https://. http:// is allowed only for localhost.";
            return false;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "The Site URL can't hold a user name, a query (?...) or a fragment (#...): use just the address people open BlueTrack at.";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>The page's full address, or null when no usable Site URL is set.</summary>
    public static string? Link(string? siteUrl, SitePage page)
    {
        // A value written straight into the table is checked here too.
        if (!TryNormalize(siteUrl, out var normalized, out _) || normalized is null) return null;
        return new Uri(normalized + page.Path).AbsoluteUri;
    }

    /// <summary>"Admin > Audit Log (https://...)", or just the page name when no Site URL is set. For plain text.</summary>
    public static string Describe(string? siteUrl, SitePage page) =>
        Link(siteUrl, page) is { } link ? $"{page.Name} ({link})" : page.Name;
}
