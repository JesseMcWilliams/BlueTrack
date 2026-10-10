using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Data;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-185: delete and undelete accounts in BlueTrack, with reasons, against
/// the seeded synthetic accounts. Each test leaves its accounts undeleted.
/// </summary>
public class AccountDeletionTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountDeletionTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record Skipped(long AccountKey, string? AccountName, string Reason);
    private sealed record Result(int Requested, int Changed, List<Skipped> Skipped);
    private sealed record HistoryEntry(string Action, string Reason, string? PerformedByName, DateTime PerformedAt, string? BatchId);

    private static async Task ForceUndeleteAsync(long accountKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.account_deletion WHERE AccountKey = @accountKey;
            UPDATE dbo.fact_account SET IsDeleted = 0, IsDeletedInSource = 0 WHERE AccountKey = @accountKey;
            """, new { accountKey });
    }

    [Fact]
    public async Task DeleteThenUndelete_TracksReasons_AndHidesTheAccountUntilShown()
    {
        var keys = new[] { await TestAccounts.GetAccountKeyAsync("TestAccount01"), await TestAccounts.GetAccountKeyAsync("TestAccount02") };
        var lockRepository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        foreach (var key in keys) await lockRepository.ForceReleaseAsync(key);
        var client = Client("TestUser.Admin");
        var reason = $"Contract test {Guid.NewGuid():N}"[..30];

        try
        {
            var deleted = await (await client.PostAsJsonAsync("/api/account-progress/delete", new { accountKeys = keys, reason }))
                .Content.ReadFromJsonAsync<Result>();
            Assert.Equal(2, deleted!.Changed);

            // Hidden by default; shown with deleted=Only, with who and why.
            var visible = await client.GetFromJsonAsync<JsonArray>("/api/account-progress?search=TestAccount0&pageSize=100");
            Assert.DoesNotContain(visible!, a => keys.Contains(a!["accountKey"]!.GetValue<long>()));
            var only = await client.GetFromJsonAsync<JsonArray>("/api/account-progress?search=TestAccount0&pageSize=100&deleted=Only");
            var row = only!.Single(a => a!["accountKey"]!.GetValue<long>() == keys[0])!;
            Assert.True(row["isDeleted"]!.GetValue<bool>());
            Assert.Equal(reason, row["deletionReason"]!.GetValue<string>());
            Assert.NotNull(row["deletedByName"]);

            // Deleting again is skipped, not an error.
            var again = await (await client.PostAsJsonAsync("/api/account-progress/delete", new { accountKeys = new[] { keys[0] }, reason }))
                .Content.ReadFromJsonAsync<Result>();
            Assert.Contains("Already deleted", Assert.Single(again!.Skipped).Reason);

            // Undelete needs a reason too.
            var noReason = await client.PostAsJsonAsync("/api/account-progress/undelete", new { accountKeys = keys, reason = " " });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

            var undeleted = await (await client.PostAsJsonAsync("/api/account-progress/undelete", new { accountKeys = keys, reason = "Deleted by mistake" }))
                .Content.ReadFromJsonAsync<Result>();
            Assert.Equal(2, undeleted!.Changed);
            var after = await client.GetFromJsonAsync<JsonArray>("/api/account-progress?search=TestAccount0&pageSize=100");
            Assert.Contains(after!, a => a!["accountKey"]!.GetValue<long>() == keys[0]);

            var history = await client.GetFromJsonAsync<List<HistoryEntry>>($"/api/account-progress/{keys[0]}/deletion-history");
            Assert.Equal("Undelete", history![0].Action);
            Assert.Equal("Deleted by mistake", history[0].Reason);
            Assert.Equal("Delete", history[1].Action);
            Assert.Equal(reason, history[1].Reason);
            Assert.NotNull(history[1].BatchId); // two accounts at once share a batch
        }
        finally
        {
            foreach (var key in keys) await ForceUndeleteAsync(key);
        }
    }

    [Fact]
    public async Task Undelete_RefusesAnAccountOnlyCyberArkDeleted()
    {
        var key = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("UPDATE dbo.fact_account SET IsDeleted = 1, IsDeletedInSource = 1 WHERE AccountKey = @key", new { key });
        }
        try
        {
            var result = await (await Client("TestUser.Admin").PostAsJsonAsync("/api/account-progress/undelete", new { accountKeys = new[] { key }, reason = "Try" }))
                .Content.ReadFromJsonAsync<Result>();
            Assert.Equal(0, result!.Changed);
            Assert.Contains("Deleted in CyberArk", Assert.Single(result.Skipped).Reason);
        }
        finally
        {
            await ForceUndeleteAsync(key);
        }
    }

    [Fact]
    public async Task Delete_RequiresAReason_AndTheDeleteAccountsPermission()
    {
        var key = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client("TestUser.Admin").PostAsJsonAsync("/api/account-progress/delete", new { accountKeys = new[] { key }, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("TestUser.Analyst").PostAsJsonAsync("/api/account-progress/delete", new { accountKeys = new[] { key }, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client("TestUser.Analyst").PostAsJsonAsync("/api/account-progress/undelete", new { accountKeys = new[] { key }, reason = "x" })).StatusCode);
    }
}
