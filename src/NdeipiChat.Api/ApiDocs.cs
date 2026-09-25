using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace NdeipiChat.Api;

/// <summary>
/// The OpenAPI document at /openapi/v1.json and Swagger UI at /swagger, in Development only.
/// Endpoints that require authorization are marked as taking a Clerk session token, so the
/// Authorize button in Swagger UI applies it to exactly those calls.
/// </summary>
public static class ApiDocs
{
    const string BearerScheme = "Bearer";

    public static IServiceCollection AddApiDocs(this IServiceCollection services)
    {
        services.AddOpenApi(o =>
        {
            o.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Ndeipi Chat API";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "A Clerk session token."
                };
                return Task.CompletedTask;
            });

            o.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = []
                    });
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static void MapApiDocs(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        app.MapOpenApi();
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("/openapi/v1.json", "Ndeipi Chat API");
            o.DocumentTitle = "Ndeipi Chat API";
        });
    }
}
