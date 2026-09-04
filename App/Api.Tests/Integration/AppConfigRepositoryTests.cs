using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.app_config/web.audit_config are both singleton rows (06_BlueTrack_WebInterface_Schema.sql) --
/// every test here restores the original values afterward since this is shared, real BlueTrackTest state.
/// </summary>
public class AppConfigRepositoryTests
{
    private static AppConfigRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task GetAsync_ReturnsTheSingletonRow()
    {
        var repository = CreateRepository();

        var config = await repository.GetAsync();

        Assert.NotNull(config.BreadcrumbPosition);
        Assert.NotNull(config.ExceptionIdPattern);
    }

    [Fact]
    public async Task IsLogReadEventsEnabledAsync_MatchesTheSingletonRowsValue()
    {
        var repository = CreateRepository();
        var config = await repository.GetAsync();

        var isEnabled = await repository.IsLogReadEventsEnabledAsync();

        Assert.Equal(config.LogReadEvents, isEnabled);
    }

    [Fact]
    public async Task UpdateAsync_PersistsThenRestoresOriginalValues()
    {
        var repository = CreateRepository();
        var before = await repository.GetAsync();

        await repository.UpdateAsync(new SaveGlobalApplicationConfigRequest
        {
            IdleTimeoutMinutes = before.IdleTimeoutMinutes + 1,
            BreadcrumbPosition = before.BreadcrumbPosition,
            ExceptionIdPattern = before.ExceptionIdPattern,
            LockTimeoutMinutes = before.LockTimeoutMinutes,
            RetentionDays = before.RetentionDays,
            LogReadEvents = !before.LogReadEvents
        }, modifiedByUserKey: await TestUsers.GetUserKeyAsync("IntegrationTestUser1"));

        var after = await repository.GetAsync();
        Assert.Equal(before.IdleTimeoutMinutes + 1, after.IdleTimeoutMinutes);
        Assert.Equal(!before.LogReadEvents, after.LogReadEvents);

        await repository.UpdateAsync(new SaveGlobalApplicationConfigRequest
        {
            IdleTimeoutMinutes = before.IdleTimeoutMinutes,
            BreadcrumbPosition = before.BreadcrumbPosition,
            ExceptionIdPattern = before.ExceptionIdPattern,
            LockTimeoutMinutes = before.LockTimeoutMinutes,
            RetentionDays = before.RetentionDays,
            LogReadEvents = before.LogReadEvents
        }, modifiedByUserKey: await TestUsers.GetUserKeyAsync("IntegrationTestUser1"));

        var restored = await repository.GetAsync();
        Assert.Equal(before.IdleTimeoutMinutes, restored.IdleTimeoutMinutes);
        Assert.Equal(before.LogReadEvents, restored.LogReadEvents);
    }
}
