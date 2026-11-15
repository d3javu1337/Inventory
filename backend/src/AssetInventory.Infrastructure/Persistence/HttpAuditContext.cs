using System.Security.Claims;
using AssetInventory.Application.Common;
using Microsoft.AspNetCore.Http;

namespace AssetInventory.Infrastructure.Persistence;

internal sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    public Guid? UserId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
