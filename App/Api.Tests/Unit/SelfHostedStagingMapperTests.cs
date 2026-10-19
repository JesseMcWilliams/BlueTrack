using System.Data;
using BlueTrack.Api.CyberArkDirect;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-248: PVWA data mapped into EVD-shaped staging rows.</summary>
public class SelfHostedStagingMapperTests
{
    private static readonly Guid Batch = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static StagingTables Map(PvwaSnapshot snapshot, IReadOnlyDictionary<string, long>? locations = null) =>
        new SelfHostedStagingMapper(locations).Map(snapshot, Batch, "PVWA REST (test)");

    private static PvwaAccount Account(long safe = 59, long file = 3, string? platform = "UnixSSH", bool? auto = null, string? reason = null,
        IReadOnlyDictionary<string, string>? props = null) =>
        new(safe, file, "Huge_Bunch", "Operating System-UnixSSH-Test01-root", "Test01", "root", platform,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc), null, auto, reason,
            props ?? new Dictionary<string, string>());

    private static Dictionary<string, string> Properties(DataTable table, long safe, int file) =>
        table.Rows.Cast<DataRow>().Where(r => (long)r["CAOPSafeId"] == safe && (int)r["CAOPFileId"] == file)
            .ToDictionary(r => (string)r["CAOPObjectPropertyName"], r => (string)r["CAOPObjectPropertyValue"]);

    [Fact]
    public void Account_BecomesAFileRow_WithTheEvdsSafeAndFileIds_AndAPasswordType()
    {
        var tables = Map(new PvwaSnapshot { Accounts = { Account() } });

        var row = Assert.Single(tables.Files.Rows.Cast<DataRow>());
        Assert.Equal(59L, row["CAFSafeID"]);
        Assert.Equal(3L, row["CAFFileID"]);
        Assert.Equal("Operating System-UnixSSH-Test01-root", row["CAFFileName"]);
        Assert.Equal("Huge_Bunch", row["CAFSafeName"]);
        Assert.Equal(2, row["CAFType"]);   // 'Password', as the Load filters on
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), row["CAFCreationDate"]);
        Assert.Equal(new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc), row["CAFModificationDate"]);
        Assert.Equal(DBNull.Value, row["CAFLastUsedDate"]);   // PVWA didn't send a verified date
    }

    [Fact]
    public void Account_BecomesTheEavRowsTheLoadPivots()
    {
        var snapshot = new PvwaSnapshot
        {
            Accounts = { Account(props: new Dictionary<string, string> { ["LogonDomain"] = "CORP", ["Location"] = "IT", ["UserName"] = "ignored" }) }
        };

        var props = Properties(Map(snapshot).ObjectProperties, 59, 3);

        Assert.Equal("root", props["UserName"]);      // the account's own value wins over a property of the same name
        Assert.Equal("Test01", props["Address"]);
        Assert.Equal("UnixSSH", props["PolicyID"]);
        Assert.Equal("CORP", props["LogonDomain"]);
        Assert.Equal("IT", props["Location"]);
        Assert.DoesNotContain("CPMDisabled", props.Keys);
    }

    [Fact]
    public void AccountWithoutAPlatform_HasNoPolicyId_AndOneWithBlankPlatformEither()
    {
        var snapshot = new PvwaSnapshot { Accounts = { Account(file: 1, platform: null), Account(file: 2, platform: " ") } };

        var tables = Map(snapshot);

        Assert.DoesNotContain("PolicyID", Properties(tables.ObjectProperties, 59, 1).Keys);
        Assert.DoesNotContain("PolicyID", Properties(tables.ObjectProperties, 59, 2).Keys);
    }

    [Theory]
    [InlineData(false, "Managed by the platform team", "Managed by the platform team")]
    [InlineData(false, null, "No reason given")]
    [InlineData(false, "  ", "No reason given")]
    public void ManagementTurnedOff_BecomesCpmDisabledWithTheReason(bool auto, string? reason, string expected)
    {
        var props = Properties(Map(new PvwaSnapshot { Accounts = { Account(auto: auto, reason: reason) } }).ObjectProperties, 59, 3);

        Assert.Equal(expected, props["CPMDisabled"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void ManagementOnOrUnknown_LeavesCpmDisabledOut(bool? auto) =>
        Assert.DoesNotContain("CPMDisabled", Properties(Map(new PvwaSnapshot { Accounts = { Account(auto: auto, reason: "stale text") } }).ObjectProperties, 59, 3).Keys);

    [Fact]
    public void PropertyNameLongerThan29_IsLeftOut_AndReported()
    {
        var tables = Map(new PvwaSnapshot { Accounts = { Account(props: new Dictionary<string, string> { [new string('P', 30)] = "x", ["Short"] = "y" }) } });

        var props = Properties(tables.ObjectProperties, 59, 3);
        Assert.Equal("y", props["Short"]);
        Assert.Equal(4, props.Count);   // UserName, Address, PolicyID, Short
        Assert.Contains("1 account property with a name over 29 characters was not staged", Assert.Single(tables.Warnings));
    }

    [Fact]
    public void Permissions_MapToTheEvdOwnerColumns_AndAMissingPermissionStaysNull()
    {
        var perms = new Dictionary<string, bool>
        {
            ["listAccounts"] = true, ["retrieveAccounts"] = false, ["addAccounts"] = true, ["updateAccountContent"] = false,
            ["updateAccountProperties"] = true, ["renameAccounts"] = false, ["deleteAccounts"] = true, ["viewAuditLog"] = true,
            ["viewSafeMembers"] = true, ["useAccounts"] = true, ["initiateCPMAccountManagementOperations"] = true,
            ["specifyNextAccountContent"] = false, ["moveAccountsAndFolders"] = true, ["manageSafe"] = true,
            ["manageSafeMembers"] = false, ["backupSafe"] = true, ["accessWithoutConfirmation"] = true,
            ["requestsAuthorizationLevel1"] = true
            // createFolders, deleteFolders, unlockAccounts, requestsAuthorizationLevel2: not sent
        };
        var snapshot = new PvwaSnapshot { Members = { new PvwaMember(7, "SafeSeven", 42, "Auditors", IsGroup: true, ExpirationDate: null, perms) } };

        var row = Assert.Single(Map(snapshot).Owners.Rows.Cast<DataRow>());

        Assert.Equal(7L, row["CAOSafeID"]);
        Assert.Equal(42L, row["CAOOwnerID"]);
        Assert.Equal(1, row["CAOOwnerType"]);   // 1 = group
        Assert.Equal("YES", row["CAOList"]);
        Assert.Equal("NO", row["CAORetrieve"]);
        Assert.Equal("YES", row["CAOCreateObject"]);
        Assert.Equal("YES", row["CAOUpdateObjectProperties"]);
        Assert.Equal("NO", row["CAOUpdateObject"]);
        Assert.Equal("YES", row["CAOViewAudit"]);
        Assert.Equal("YES", row["CAOViewOwners"]);
        Assert.Equal("YES", row["CAOUsePassword"]);
        Assert.Equal("YES", row["CAOInitiateCPMChange"]);
        Assert.Equal("NO", row["CAOInitiateCPMChangeWithManualPassword"]);
        Assert.Equal("YES", row["CAOMoveFrom"]);
        Assert.Equal("YES", row["CAOMoveInto"]);
        Assert.Equal("YES", row["CAOManageSafe"]);
        Assert.Equal("NO", row["CAOManageSafeOwners"]);
        Assert.Equal("YES", row["CAOBackup"]);
        Assert.Equal("YES", row["CAONoConfirmRequired"]);
        Assert.Equal("YES", row["CAOConfirm"]);
        Assert.Equal(DBNull.Value, row["CAOCreateFolder"]);
        Assert.Equal(DBNull.Value, row["CAOUnlockObject"]);
    }

    [Fact]
    public void UserMember_IsOwnerTypeZero()
    {
        var snapshot = new PvwaSnapshot { Members = { new PvwaMember(7, "S", 2, "Administrator", IsGroup: false, ExpirationDate: null, new Dictionary<string, bool>()) } };

        Assert.Equal(0, Map(snapshot).Owners.Rows[0]["CAOOwnerType"]);
    }

    [Theory]
    [InlineData("Built-InAdmins", 10)]
    [InlineData("EPVUser", 34)]
    [InlineData("PSM", 36)]
    [InlineData("epvuser", 34)]
    public void UserType_MapsToTheEvdsTypeId(string type, int expected)
    {
        var row = Map(new PvwaSnapshot { Users = { new PvwaUser(5, "u", "CyberArk", type, "\\", true, null, null, null) } }).Users.Rows[0];

        Assert.Equal(expected, row["CAUUserTypeID"]);
    }

    [Fact]
    public void UnknownUserType_StaysNull()
    {
        var row = Map(new PvwaSnapshot { Users = { new PvwaUser(5, "u", "CyberArk", "SomethingNew", "\\", true, null, null, null) } }).Users.Rows[0];

        Assert.Equal(DBNull.Value, row["CAUUserTypeID"]);
    }

    [Fact]
    public void User_MapsSourceEnabledAndNames_AndCutsToTheColumnWidths()
    {
        var snapshot = new PvwaSnapshot
        {
            Users =
            {
                new PvwaUser(1, "Backup", "CyberArk", "Built-InAdmins", "\\System", Enabled: true, "First", "Last", "a@b.com"),
                new PvwaUser(2, "adm.jesse", "LDAP", "EPVUser", "\\", Enabled: false, new string('F', 40), new string('L', 40), new string('e', 80))
            }
        };

        var rows = Map(snapshot).Users.Rows.Cast<DataRow>().ToList();

        Assert.Equal((1, "NO"), ((int)rows[0]["CAUExternalInternal"], (string)rows[0]["CAUDisabled"]));   // CyberArk = internal, enabled
        Assert.Equal((2, "YES"), ((int)rows[1]["CAUExternalInternal"], (string)rows[1]["CAUDisabled"]));  // LDAP = external, disabled
        Assert.Equal(30, ((string)rows[1]["CAUFirstName"]).Length);
        Assert.Equal(30, ((string)rows[1]["CAULastName"]).Length);
        Assert.Equal(50, ((string)rows[1]["CAUBusinessEmail"]).Length);
    }

    [Fact]
    public void GroupMemberWhoseIdIsAGroup_IsFlaggedAsANestedGroup()
    {
        var snapshot = new PvwaSnapshot
        {
            Groups =
            {
                new PvwaGroup(12, "Vault Admins", "Vault", "\\", null, [2, 41]),
                new PvwaGroup(41, "CyberArk Vault Admins", "Directory", "\\", null, [])
            }
        };

        var members = Map(snapshot).GroupMembers.Rows.Cast<DataRow>().ToDictionary(r => (long)r["CAGMUserID"], r => (string)r["CAGMMemberIsGroup"]);

        Assert.Equal("NO", members[2]);
        Assert.Equal("YES", members[41]);
    }

    [Fact]
    public void GroupWithMembers_BecomesGroupAndMemberRows()
    {
        var snapshot = new PvwaSnapshot { Groups = { new PvwaGroup(9, "Auditors", "Vault", "\\", "Audit group", [3, 32, 33]) } };

        var tables = Map(snapshot);

        var group = Assert.Single(tables.Groups.Rows.Cast<DataRow>());
        Assert.Equal((9L, "Auditors", 1), ((long)group["CAGGroupID"], (string)group["CAGGroupName"], (int)group["CAGExternalInternal"]));
        Assert.Equal(new long[] { 3, 32, 33 }, tables.GroupMembers.Rows.Cast<DataRow>().Select(r => (long)r["CAGMUserID"]));
        Assert.All(tables.GroupMembers.Rows.Cast<DataRow>(), r => Assert.Equal("NO", r["CAGMMemberIsGroup"]));
    }

    [Fact]
    public void Safe_MapsNumberAndRetention()
    {
        var snapshot = new PvwaSnapshot
        {
            Safes = { new PvwaSafe(59, "Huge_Bunch", "Huge_Bunch", "\\", "Administrator", new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc), DaysRetention: 30, VersionsRetention: 5) }
        };

        var row = Assert.Single(Map(snapshot).Safes.Rows.Cast<DataRow>());

        Assert.Equal(59L, row["CASSafeID"]);
        Assert.Equal("Administrator", row["CASCreatedBy"]);
        Assert.Equal(5, row["CASYearlyVersions"]);          // the Load's VersionRetention
        Assert.Equal(30, row["CASObjectsRetentionPeriod"]); // the Load's DayRetention
    }

    [Fact]
    public void Locations_KeepAKnownNamesId_AndNewOnesTakeTheNextFreeNumber()
    {
        var snapshot = new PvwaSnapshot
        {
            Safes =
            {
                new PvwaSafe(1, "A", "A", "\\", null, null, null, null),
                new PvwaSafe(2, "B", "B", "\\System", null, null, null, null),
                new PvwaSafe(3, "C", "C", "\\New Place", null, null, null, null),
                new PvwaSafe(4, "D", "D", "\\NEW place", null, null, null, null),   // same name, other case
                new PvwaSafe(5, "E", "E", null, null, null, null, null)
            }
        };

        var rows = Map(snapshot, new Dictionary<string, long> { ["\\"] = 0, ["\\System"] = 1 }).Safes.Rows.Cast<DataRow>().ToList();

        Assert.Equal(new object[] { 0L, 1L, 2L, 2L, DBNull.Value }, rows.Select(r => r["CASLocationID"]));
        Assert.Equal(DBNull.Value, rows[4]["CASLocationName"]);
    }

    [Fact]
    public void Locations_WithNoneKnown_StartAtZero() =>
        Assert.Equal(0L, Map(new PvwaSnapshot { Groups = { new PvwaGroup(1, "G", "Vault", "\\", null, []) } }).Groups.Rows[0]["CAGLocationID"]);

    [Fact]
    public void Platforms_AreLowercasedAndCut()
    {
        var snapshot = new PvwaSnapshot
        {
            Platforms = { new PvwaPlatform("WinDesktopLocal", "Windows Desktop Local Accounts", "desc", Active: true, "Regular"), new PvwaPlatform("Old", null, null, null, null) }
        };

        var rows = Map(snapshot).Platforms.Rows.Cast<DataRow>().ToList();

        Assert.Equal(("WinDesktopLocal", "regular", true), ((string)rows[0]["PlatformID"], (string)rows[0]["PlatformType"], (bool)rows[0]["IsActive"]));
        Assert.Equal(DBNull.Value, rows[1]["PlatformName"]);
        Assert.Equal(DBNull.Value, rows[1]["IsActive"]);
    }

    [Fact]
    public void EveryRow_CarriesTheBatchAndSource()
    {
        var snapshot = new PvwaSnapshot { Safes = { new PvwaSafe(1, "A", "A", "\\", null, null, null, null) }, Accounts = { Account() } };

        var tables = Map(snapshot);

        foreach (var row in tables.All.SelectMany(t => t.Rows.Cast<DataRow>()))
        {
            Assert.Equal(Batch, row["ImportBatchId"]);
            Assert.Equal("PVWA REST (test)", row["SourceFileName"]);
        }
    }
}
