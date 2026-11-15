using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using AssetInventory.Application;
using AssetInventory.Infrastructure;
using AssetInventory.Infrastructure.Persistence;
using AssetInventory.Api.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
ProductionSecurity.Validate(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Keep framework loopback defaults; add only explicitly trusted proxy addresses, never trust every client.
    foreach (var value in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(IPAddress.Parse(value));
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd("code", "unexpected_error");
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddOpenApi(OpenApiContract.Configure);
builder.Services.AddCors(options => options.AddPolicy("web", policy => policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await app.Services.ApplyDatabaseMigrationsAsync();
    await app.Services.SeedAdministratorAsync(builder.Configuration);
}

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseMiddleware<ApplicationExceptionMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapApiEndpoints();
app.MapHealthEndpoints();

app.Run();

public partial class Program;
