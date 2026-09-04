using BlueTrack.Api.Data;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

public class ExceptionIdGeneratorTests
{
    [Fact]
    public void Generate_DefaultPattern_RendersYearAndPaddedSequence()
    {
        var result = ExceptionIdGenerator.Generate("EXC-{yyyy}-{seq:0000}", 2026, 7);

        Assert.Equal("EXC-2026-0007", result);
    }

    [Fact]
    public void Generate_TwoDigitYearToken_UsesLastTwoDigits()
    {
        var result = ExceptionIdGenerator.Generate("EXC-{yy}-{seq:000}", 2026, 3);

        Assert.Equal("EXC-26-003", result);
    }

    [Fact]
    public void Generate_SequenceWiderThanPadding_IsNotTruncated()
    {
        var result = ExceptionIdGenerator.Generate("EXC-{seq:000}", 2026, 12345);

        Assert.Equal("EXC-12345", result);
    }

    [Fact]
    public void Generate_NoTokens_ReturnsPatternUnchanged()
    {
        var result = ExceptionIdGenerator.Generate("STATIC-PREFIX", 2026, 1);

        Assert.Equal("STATIC-PREFIX", result);
    }
}
