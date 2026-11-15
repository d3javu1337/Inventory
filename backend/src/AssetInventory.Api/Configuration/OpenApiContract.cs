using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AssetInventory.Api.Configuration;

internal static class OpenApiContract
{
    private const string Bearer = JwtBearerDefaults.AuthenticationScheme;

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[Bearer] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(Bearer, context.Document)] = []
                });
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Unauthorized" });
                operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Forbidden" });
            }
            return Task.CompletedTask;
        });
    }

    public static RouteHandlerBuilder DescribeAssetVersion(this RouteHandlerBuilder endpoint, bool requiresIfMatch = false) =>
        endpoint.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (requiresIfMatch)
            {
                var ifMatch = operation.Parameters!.OfType<OpenApiParameter>()
                    .Single(p => p.In == ParameterLocation.Header && p.Name == "If-Match");
                ifMatch.Required = true;
                ifMatch.Description = "Strong ETag returned by the asset endpoint, a list of entity tags, or *.";
            }
            var response = (OpenApiResponse)operation.Responses!["200"];
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Strong entity tag for the returned asset version.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            };
            return Task.CompletedTask;
        });
}
