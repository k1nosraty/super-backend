using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SuperBackend.Api;

internal static class ApiOpenApi
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "SuperBackend API";
            document.Info.Version = "v1";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Access token returned by the Identity login endpoint."
            };
            document.Components.SecuritySchemes["OidcExchange"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = "SuperBackend.OidcExchange",
                In = ParameterLocation.Cookie,
                Description = "Short-lived browser ticket issued by the successful provider callback."
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            var path = context.Description.RelativePath?.TrimEnd('/');
            var scheme = path is "api/v1/oidc/session" or "api/v1/oidc/exchange" ? "OidcExchange"
                : context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any() ? null : "Bearer";
            if (scheme is not null)
            {
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(scheme, context.Document)] = []
                }];
            }

            if (path == "api/v1/oidc/exchange")
                AddHeader(operation, "Origin", required: true, "The configured first-party HTTPS origin.");
            if (path == "api/v1/notes" && context.Description.HttpMethod == "POST")
                AddHeader(operation, "Idempotency-Key", required: false, "A stable request key scoped to the note owner.");
            if (path == "api/v1/tenants/{tenantId}/documents/{id}" && context.Description.HttpMethod == "PUT")
                AddHeader(operation, "If-Match", required: true, "The strong ETag returned by GET.");
            return Task.CompletedTask;
        });
    }

    private static void AddHeader(OpenApiOperation operation, string name, bool required, string description)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
