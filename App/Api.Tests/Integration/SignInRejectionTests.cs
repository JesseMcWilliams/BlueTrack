using BlueTrack.Api.Auth;
using BlueTrack.Api.Tests.Contract;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>D-285: a rejected SAML or OIDC sign-in is audit-logged as LogonFailed, and its alert is limited to one per 15 minutes.</summary>
public class SignInRejectionTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public SignInRejectionTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ARejection_IsAuditLoggedAsLogonFailed_ByTheSignInSystemUser()
    {
        var reason = $"test rejection {Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SignInRejectionRecorder>().RecordAsync("SAML", reason, "192.0.2.7");
        }

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        try
        {
            var logged = await connection.QuerySingleAsync<(string User, string EntityKey, string Detail)>("""
                SELECT u.DisplayName, e.EntityKey, e.Detail FROM web.audit_event e
                JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey AND t.EventTypeName = 'LogonFailed'
                JOIN web.app_user u ON u.UserKey = e.PerformedByUserKey
                WHERE e.Detail LIKE '%' + @reason
                """, new { reason });
            Assert.Equal(("BlueTrack sign-in (system)", "SAML", $"SAML sign-in rejected: {reason}"), logged);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.audit_event WHERE Detail LIKE '%' + @reason", new { reason });
        }
    }

    [Fact]
    public void TheAlert_IsSentAtMostOncePerWindow_AndTheNextOneCountsTheRest()
    {
        var alerter = new SignInRejectedAlerter(_factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignInRejectedAlerter>.Instance);
        var start = new DateTime(2026, 10, 21, 2, 0, 0, DateTimeKind.Utc);

        Assert.Equal((true, 0), alerter.Claim(start));
        Assert.Equal((false, 0), alerter.Claim(start.AddMinutes(1)));
        Assert.Equal((false, 0), alerter.Claim(start.AddMinutes(14)));
        Assert.Equal((true, 2), alerter.Claim(start.AddMinutes(15)));

        var body = SignInRejectedAlerter.BuildBody("OIDC", "access_denied", null, start, 2, "Admin > Audit Log");
        Assert.Contains("Why: access_denied", body);
        Assert.Contains("2 more rejection(s) since the last email", body);
        Assert.Contains("event LogonFailed", body);
    }
}
