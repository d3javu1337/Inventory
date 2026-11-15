using AssetInventory.Application.Common;

namespace AssetInventory.Api.Middleware;

public sealed class ApplicationExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (AssetInventory.Application.Common.ApplicationException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            await Results.Problem(statusCode: exception.StatusCode, title: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        }
    }
}
