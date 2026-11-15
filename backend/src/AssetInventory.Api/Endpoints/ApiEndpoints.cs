using AssetInventory.Contracts.System;

namespace AssetInventory.Api.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthEndpoints();
        endpoints.MapCatalogEndpoints();
        endpoints.MapAssetEndpoints();
        endpoints.MapInventoryEndpoints();
        var api = endpoints.MapGroup("/api/v1")
            .WithTags("System");

        api.MapGet("/", (IHostEnvironment environment) =>
                TypedResults.Ok(new ApiInfoResponse(
                    "Asset Inventory API",
                    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    environment.EnvironmentName)))
            .WithName("GetApiInfo")
            .WithSummary("Returns API metadata.");

        return endpoints;
    }
}
