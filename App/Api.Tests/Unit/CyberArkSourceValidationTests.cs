using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;
using BlueTrack.Api.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-211: the checks on CyberArk source settings before a save or a Test.</summary>
public class CyberArkSourceValidationTests
{
    private static readonly AllowedFileRoots NoRoots = new(new ConfigurationBuilder().Build());

    [Fact]
    public void Normalize_TrimsValues_AndDropsAnUnusedSourcesValue()
    {
        var normalized = CyberArkSourceValidation.Normalize(new CyberArkSourceSettings
        {
            ImportPrivilegeCloud = true,
            ExportFolderPath = "  D:\\Exports\\ ",
            UsersFileName = " users.csv ",
            ImportSelfHosted = false,
            PvwaBaseUrl = "https://pvwa.example.com"
        });

        Assert.Equal("D:\\Exports", normalized.ExportFolderPath);
        Assert.Equal("users.csv", normalized.UsersFileName);
        Assert.Null(normalized.PvwaBaseUrl);
    }

    [Fact]
    public void Validate_DefaultsWithOneSource_AreValid()
    {
        var settings = new CyberArkSourceSettings { ImportPrivilegeCloud = true, ExportFolderPath = "D:\\Exports" };

        Assert.Empty(CyberArkSourceValidation.Validate(settings, NoRoots));
    }

    [Theory]
    [InlineData(false, null, false, null, "at least one source")]
    [InlineData(true, null, false, null, "Export folder is required")]
    [InlineData(true, "\\\\server\\share", false, null, "UNC")]
    [InlineData(false, null, true, null, "PVWA address is required")]
    [InlineData(false, null, true, "http://pvwa.example.com", "PVWA address must be https://host")]
    public void Validate_ReportsEachProblem(bool privilegeCloud, string? folder, bool selfHosted, string? pvwa, string expected)
    {
        var settings = CyberArkSourceValidation.Normalize(new CyberArkSourceSettings
        {
            ImportPrivilegeCloud = privilegeCloud, ExportFolderPath = folder, ImportSelfHosted = selfHosted, PvwaBaseUrl = pvwa
        });

        Assert.Contains(CyberArkSourceValidation.Validate(settings, NoRoots), e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Export_*.csv", "* and ?")]
    [InlineData("sub\\file.csv", "file name only")]
    [InlineData("", "required")]
    public void Validate_RejectsFileNamesSqlServerCantUse(string fileName, string expected)
    {
        var settings = new CyberArkSourceSettings { ImportPrivilegeCloud = true, ExportFolderPath = "D:\\Exports", SafesFileName = fileName };

        Assert.Contains(CyberArkSourceValidation.Validate(settings, NoRoots), e => e.StartsWith("Safes file:") && e.Contains(expected));
    }
}
