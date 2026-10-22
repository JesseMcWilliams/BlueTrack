using BlueTrack.Api.Formatting;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-294: the installation's format culture. Only TryGet is tested here: Set changes the process-wide value that
/// other tests (running in parallel) format with.
/// </summary>
public class ServerTextCultureTests
{
    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("de-de", "de-DE")]
    [InlineData(" fr-FR ", "fr-FR")]
    public void ASpecificCulture_IsAccepted_ByItsOwnName(string name, string expected) =>
        Assert.Equal(expected, ServerTextCulture.TryGet(name)!.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en")]            // neutral: no number or date format of its own
    [InlineData("not a culture!")]
    public void AnythingElse_IsRefused(string? name) => Assert.Null(ServerTextCulture.TryGet(name));

    [Fact]
    public void TheDefault_IsEnUs() => Assert.Equal("en-US", ServerTextCulture.DefaultName);
}
