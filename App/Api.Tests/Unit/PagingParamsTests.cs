using BlueTrack.Api;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-124 Phase 3: the shared page/pageSize normalization used by all six
/// paginated list endpoints. This is the deterministic proof that the
/// server-side pageSize cap (PagingParams.MaxPageSize) is a real guard, not
/// decoration -- a request for an absurd pageSize must come back capped at
/// 500, never honored literally (which is also exercised end-to-end, over
/// real HTTP, by RiskScoreReportControllerTests.GetReport_AbsurdPageSize_IsCappedServerSide_RequestStillSucceeds).
/// </summary>
public class PagingParamsTests
{
    [Fact]
    public void Normalize_NullPageAndPageSize_DefaultsToPage1AndPageSize50()
    {
        var (page, pageSize) = PagingParams.Normalize(null, null);

        Assert.Equal(1, page);
        Assert.Equal(50, pageSize);
    }

    [Fact]
    public void Normalize_ValidPageAndPageSize_PassesThroughUnchanged()
    {
        var (page, pageSize) = PagingParams.Normalize(3, 25);

        Assert.Equal(3, page);
        Assert.Equal(25, pageSize);
    }

    [Fact]
    public void Normalize_AbsurdPageSize_IsCappedAtMaxPageSize()
    {
        var (_, pageSize) = PagingParams.Normalize(1, 999_999_999);

        Assert.Equal(500, pageSize);
        Assert.Equal(PagingParams.MaxPageSize, pageSize);
    }

    [Fact]
    public void Normalize_PageSizeExactlyAtCap_IsNotAlteredFurther()
    {
        var (_, pageSize) = PagingParams.Normalize(1, 500);

        Assert.Equal(500, pageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public void Normalize_ZeroOrNegativePage_FloorsAt1(int requestedPage)
    {
        var (page, _) = PagingParams.Normalize(requestedPage, 50);

        Assert.Equal(1, page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Normalize_ZeroOrNegativePageSize_FallsBackToDefault(int requestedPageSize)
    {
        var (_, pageSize) = PagingParams.Normalize(1, requestedPageSize);

        Assert.Equal(PagingParams.DefaultPageSize, pageSize);
    }

    [Theory]
    [InlineData(1, 50, 0)]
    [InlineData(2, 50, 50)]
    [InlineData(3, 25, 50)]
    [InlineData(48, 50, 2350)]
    public void Offset_ComputesZeroBasedOffsetFromOneBasedPage(int page, int pageSize, int expectedOffset)
    {
        Assert.Equal(expectedOffset, PagingParams.Offset(page, pageSize));
    }
}
