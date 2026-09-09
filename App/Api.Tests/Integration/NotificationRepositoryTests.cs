using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.notification_config/recipient/dim_notification_type/log (D-115/D-116).
/// </summary>
public class NotificationRepositoryTests
{
    private static NotificationRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task Recipient_CreateUpdateDelete_RoundTrips_AndOnlyActiveOnesAreReturnedByGetActiveRecipientEmailsAsync()
    {
        var repository = CreateRepository();
        var email = $"integrationtest_{Guid.NewGuid():N}@example.com";

        var recipientKey = await repository.CreateRecipientAsync(new SaveNotificationRecipientRequest
        {
            Email = email,
            DisplayName = "Integration Test Recipient",
            IsActive = true
        });

        try
        {
            var activeEmails = await repository.GetActiveRecipientEmailsAsync();
            Assert.Contains(email, activeEmails);

            await repository.UpdateRecipientAsync(recipientKey, new SaveNotificationRecipientRequest
            {
                Email = email,
                DisplayName = "Updated Recipient",
                IsActive = false
            });

            var afterDeactivate = await repository.GetActiveRecipientEmailsAsync();
            Assert.DoesNotContain(email, afterDeactivate);

            var all = await repository.GetRecipientsAsync();
            Assert.Equal("Updated Recipient", Assert.Single(all, r => r.RecipientKey == recipientKey).DisplayName);
        }
        finally
        {
            await repository.DeleteRecipientAsync(recipientKey);
        }

        Assert.DoesNotContain(await repository.GetRecipientsAsync(), r => r.RecipientKey == recipientKey);
    }

    [Fact]
    public async Task WasSentRecentlyAsync_FalseUntilRecorded_TrueWithinCooldown_FalseAfterCooldownWindowPasses()
    {
        var repository = CreateRepository();

        Assert.False(await repository.WasSentRecentlyAsync("DevFakeAuthEnabledTooLong", TimeSpan.FromDays(7)));

        await repository.RecordSentAsync("DevFakeAuthEnabledTooLong", "Integration test send");
        try
        {
            Assert.True(await repository.WasSentRecentlyAsync("DevFakeAuthEnabledTooLong", TimeSpan.FromDays(7)));
            // A cooldown shorter than "just now" has already elapsed.
            Assert.False(await repository.WasSentRecentlyAsync("DevFakeAuthEnabledTooLong", TimeSpan.Zero));
        }
        finally
        {
            await DeleteNotificationLogRowsAsync("DevFakeAuthEnabledTooLong");
        }
    }

    [Fact]
    public async Task GetTargetRoleInfoAsync_NoTargetRoleAssigned_ReturnsNullEmailAndEmptyGroupList()
    {
        var repository = CreateRepository();

        var (roleEmail, groups) = await repository.GetTargetRoleInfoAsync("DevFakeAuthEnabledTooLong");

        Assert.Null(roleEmail);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task SetNotificationTypeTargetRoleAsync_ThenGetTargetRoleInfoAsync_ReturnsRoleEmailAndMappedGroups()
    {
        var repository = CreateRepository();
        var roleRepository = new RoleRepository(new TestDbConnectionFactory());
        var groupMappingRepository = new GroupRoleMappingRepository(new TestDbConnectionFactory());

        var roleKey = await roleRepository.CreateRoleAsync(new SaveRoleRequest
        {
            RoleName = $"IntegrationTestRole_{Guid.NewGuid():N}",
            Description = "Created by a notification-targeting integration test",
            NotificationEmail = "role-email-integrationtest@example.com",
            PermissionNames = []
        });

        try
        {
            var providerKey = await GetDevFakeAuthProviderKeyAsync();
            var groupIdentifier = $"S-1-5-21-0-0-0-{Guid.NewGuid().GetHashCode() & 0x7FFFFFFF}";
            var mappingKey = await groupMappingRepository.CreateAsync(providerKey, groupIdentifier, roleKey);

            var types = await repository.GetNotificationTypesAsync();
            var devFakeAuthType = Assert.Single(types, t => t.NotificationTypeName == "DevFakeAuthEnabledTooLong");

            await repository.SetNotificationTypeTargetRoleAsync(devFakeAuthType.NotificationTypeKey, roleKey);
            try
            {
                var (roleEmail, groups) = await repository.GetTargetRoleInfoAsync("DevFakeAuthEnabledTooLong");

                Assert.Equal("role-email-integrationtest@example.com", roleEmail);
                Assert.Contains(groupIdentifier, groups);
            }
            finally
            {
                await repository.SetNotificationTypeTargetRoleAsync(devFakeAuthType.NotificationTypeKey, null);
                await groupMappingRepository.DeleteAsync(mappingKey);
            }
        }
        finally
        {
            await roleRepository.DeleteRoleAsync(roleKey);
        }
    }

    private static async Task DeleteNotificationLogRowsAsync(string notificationTypeName)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        await Dapper.SqlMapper.ExecuteAsync(connection, """
            DELETE nl FROM web.notification_log nl
            JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
            WHERE nt.NotificationTypeName = @NotificationTypeName
            """, new { NotificationTypeName = notificationTypeName });
    }

    private static async Task<int> GetDevFakeAuthProviderKeyAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth'");
    }
}
