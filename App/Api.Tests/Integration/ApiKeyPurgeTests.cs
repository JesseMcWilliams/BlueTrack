using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Tests.Contract;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-243: API keys that expired more than the retention setting ago are deleted (ApiKeyPurgeService), each in the
/// Audit Log; a key still inside the period, a key that hasn't expired and a retention of 0 are left alone.
/// </summary>
public class ApiKeyPurgeTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ApiKeyPurgeTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private ApiKeyPurgeService NewService() =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiKeyPurgeService>.Instance);

    [Fact]
    public async Task ExpiredKeys_AreDeletedAfterTheRetentionPeriod_AndAuditedBySystem()
    {
        var keys = new ApiKeyRepository(new TestDbConnectionFactory());
        var creator = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var id = Guid.NewGuid().ToString("N")[..8];
        var created = new List<int>();

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var roleKey = await connection.QuerySingleAsync<int>("SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Viewer'");
        var originalRetention = await connection.QuerySingleAsync<int>("SELECT ApiKeyExpiredRetentionDays FROM web.app_config");
        async Task<int> AddKeyAsync(string label, int expiredDaysAgo, bool enabled = true)
        {
            var (_, prefix, hash) = ApiKeyGenerator.Create();
            var key = await keys.CreateAsync($"PurgeTest {label} {id}", prefix, hash, roleKey, DateTime.UtcNow.AddYears(1), creator);
            created.Add(key);
            await connection.ExecuteAsync("UPDATE web.api_key SET ExpiresAt = DATEADD(DAY, -@days, SYSUTCDATETIME()), IsEnabled = @enabled WHERE ApiKeyKey = @key",
                new { days = expiredDaysAgo, enabled, key });
            return key;
        }
        async Task<bool> ExistsAsync(int key) => await connection.QuerySingleAsync<bool>("SELECT CASE WHEN EXISTS (SELECT 1 FROM web.api_key WHERE ApiKeyKey = @key) THEN 1 ELSE 0 END", new { key });

        try
        {
            var old = await AddKeyAsync("old", 400);
            var oldDisabled = await AddKeyAsync("old disabled", 500, enabled: false);
            var recent = await AddKeyAsync("recent", 100);
            var live = await AddKeyAsync("live", -30);   // expires in 30 days

            // A retention of 0 keeps everything.
            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyExpiredRetentionDays = 0");
            Assert.Equal(0, await NewService().RunOnceAsync(CancellationToken.None));
            Assert.True(await ExistsAsync(old));

            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyExpiredRetentionDays = 365");
            Assert.Equal(2, await NewService().RunOnceAsync(CancellationToken.None));
            Assert.False(await ExistsAsync(old));
            Assert.False(await ExistsAsync(oldDisabled));
            Assert.True(await ExistsAsync(recent));
            Assert.True(await ExistsAsync(live));
            Assert.Equal(0, await NewService().RunOnceAsync(CancellationToken.None));   // nothing left to do

            // A shorter period brings the recent one in.
            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyExpiredRetentionDays = 30");
            Assert.Equal(1, await NewService().RunOnceAsync(CancellationToken.None));
            Assert.False(await ExistsAsync(recent));
            Assert.True(await ExistsAsync(live));

            var audited = (await connection.QueryAsync<(string Detail, string User)>("""
                SELECT TOP (10) e.Detail, u.DisplayName
                FROM web.audit_event e
                JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                JOIN web.app_user u ON u.UserKey = e.PerformedByUserKey
                WHERE t.EventTypeName = 'ApiKeyChanged' AND e.Detail LIKE '%deleted automatically%' ORDER BY e.AuditEventKey DESC
                """)).ToList();
            Assert.Contains(audited, a => a.Detail.Contains($"PurgeTest old {id}") && a.Detail.Contains("more than 365 days ago") && a.User == "BlueTrack API access (system)");
            Assert.Contains(audited, a => a.Detail.Contains($"PurgeTest recent {id}") && a.Detail.Contains("more than 30 days ago"));
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyExpiredRetentionDays = @originalRetention", new { originalRetention });
            if (created.Count > 0) await connection.ExecuteAsync("DELETE FROM web.api_key WHERE ApiKeyKey IN @created", new { created });
        }
    }

    [Fact]
    public async Task Retention_IsASetting_ADayCountOrZero()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        var original = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        Assert.Equal(365, original["apiKeyExpiredRetentionDays"]!.GetValue<int>());

        JsonObject With(int days)
        {
            var body = JsonNode.Parse(original.ToJsonString())!.AsObject();
            body["apiKeyExpiredRetentionDays"] = days;
            return body;
        }

        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/configuration", With(-1))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/configuration", With(3651))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/configuration", With(0))).StatusCode);
            Assert.Equal(0, (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["apiKeyExpiredRetentionDays"]!.GetValue<int>());
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/configuration", With(90))).StatusCode);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/configuration", With(365))).StatusCode);
        }
    }
}
