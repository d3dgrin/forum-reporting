using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Reporting.Api.Auth;

/// <summary>
/// !!! LOCAL DEMO AUTHENTICATION — NOT PRODUCTION AUTHENTICATION !!!
/// Trusts whatever user ID the caller puts in the <c>X-Demo-UserId</c> header, so anyone can impersonate anyone.
/// It exists only so the prototype can be demonstrated with several users without an identity provider.
/// Production: the API Gateway / Identity Service issues tokens; this service would validate a JWT bearer token
/// (issuer, audience, signature, expiry) and take the user ID from the <c>sub</c> claim.
/// Program.cs refuses to start with this handler in the Production environment.
/// </summary>
public sealed class DemoHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DemoHeader";
    public const string HeaderName = "X-Demo-UserId";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.Fail($"Exactly one {HeaderName} header is required."));
        }

        var userId = values[0];
        if (!Identifiers.IsValidUserId(userId))
        {
            return Task.FromResult(AuthenticateResult.Fail($"{HeaderName} is not a valid user ID."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId!)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user's ID. The only source of the reporter ID; never the request body.</summary>
    public static string GetRequiredUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated user has no NameIdentifier claim.");
}
