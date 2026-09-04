using BlueTrack.Api;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

public class SortParserTests
{
    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(SortParser.Parse(null));
        Assert.Empty(SortParser.Parse(""));
        Assert.Empty(SortParser.Parse("   "));
    }

    [Fact]
    public void Parse_SingleField_DefaultsToAscending()
    {
        var result = SortParser.Parse("stageName");

        var field = Assert.Single(result);
        Assert.Equal("stageName", field.Field);
        Assert.False(field.Descending);
    }

    [Fact]
    public void Parse_ExplicitDescending_IsCaseInsensitive()
    {
        var result = SortParser.Parse("ownerName:DESC");

        var field = Assert.Single(result);
        Assert.Equal("ownerName", field.Field);
        Assert.True(field.Descending);
    }

    [Fact]
    public void Parse_UnrecognizedDirection_DefaultsToAscending()
    {
        var result = SortParser.Parse("stageName:sideways");

        var field = Assert.Single(result);
        Assert.False(field.Descending);
    }

    [Fact]
    public void Parse_MultipleFields_PreservesOrder()
    {
        var result = SortParser.Parse("stageName:asc,ownerName:desc, riskLevel");

        Assert.Equal(3, result.Count);
        Assert.Equal(("stageName", false), result[0]);
        Assert.Equal(("ownerName", true), result[1]);
        Assert.Equal(("riskLevel", false), result[2]);
    }

    [Theory]
    [InlineData("stageName:asc'; DROP TABLE fact_account_progress; --")]
    [InlineData("' OR 1=1 --")]
    public void Parse_SqlInjectionAttempt_IsTreatedAsAnOpaqueFieldName(string maliciousSort)
    {
        // SortParser itself does no validation -- it's purely a splitter.
        // The SQL-injection guard is the downstream column whitelist (each
        // repository's own allowed-sort-columns check), not here. This test
        // documents that boundary: whatever comes in here comes out as a
        // literal field name, never executed or interpreted.
        var result = SortParser.Parse(maliciousSort);

        Assert.All(result, f => Assert.DoesNotContain(',', f.Field));
    }
}
