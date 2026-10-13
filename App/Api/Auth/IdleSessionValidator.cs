using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Auth;

/// <summary>
/// D-209: Global Application Configuration's Idle Timeout signs people out.
/// Before this it only set how long cached rights lived. Two halves:
///   - the SPA signs out after that many minutes without activity in any of
///     its tabs (useIdleSignOut), for every sign-in method;
///   - this, on the cookie-based sessions (OIDC/DevFakeAuth "Cookies", SAML
///     "saml2"), so a session whose tab was closed also ends: every request
///     records the time in the ticket (rewritten at most once a minute), and a
///     request arriving more than Idle Timeout after the last one is refused
///     and the cookie removed.
/// Windows Integrated has no session to end on the server -- the browser signs
/// in again on each request -- so for it only the SPA half applies.
/// </summary>
public static class IdleSessionValidator
{
    public const string LastActivityKey = "bt.lastActivity";
    private static readonly TimeSpan RecordEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ConfigCacheFor = TimeSpan.FromSeconds(60);

    private static (int Minutes, DateTimeOffset ReadAt)? cachedIdle;

    public static void Attach(CookieAuthenticationOptions options)
    {
        var previous = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            await previous(context);
            if (context.Principal is not null) await ValidateAsync(context);
        };
    }

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var idle = TimeSpan.FromMinutes(await IdleMinutesAsync(services, now));

        var last = context.Properties.Items.TryGetValue(LastActivityKey, out var stored) && long.TryParse(stored, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : context.Properties.IssuedUtc ?? now;

        if (now - last > idle)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }
        if (now - last >= RecordEvery || !context.Properties.Items.ContainsKey(LastActivityKey))
        {
            context.Properties.Items[LastActivityKey] = now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.ShouldRenew = true;
        }
    }

    /// <summary>The configured Idle Timeout, read at most once a minute per process.</summary>
    private static async Task<int> IdleMinutesAsync(IServiceProvider services, DateTimeOffset now)
    {
        if (cachedIdle is { } cached && now - cached.ReadAt < ConfigCacheFor) return cached.Minutes;
        var minutes = Math.Max(1, (await services.GetRequiredService<AppConfigRepository>().GetAsync()).IdleTimeoutMinutes);
        cachedIdle = (minutes, now);
        return minutes;
    }

    /// <summary>Tests change the setting and need the next request to see it.</summary>
    public static void ForgetCachedSetting() => cachedIdle = null;
}
