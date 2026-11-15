using AssetInventory.Application.Auth;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace AssetInventory.Api.Endpoints;

public static class AuthEndpoints
{
    private const string RefreshCookie = "asset_inventory_refresh";
    private const string ExpectedUserHeader = "X-Expected-User-Id";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth").WithTags("Auth").AddEndpointFilter<LoginAttemptFilter>();
        auth.MapPost("/mobile/login/start", (LoginStartRequest request, IAuthService service, CancellationToken ct) => service.StartLoginAsync(request, ct));
        auth.MapPost("/mobile/login", (LoginRequest request, IAuthService service, CancellationToken ct) => service.LoginAsync(request, ct));
        auth.MapPost("/mobile/initial-password", (SetInitialPasswordRequest request, IAuthService service, CancellationToken ct) => service.SetInitialPasswordAsync(request, ct));
        auth.MapPost("/mobile/change-password", (ChangePasswordRequest request, System.Security.Claims.ClaimsPrincipal user, IAuthService service, CancellationToken ct) => service.ChangePasswordAsync(InventoryEndpoints.UserId(user), request, ct)).RequireAuthorization();
        auth.MapPost("/mobile/refresh", (RefreshRequest request, [FromHeader(Name = ExpectedUserHeader)] Guid? expectedUserId, IAuthService service, CancellationToken ct) =>
            service.RefreshAsync(request, ct, expectedUserId));
        auth.MapPost("/mobile/logout", async (LogoutRequest request, IAuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(request.RefreshToken, ct);
            return TypedResults.NoContent();
        });
        auth.MapPost("/web/login/start", (LoginStartRequest request, IAuthService service, CancellationToken ct) => service.StartLoginAsync(request, ct));
        auth.MapPost("/web/login", async (LoginRequest request, HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            var pair = await service.LoginAsync(request with { Device = request.Device ?? "web" }, ct);
            SetCookie(context, pair.RefreshToken, pair.RefreshTokenExpiresAt);
            return TypedResults.Ok(new WebSessionResponse(pair.AccessToken, pair.AccessTokenExpiresAt, pair.User));
        });
        auth.MapPost("/web/initial-password", async (SetInitialPasswordRequest request, HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            var pair = await service.SetInitialPasswordAsync(request with { Device = request.Device ?? "web" }, ct);
            SetCookie(context, pair.RefreshToken, pair.RefreshTokenExpiresAt);
            return TypedResults.Ok(new WebSessionResponse(pair.AccessToken, pair.AccessTokenExpiresAt, pair.User));
        });
        auth.MapPost("/web/change-password", async (ChangePasswordRequest request, HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            var pair = await service.ChangePasswordAsync(InventoryEndpoints.UserId(context.User), request with { Device = "web" }, ct, context.Request.Cookies[RefreshCookie]);
            SetCookie(context, pair.RefreshToken, pair.RefreshTokenExpiresAt);
            return TypedResults.Ok(new WebSessionResponse(pair.AccessToken, pair.AccessTokenExpiresAt, pair.User));
        }).RequireAuthorization();
        auth.MapPost("/web/refresh", async Task<Results<Ok<WebSessionResponse>, UnauthorizedHttpResult>> ([FromHeader(Name = ExpectedUserHeader)] Guid? expectedUserId, HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            if (!context.Request.Cookies.TryGetValue(RefreshCookie, out var token)) return TypedResults.Unauthorized();
            var pair = await service.RefreshAsync(new RefreshRequest(token, "web"), ct, expectedUserId);
            SetCookie(context, pair.RefreshToken, pair.RefreshTokenExpiresAt);
            return TypedResults.Ok(new WebSessionResponse(pair.AccessToken, pair.AccessTokenExpiresAt, pair.User));
        });
        auth.MapPost("/web/logout", async ([FromHeader(Name = ExpectedUserHeader)] Guid? expectedUserId, HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            if (context.Request.Cookies.TryGetValue(RefreshCookie, out var token) && !await service.LogoutAsync(token, ct, expectedUserId))
                throw new ConflictException("В браузере открыта сессия другой учетной записи.");
            context.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/v1/auth/web" });
            return TypedResults.NoContent();
        });
        return endpoints;
    }

    private static void SetCookie(HttpContext context, string token, DateTimeOffset expiresAt)
    {
        context.Response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps || !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(), SameSite = SameSiteMode.Strict,
            Expires = expiresAt, Path = "/api/v1/auth/web"
        });
    }
}
