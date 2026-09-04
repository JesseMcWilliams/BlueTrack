using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>web.account_progress_field_metadata (Design_Interface_Extensibility.md), backing the Account Progress edit form.</summary>
public class FieldMetadataRepositoryTests
{
    private static FieldMetadataRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task CreateAsync_IsReadableByGetAllAsync()
    {
        var repository = CreateRepository();
        var fieldName = $"IntegrationTestField_{Guid.NewGuid():N}";
        var fieldMetadataKey = await repository.CreateAsync(new SaveFieldMetadataRequest
        {
            FieldName = fieldName,
            DisplayLabel = "Integration Test Field",
            FieldType = "text",
            IsRequired = false,
            DisplayOrder = 999
        });

        try
        {
            var all = await repository.GetAllAsync();
            var created = Assert.Single(all, f => f.FieldMetadataKey == fieldMetadataKey);
            Assert.Equal("Integration Test Field", created.DisplayLabel);
            Assert.False(created.IsRequired);
        }
        finally
        {
            await repository.DeleteAsync(fieldMetadataKey);
        }
    }

    [Fact]
    public async Task UpdateAsync_ChangesArePersisted()
    {
        var repository = CreateRepository();
        var fieldName = $"IntegrationTestField_{Guid.NewGuid():N}";
        var fieldMetadataKey = await repository.CreateAsync(new SaveFieldMetadataRequest
        {
            FieldName = fieldName,
            DisplayLabel = "Original Label",
            FieldType = "text",
            IsRequired = false,
            DisplayOrder = 999
        });

        try
        {
            await repository.UpdateAsync(fieldMetadataKey, new SaveFieldMetadataRequest
            {
                FieldName = fieldName,
                DisplayLabel = "Updated Label",
                FieldType = "text",
                IsRequired = true,
                DisplayOrder = 998
            });

            var all = await repository.GetAllAsync();
            var updated = Assert.Single(all, f => f.FieldMetadataKey == fieldMetadataKey);
            Assert.Equal("Updated Label", updated.DisplayLabel);
            Assert.True(updated.IsRequired);
            Assert.Equal(998, updated.DisplayOrder);
        }
        finally
        {
            await repository.DeleteAsync(fieldMetadataKey);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow()
    {
        var repository = CreateRepository();
        var fieldMetadataKey = await repository.CreateAsync(new SaveFieldMetadataRequest
        {
            FieldName = $"IntegrationTestField_{Guid.NewGuid():N}",
            DisplayLabel = "To Be Deleted",
            FieldType = "text",
            IsRequired = false,
            DisplayOrder = 999
        });

        await repository.DeleteAsync(fieldMetadataKey);

        var all = await repository.GetAllAsync();
        Assert.DoesNotContain(all, f => f.FieldMetadataKey == fieldMetadataKey);
    }
}
