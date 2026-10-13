using System.Text;
using ITfoxtec.Identity.Saml2.MvcCore;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// Found live 2026-10-13: the ACS read Authentik's POST with
/// ToGenericHttpRequestAsync(readBodyAsString: true), which leaves Form empty, so
/// Saml2PostBinding.Read threw a NullReferenceException looking for SAMLResponse
/// (a 500 after every SAML sign-in). The POST binding needs the form read.
/// </summary>
public class Saml2AcsRequestReadingTests
{
    private static HttpRequest FormPost(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context.Request;
    }

    [Fact]
    public async Task ReadingTheFormKeepsSamlResponse()
    {
        var generic = await FormPost("SAMLResponse=PHNhbWxwOlJlc3BvbnNlLz4%3D&RelayState=x").ToGenericHttpRequestAsync(validate: true);

        Assert.NotNull(generic.Form);
        Assert.Equal("PHNhbWxwOlJlc3BvbnNlLz4=", generic.Form["SAMLResponse"]);
    }

    [Fact]
    public async Task ReadingTheBodyAsAString_LeavesNoForm_WhichIsWhatBrokeTheAcs()
    {
        var generic = await FormPost("SAMLResponse=PHNhbWxwOlJlc3BvbnNlLz4%3D").ToGenericHttpRequestAsync(readBodyAsString: true, validate: true);

        Assert.True(generic.Form is null || generic.Form["SAMLResponse"] is null);
    }
}
