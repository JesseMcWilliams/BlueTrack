using Dapper;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-197: a change to a role, group mapping or identity provider clears every
/// user's cached rights (web.distributed_cache rows keyed "rights:..."), and
/// leaves other cache entries alone.
/// </summary>
public class UserRightsCacheTests
{
    [Fact]
    public async Task InvalidateAll_RemovesOnlyRightsEntries()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var rightsKey = $"rights:999:IntegrationTest_{suffix}";
        var otherKey = $"other:IntegrationTest_{suffix}";
        const string insert = """
            INSERT INTO web.distributed_cache (Id, Value, ExpiresAtTime, SlidingExpirationInSeconds, AbsoluteExpiration)
            VALUES (@Id, 0x00, DATEADD(HOUR, 1, SYSDATETIMEOFFSET()), NULL, NULL)
            """;
        await connection.ExecuteAsync(insert, new { Id = rightsKey });
        await connection.ExecuteAsync(insert, new { Id = otherKey });

        try
        {
            // Only the connection factory is used by InvalidateAllAsync.
            var cache = new UserRightsCache(null!, null!, new TestDbConnectionFactory());
            await cache.InvalidateAllAsync();

            var remaining = (await connection.QueryAsync<string>(
                "SELECT Id FROM web.distributed_cache WHERE Id IN (@rightsKey, @otherKey)", new { rightsKey, otherKey })).ToList();
            Assert.Equal([otherKey], remaining);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.distributed_cache WHERE Id IN (@rightsKey, @otherKey)", new { rightsKey, otherKey });
        }
    }
}
