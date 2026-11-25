using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reporting.Api.Auth;

/// <summary>
/// Adds the demo <c>X-Demo-UserId</c> header as an API-key security scheme, so Swagger UI shows an
/// "Authorize" button: enter e.g. <c>reporter-1</c> once and every request is sent as that user.
/// </summary>
public sealed class DemoHeaderOpenApiTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[DemoHeaderAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = DemoHeaderAuthenticationHandler.HeaderName,
            Description = "DEMO authentication only: the user ID to act as, e.g. reporter-1 (target-1 wrote post-1..3).",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(DemoHeaderAuthenticationHandler.SchemeName, document)] = [],
        });
        return Task.CompletedTask;
    }
}
