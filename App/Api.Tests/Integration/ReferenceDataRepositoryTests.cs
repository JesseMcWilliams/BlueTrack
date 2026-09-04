using BlueTrack.Api.Data;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Backs the Dropdown fields on the field-metadata-driven Account Progress
/// edit form (Design_Interface_Extensibility.md). All five reference
/// tables are seeded, fixed dimension tables (never empty in a built database).
/// </summary>
public class ReferenceDataRepositoryTests
{
    [Fact]
    public async Task GetAllReferenceDataAsync_ReturnsAllFiveKnownTablesWithData()
    {
        var repository = new ReferenceDataRepository(new TestDbConnectionFactory());

        var result = await repository.GetAllReferenceDataAsync();

        Assert.Equal(5, result.Count);
        foreach (var table in new[] { "dim_blueprint_stage", "dim_progress_status", "dim_risk_level", "dim_account_type", "dim_source_of_record" })
        {
            Assert.True(result.ContainsKey(table), $"Expected reference data for {table}");
            Assert.NotEmpty(result[table]);
        }
    }
}
