using BlueTrack.Api.DataFeeds;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-228: how Test compares a Privilege Cloud file's header with its columns.</summary>
public class CyberArkImportColumnsTests
{
    private static readonly CyberArkImportColumn[] Users =
    [
        new("Users", "UserID", true, 1),
        new("Users", "Username", true, 2),
        new("Users", "Email", false, 6),
        new("Users", "Enabled", false, 9)
    ];

    [Theory]
    [InlineData("UsersFileName", "Users")]
    [InlineData("GroupMembersFileName", "GroupMembers")]
    [InlineData("EntitlementsFileName", "Entitlements")]
    public void FileKindOf_DropsTheFileNameSuffix(string field, string kind) =>
        Assert.Equal(kind, CyberArkImportColumns.FileKindOf(field));

    [Fact]
    public void Compare_IgnoresCaseSpacesAndUnknownColumns()
    {
        var (missingRequired, missingOptional) = CyberArkImportColumns.Compare([" userid ", "USERNAME", "Enabled", "Unknown"], Users);

        Assert.Empty(missingRequired);
        Assert.Equal(["Email"], missingOptional);
    }

    [Fact]
    public void Compare_ListsMissingRequiredColumns_InTheTablesOrder()
    {
        var (missingRequired, missingOptional) = CyberArkImportColumns.Compare([], Users);

        Assert.Equal(["UserID", "Username"], missingRequired);
        Assert.Equal(["Email", "Enabled"], missingOptional);
    }
}
