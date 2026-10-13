using System.Security.AccessControl;
using System.Security.Principal;
using BlueTrack.Api.Backups;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-203 against the real file system: a folder this test creates (so it owns
/// it and may change its permissions), given an explicit "Users: Read" on the
/// folder and on a .bak file in it.
/// </summary>
public class BackupFolderAccessTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static void AllowUsersRead(FileSystemInfo item)
    {
        var rule = new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow);
        switch (item)
        {
            case DirectoryInfo d: var ds = d.GetAccessControl(); ds.AddAccessRule(rule); d.SetAccessControl(ds); break;
            case FileInfo f: var fs = f.GetAccessControl(); fs.AddAccessRule(rule); f.SetAccessControl(fs); break;
        }
    }

    [Fact]
    public void Enforce_RemovesBroadReadAccess_FromTheFolderAndItsBackups()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BlueTrackBackupAclTest_" + Guid.NewGuid().ToString("N")));
        try
        {
            var bak = new FileInfo(Path.Combine(folder.FullName, "BlueTrack_20261012.bak"));
            File.WriteAllText(bak.FullName, "not a real backup");
            AllowUsersRead(folder);
            AllowUsersRead(bak);
            Assert.NotEmpty(BackupFolderAccess.BroadReaders(folder.GetAccessControl()));

            var outcome = BackupFolderAccess.Enforce(folder.FullName);

            Assert.True(outcome.MayBackUp);
            Assert.Contains("Removed broad groups' read access", outcome.Note);
            Assert.Empty(BackupFolderAccess.BroadReaders(new DirectoryInfo(folder.FullName).GetAccessControl()));
            Assert.Empty(BackupFolderAccess.BroadReaders(new FileInfo(bak.FullName).GetAccessControl()));
            Assert.True(new DirectoryInfo(folder.FullName).GetAccessControl().AreAccessRulesProtected);

            // Already fixed: nothing to report the second time.
            Assert.Null(BackupFolderAccess.Enforce(folder.FullName).Note);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Enforce_AFolderThisServerCantSee_BacksUpWithANote()
    {
        var outcome = BackupFolderAccess.Enforce(Path.Combine(Path.GetTempPath(), "BlueTrackNoSuchFolder_" + Guid.NewGuid().ToString("N")));
        Assert.True(outcome.MayBackUp);
        Assert.Contains("weren't checked", outcome.Note);
    }

    [Theory]
    [InlineData("S-1-1-0", true)]                                     // Everyone
    [InlineData("S-1-5-32-545", true)]                                // BUILTIN\Users
    [InlineData("S-1-5-11", true)]                                    // Authenticated Users
    [InlineData("S-1-5-21-305334961-334695230-3836195740-513", true)] // a domain's Domain Users
    [InlineData("S-1-5-32-544", false)]                               // BUILTIN\Administrators
    [InlineData("S-1-5-18", false)]                                   // SYSTEM
    [InlineData("S-1-5-21-305334961-334695230-3836195740-1105", false)]
    public void IsBroadGroup(string sid, bool expected)
    {
        Assert.Equal(expected, BackupFolderAccess.IsBroadGroup(new SecurityIdentifier(sid)));
    }
}
