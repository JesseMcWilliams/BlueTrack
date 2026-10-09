using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-182: Account Progress bulk edit over real HTTP, against the seeded
/// synthetic accounts. Each test puts the accounts it changed back the way
/// they were (directly through the repository, so the restore can't be
/// affected by the code under test).
/// </summary>
public class AccountProgressBulkEditTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountProgressBulkEditTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static AccountProgressRepository Repository() => new(new TestDbConnectionFactory());

    private static async Task RestoreAsync(AccountProgressDetail before) =>
        await Repository().UpdateAsync(before.AccountKey, new SaveAccountProgressRequest
        {
            CurrentStageKey = before.CurrentStageKey, CurrentStatusKey = before.CurrentStatusKey, RiskLevelKey = before.RiskLevelKey,
            AccountTypeKey = before.AccountTypeKey, SORKey = before.SORKey, OwnerName = before.OwnerName, BusinessUnit = before.BusinessUnit,
            TargetRemediationDate = before.TargetRemediationDate, ActualCompletionDate = before.ActualCompletionDate, Notes = before.Notes
        }, before.ExceptionKey);

    private static async Task<int> CountBulkEditEventsAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            WHERE t.EventTypeName = 'BulkEdit'
            """);
    }

    private sealed record Skipped(long AccountKey, string? AccountName, string Reason);
    private sealed record BulkResult(int Requested, int Updated, int Unchanged, List<Skipped> Skipped);
    private sealed record KeysResult(int MatchingCount, int MaxAccounts, List<long> AccountKeys);

    [Fact]
    public async Task BulkEdit_ChangesOnlyTheChosenFields_AppendsNotes_AndLogsASummary()
    {
        var keys = new[] { await TestAccounts.GetAccountKeyAsync("TestAccount01"), await TestAccounts.GetAccountKeyAsync("TestAccount02") };
        var before = new List<AccountProgressDetail>();
        foreach (var key in keys) before.Add((await Repository().GetDetailAsync(key))!);
        var bulkEventsBefore = await CountBulkEditEventsAsync();
        var unit = $"BulkTest-{Guid.NewGuid():N}"[..20];

        try
        {
            var response = await Client("TestUser.Analyst").PostAsJsonAsync("/api/account-progress/bulk-edit", new
            {
                accountKeys = keys, fields = new[] { "BusinessUnit", "Notes" }, businessUnit = unit, notes = "Bulk note", notesMode = "Append"
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(2, result!.Requested);
            Assert.Equal(2, result.Updated);
            Assert.Empty(result.Skipped);

            foreach (var original in before)
            {
                var after = (await Repository().GetDetailAsync(original.AccountKey))!;
                Assert.Equal(unit, after.BusinessUnit);
                Assert.EndsWith("Bulk note", after.Notes);
                if (!string.IsNullOrWhiteSpace(original.Notes)) Assert.StartsWith(original.Notes.TrimEnd(), after.Notes);
                Assert.Equal(original.OwnerName, after.OwnerName);
                Assert.Equal(original.CurrentStageKey, after.CurrentStageKey);
                Assert.Equal(original.CurrentStatusKey, after.CurrentStatusKey);
            }
            Assert.Equal(bulkEventsBefore + 1, await CountBulkEditEventsAsync());

            // The same edit again changes nothing more for BusinessUnit alone.
            var again = await (await Client("TestUser.Analyst").PostAsJsonAsync("/api/account-progress/bulk-edit", new
            {
                accountKeys = keys, fields = new[] { "BusinessUnit" }, businessUnit = unit
            })).Content.ReadFromJsonAsync<BulkResult>();
            Assert.Equal(2, again!.Unchanged);
        }
        finally
        {
            foreach (var original in before) await RestoreAsync(original);
        }
    }

    [Fact]
    public async Task BulkEdit_SkipsAnAccountSomeoneElseIsEditing()
    {
        var locked = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var free = await TestAccounts.GetAccountKeyAsync("TestAccount04");
        var lockRepository = new AccountProgressLockRepository(new TestDbConnectionFactory());
        await lockRepository.ForceReleaseAsync(locked);
        await lockRepository.ForceReleaseAsync(free);
        var beforeLocked = (await Repository().GetDetailAsync(locked))!;
        var beforeFree = (await Repository().GetDetailAsync(free))!;

        var approver = Client("TestUser.Approver");
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsync($"/api/account-progress/{locked}/lock", null)).StatusCode);
        try
        {
            var result = await (await Client("TestUser.Analyst").PostAsJsonAsync("/api/account-progress/bulk-edit", new
            {
                accountKeys = new[] { locked, free }, fields = new[] { "BusinessUnit" }, businessUnit = "BulkLockTest"
            })).Content.ReadFromJsonAsync<BulkResult>();

            Assert.Equal(1, result!.Updated);
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(locked, skipped.AccountKey);
            Assert.Contains("Being edited by", skipped.Reason);
            Assert.Equal(beforeLocked.BusinessUnit, (await Repository().GetDetailAsync(locked))!.BusinessUnit);
            // The bulk edit released its own lock on the account it saved.
            Assert.Null(await lockRepository.GetStatusAsync(free));
            // ...and left the other user's lock alone.
            Assert.NotNull(await lockRepository.GetStatusAsync(locked));
        }
        finally
        {
            await lockRepository.ForceReleaseAsync(locked);
            await RestoreAsync(beforeLocked);
            await RestoreAsync(beforeFree);
        }
    }

    [Fact]
    public async Task BulkEdit_RejectsBadRequests()
    {
        var client = Client("TestUser.Analyst");
        var key = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var max = (await client.GetFromJsonAsync<JsonObject>("/api/account-progress/keys"))!["maxAccounts"]!.GetValue<int>();

        async Task<string> DetailOf(object body)
        {
            var response = await client.PostAsJsonAsync("/api/account-progress/bulk-edit", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>();
        }

        Assert.Contains("at least one account", await DetailOf(new { accountKeys = Array.Empty<long>(), fields = new[] { "OwnerName" } }));
        Assert.Contains("at least one field", await DetailOf(new { accountKeys = new[] { key }, fields = Array.Empty<string>() }));
        Assert.Contains("ExceptionKey", await DetailOf(new { accountKeys = new[] { key }, fields = new[] { "ExceptionKey" } }));
        Assert.Contains($"at most {max}", await DetailOf(new
        {
            accountKeys = Enumerable.Range(1, max + 1).Select(i => (long)-i).ToArray(), fields = new[] { "OwnerName" }
        }));
    }

    [Fact]
    public async Task Keys_ReturnsEveryMatchingAccount_UpToTheLimit()
    {
        var result = await Client("TestUser.Analyst").GetFromJsonAsync<KeysResult>("/api/account-progress/keys");
        Assert.True(result!.MaxAccounts >= 1);
        if (result.MatchingCount <= result.MaxAccounts)
        {
            Assert.Equal(result.MatchingCount, result.AccountKeys.Count);
            Assert.Contains(await TestAccounts.GetAccountKeyAsync("TestAccount01"), result.AccountKeys);
        }
        else
        {
            Assert.Empty(result.AccountKeys);
        }
    }

    [Fact]
    public async Task BulkEdit_AsViewer_IsForbidden()
    {
        var client = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/account-progress/bulk-edit", new { accountKeys = new[] { 1L }, fields = new[] { "OwnerName" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/account-progress/keys")).StatusCode);
    }
}
