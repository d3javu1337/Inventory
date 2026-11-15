using Microsoft.OpenApi;

namespace AssetInventory.Api.Endpoints;

internal static class FileResponseMetadata
{
    public static RouteHandlerBuilder ProducesFile(this RouteHandlerBuilder endpoint, params string[] mediaTypes) =>
        endpoint.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Responses!["200"] = new OpenApiResponse
            {
                Description = "OK",
                Content = mediaTypes.ToDictionary(
                    mediaType => mediaType,
                    _ => new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                    })
            };
            return Task.CompletedTask;
        });
}
