using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

public class CyberArkErrorClassifierTests
{
    [Theory]
    [InlineData("APPAP004E Password object not found", CyberArkErrorCategory.NotFound)]
    [InlineData("APPAP008E User is not authorized", CyberArkErrorCategory.AccessDenied)]
    [InlineData("APPAP227E Ambiguous query", CyberArkErrorCategory.AmbiguousQuery)]
    [InlineData("APPAP282E Password change in process", CyberArkErrorCategory.PasswordChangeInProgress)]
    [InlineData("APPAP007E Vault is down", CyberArkErrorCategory.VaultConnectivity)]
    [InlineData("Some unrecognized failure", CyberArkErrorCategory.Other)]
    [InlineData(null, CyberArkErrorCategory.Other)]
    [InlineData("", CyberArkErrorCategory.Other)]
    public void Classify_ReturnsExpectedCategory(string? reasonOrMessage, CyberArkErrorCategory expected)
    {
        Assert.Equal(expected, CyberArkErrorClassifier.Classify(reasonOrMessage));
    }

    [Fact]
    public void Classify_IsCaseInsensitiveOnErrorCode()
    {
        Assert.Equal(CyberArkErrorCategory.NotFound, CyberArkErrorClassifier.Classify("appap004e password object not found"));
    }

    [Theory]
    [InlineData(CyberArkErrorCategory.VaultConnectivity, true)]
    [InlineData(CyberArkErrorCategory.PasswordChangeInProgress, true)]
    [InlineData(CyberArkErrorCategory.NotFound, false)]
    [InlineData(CyberArkErrorCategory.AccessDenied, false)]
    [InlineData(CyberArkErrorCategory.AmbiguousQuery, false)]
    [InlineData(CyberArkErrorCategory.Other, false)]
    public void IsTransient_OnlyVaultConnectivityAndPasswordChangeInProgress(CyberArkErrorCategory category, bool expected)
    {
        Assert.Equal(expected, CyberArkErrorClassifier.IsTransient(category));
    }
}
