using BlueTrack.Api.Backups;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-200: a scheduled backup's clean-up deletes only this database's old scheduled backups.</summary>
public class BackupRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ScheduledFileName_CarriesTheMarkerAndUtcTime() =>
        Assert.Equal("BlueTrack_scheduled_20261010_010000.bak", BackupRetention.ScheduledFileName("BlueTrack", Now));

    [Fact]
    public void FilesToDelete_OnlyOldScheduledBackupsOfThisDatabase()
    {
        var names = new[]
        {
            "BlueTrack_scheduled_20260920_010000.bak",   // 20 days old: delete
            "BlueTrack_scheduled_20261001_010000.bak",   // 9 days old: keep
            "BlueTrack_20260901_120000.bak",             // a manual backup: never
            "BlueTrackTest_scheduled_20260101_010000.bak", // another database: never
            "BlueTrack_scheduled_notadate.bak",          // not our name shape: never
            "notes.txt",
        };

        Assert.Equal(["BlueTrack_scheduled_20260920_010000.bak"], BackupRetention.FilesToDelete(names, "BlueTrack", 14, Now));
    }
}
