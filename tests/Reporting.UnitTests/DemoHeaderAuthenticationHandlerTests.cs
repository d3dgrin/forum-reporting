using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Reporting.Api.Auth;

namespace Reporting.UnitTests;

public class DemoHeaderAuthenticationHandlerTests
{
    private static async Task<AuthenticateResult> AuthenticateAsync(params string[] headerValues)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication(DemoHeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DemoHeaderAuthenticationHandler>(DemoHeaderAuthenticationHandler.SchemeName, null);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (headerValues.Length > 0)
        {
            context.Request.Headers[DemoHeaderAuthenticationHandler.HeaderName] = headerValues;
        }

        return await context.AuthenticateAsync(DemoHeaderAuthenticationHandler.SchemeName);
    }

    [Fact]
    public async Task Missing_header_yields_no_result()
    {
        var result = await AuthenticateAsync();

        result.None.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("reporter 1")]
    [InlineData("reporter/1")]
    public async Task Invalid_user_id_fails(string value)
    {
        var result = await AuthenticateAsync(value);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task Too_long_user_id_fails()
    {
        var result = await AuthenticateAsync(new string('a', 65));

        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task Multiple_header_values_fail()
    {
        var result = await AuthenticateAsync("reporter-1", "reporter-2");

        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task Valid_header_authenticates_with_the_exact_user_id()
    {
        var result = await AuthenticateAsync("Reporter-1");

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier).ShouldBe("Reporter-1"); // opaque, not normalised
        result.Principal!.GetRequiredUserId().ShouldBe("Reporter-1");
    }
}
