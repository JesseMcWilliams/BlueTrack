using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.dim_application backs the Risk Exception scoping dropdown and the
/// Application ↔ Safe Mapping admin page. ApplicationName has its own
/// UNIQUE constraint (confirmed directly -- see AdminControllersFunctionalTests'
/// own note) and ApplicationRepository has no Delete method, so every row
/// created here is cleaned up with a direct SQL delete.
/// </summary>
public class ApplicationRepositoryTests
{
    private static ApplicationRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task CreateAsync_IsReadableByGetAllAsyncAndGetAllDetailedAsync()
    {
        var repository = CreateRepository();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var applicationKey = await repository.CreateAsync(new SaveApplicationRequest
        {
            ApplicationCode = $"ITAPP{suffix}",
            ApplicationName = $"Integration Test Application {suffix}",
            OwnerName = "Integration Test Owner"
        });

        try
        {
            var summaries = await repository.GetAllAsync();
            Assert.Contains(summaries, a => a.ApplicationKey == applicationKey);

            var detailed = await repository.GetAllDetailedAsync();
            var detail = Assert.Single(detailed, a => a.ApplicationKey == applicationKey);
            Assert.Equal("Integration Test Owner", detail.OwnerName);
        }
        finally
        {
            await DeleteApplicationAsync(applicationKey);
        }
    }

    [Fact]
    public async Task UpdateAsync_ChangesArePersisted()
    {
        var repository = CreateRepository();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var applicationKey = await repository.CreateAsync(new SaveApplicationRequest
        {
            ApplicationCode = $"ITAPP{suffix}",
            ApplicationName = $"Integration Test Application {suffix}"
        });

        try
        {
            await repository.UpdateAsync(applicationKey, new SaveApplicationRequest
            {
                ApplicationCode = $"ITAPP{suffix}",
                ApplicationName = $"Integration Test Application {suffix} (Updated)",
                Notes = "Updated by an integration test"
            });

            var detailed = await repository.GetAllDetailedAsync();
            var detail = Assert.Single(detailed, a => a.ApplicationKey == applicationKey);
            Assert.Equal($"Integration Test Application {suffix} (Updated)", detail.ApplicationName);
            Assert.Equal("Updated by an integration test", detail.Notes);
        }
        finally
        {
            await DeleteApplicationAsync(applicationKey);
        }
    }

    [Fact]
    public async Task GetAllSafesAsync_IncludesTheSyntheticTestSafe()
    {
        var repository = CreateRepository();

        var safes = await repository.GetAllSafesAsync();

        Assert.Contains(safes, s => s.SafeName == "TestSafe01");
    }

    [Fact]
    public async Task AssignSafeApplicationAsync_AssignThenClear_RoundTrips()
    {
        var repository = CreateRepository();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var applicationKey = await repository.CreateAsync(new SaveApplicationRequest
        {
            ApplicationCode = $"ITSAFE{suffix}",
            ApplicationName = $"Safe Assignment Integration Test {suffix}"
        });
        var safeKey = await LookupSafeKeyAsync("TestSafe01");

        try
        {
            await repository.AssignSafeApplicationAsync(safeKey, applicationKey);
            var afterAssign = await repository.GetAllSafesAsync();
            Assert.Contains(afterAssign, s => s.SafeKey == safeKey && s.ApplicationKey == applicationKey);

            await repository.AssignSafeApplicationAsync(safeKey, null);
            var afterClear = await repository.GetAllSafesAsync();
            Assert.Contains(afterClear, s => s.SafeKey == safeKey && s.ApplicationKey == null);
        }
        finally
        {
            await DeleteApplicationAsync(applicationKey);
        }
    }

    private static async Task<int> LookupSafeKeyAsync(string safeName)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT SafeKey FROM dbo.dim_safe WHERE SafeName = @SafeName", new { SafeName = safeName });
    }

    private static async Task DeleteApplicationAsync(int applicationKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM web.dim_application WHERE ApplicationKey = @ApplicationKey", new { ApplicationKey = applicationKey });
    }
}
