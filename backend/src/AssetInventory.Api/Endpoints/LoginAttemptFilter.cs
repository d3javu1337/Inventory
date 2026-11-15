using AssetInventory.Application.Auth;
using AssetInventory.Contracts.Auth;

namespace AssetInventory.Api.Endpoints;

public sealed class LoginAttemptFilter(IAuthAttemptLimiter limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var login = context.Arguments.Select(x => x switch { LoginRequest r => r.Login, LoginStartRequest r => r.Login, SetInitialPasswordRequest r => r.Login, _ => null }).FirstOrDefault(x => x is not null);
        if (context.Arguments.Any(x => x is ChangePasswordRequest))
            login = "password-change:" + context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (login is not null)
        {
            var decision = await limiter.CheckAsync(login, context.HttpContext.RequestAborted);
            if (!decision.Allowed)
            {
                context.HttpContext.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Problem(statusCode: 429, title: "Слишком много попыток входа. Повторите позже.");
            }
        }
        return await next(context);
    }
}
