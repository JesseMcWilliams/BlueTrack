using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-288: the load's CpmFailureSince and PSM flag, Reports > CPM Health and its export, and "newly failing".</summary>
public class CpmHealthTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public CpmHealthTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AFailingAccount_IsOnTheReport_AndNewlyFailing_UntilItRecovers()
    {
        var token = $"cpm{Guid.NewGuid():N}"[..12];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var accountKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted, CPMStatus, AutoManaged, LastCPMAction, LastCPMActionResult)
            OUTPUT inserted.AccountKey VALUES (2, @token, @token, 0, ' Failure ', 1, 'Change', 'Failure');
            """, new { token });
        try
        {
            await connection.ExecuteAsync("EXEC dbo.usp_Load_CpmHealth");
            var since = await connection.QuerySingleAsync<DateTime?>("SELECT CpmFailureSince FROM fact_account WHERE AccountKey = @accountKey", new { accountKey });
            Assert.NotNull(since);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
            var row = Assert.Single((await client.GetFromJsonAsync<List<CpmHealthRow>>("/api/reports/cpm-health"))!, r => r.AccountKey == accountKey);
            Assert.Equal(("Change", "Failure"), (row.LastCPMAction, row.LastCPMActionResult));
            var csv = ReportExportTests.ParseCsv(await (await client.GetAsync("/api/reports/cpm-health/export?format=csv")).Content.ReadAsByteArrayAsync());
            Assert.Equal(["Account", "Username", "Address", "Safe", "Platform", "Source"], csv[0].Take(6));
            Assert.Contains(csv, r => r[0] == token && r[7] == "Change (Failure)");

            Assert.Contains(await new CpmHealthRepository(new TestDbConnectionFactory()).GetNewlyFailingAsync(), r => r.AccountKey == accountKey);

            // A second load keeps the time; recovering clears it and the account leaves the report.
            await connection.ExecuteAsync("EXEC dbo.usp_Load_CpmHealth");
            Assert.Equal(since, await connection.QuerySingleAsync<DateTime?>("SELECT CpmFailureSince FROM fact_account WHERE AccountKey = @accountKey", new { accountKey }));
            await connection.ExecuteAsync("UPDATE fact_account SET CPMStatus = 'success' WHERE AccountKey = @accountKey; EXEC dbo.usp_Load_CpmHealth;", new { accountKey });
            Assert.Null(await connection.QuerySingleAsync<DateTime?>("SELECT CpmFailureSince FROM fact_account WHERE AccountKey = @accountKey", new { accountKey }));
            Assert.DoesNotContain((await client.GetFromJsonAsync<List<CpmHealthRow>>("/api/reports/cpm-health"))!, r => r.AccountKey == accountKey);
            Assert.Equal(HttpStatusCode.Forbidden, (await ForbiddenClient().GetAsync("/api/reports/cpm-health")).StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @accountKey", new { accountKey });
        }
    }

    private HttpClient ForbiddenClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.DoesNotExist");
        return client;
    }
}
