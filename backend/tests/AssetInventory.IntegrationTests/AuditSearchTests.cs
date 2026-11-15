using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Common;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class AuditSearchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchUsesHistoricalNamesActorsInventoryNumbersAndCompleteBusinessDaysBeforePaging()
    {
        await using var s = await fixture.OpenAsync();
        var dep = await s.Catalog.CreateDepartmentAsync(new("INV", "Инвентаризация", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("W", "Иванов Иван", dep.Id, false, true), Ct);
        var worker = await s.Catalog.CreateUserAsync(new("audit-worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        var admin = await s.Catalog.CreateUserAsync(new("audit-admin", "password-123", "Administrator", null), Ct);
        var inventory = new Inventory { Number = "INV-AUDIT-001", Type = InventoryType.Manual, Reason = InventoryReason.Other, PlannedAt = DateTimeOffset.UtcNow };
        s.Db.Inventories.Add(inventory);
        var start = new DateTimeOffset(2026, 11, 4, 19, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 45; i++) s.Db.AuditLogs.Add(new AuditLog {
            Action = "inventory.started", EntityType = nameof(Inventory), EntityId = inventory.Id.ToString(),
            UserId = i % 2 == 0 ? worker.Id : admin.Id, CreatedAt = start.AddMinutes(i),
            DataJson = JsonSerializer.Serialize(new { displayName = i == 0 ? "Старое имя объекта" : "Обычная запись" }),
            IpAddress = "127.10.20.30", CorrelationId = $"request-{i}",
        });
        foreach (var time in new[] { start.AddTicks(-10), start.AddDays(1).AddTicks(-10), start.AddDays(1) })
            s.Db.AuditLogs.Add(new AuditLog { Action = "inventory.started", EntityType = nameof(Inventory), EntityId = inventory.Id.ToString(), UserId = worker.Id, CreatedAt = time, CorrelationId = time == start.AddDays(1).AddTicks(-10) ? "trace-last-minute" : null });
        await s.Db.SaveChangesAsync(Ct);
        var query = new AuditListQuery(3, 20, "INV-AUDIT", "inventory.started", nameof(Inventory), inventory.Id.ToString().ToUpperInvariant(), DateFrom: new(2026, 11, 5), DateTo: new(2026, 11, 5));
        var page = await s.Monitoring.GetAuditAsync(query, Ct);
        Assert.Equal(46, page.TotalCount);
        Assert.Equal(6, page.Items.Count);
        Assert.All(page.Items, log => Assert.Equal(inventory.Number, log.EntityDisplayName));
        var first = await s.Monitoring.GetAuditAsync(query with { Page = 1 }, Ct);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(page.Items.Select(x => x.Id)));
        Assert.Equal(3, (await s.Monitoring.GetAuditAsync(query with { Page = 999 }, Ct)).Page);
        Assert.Equal(24, (await s.Monitoring.GetAuditAsync(query with { Page = 1, UserId = worker.Id }, Ct)).TotalCount);
        Assert.Equal(24, (await s.Monitoring.GetAuditAsync(query with { Page = 1, Search = "ИВАНОВ" }, Ct)).TotalCount);
        Assert.Single((await s.Monitoring.GetAuditAsync(new(Search: "старое ИМЯ", Action: "inventory.started"), Ct)).Items);
        Assert.Single((await s.Monitoring.GetAuditAsync(new(Search: "trace-last-minute"), Ct)).Items);
        Assert.Equal(22, (await s.Monitoring.GetAuditAsync(new(Search: "AUDIT-ADMIN", Action: "inventory.started"), Ct)).TotalCount);
        Assert.Empty((await s.Monitoring.GetAuditAsync(query with { EntityId = Guid.NewGuid().ToString() }, Ct)).Items);
    }

    [Fact]
    public async Task SystemFilterIncludesLegacyEmptyAndNullUsersAndSearchTreatsWildcardsLiterally()
    {
        await using var s = await fixture.OpenAsync();
        var logs = new[] { "Код_100%", "Код-1000", "Системная запись" }.Select(name => new AuditLog { Action = "qa.test", EntityType = nameof(Asset), EntityId = Guid.CreateVersion7().ToString(), UserId = Guid.Empty, DataJson = JsonSerializer.Serialize(new { displayName = name }) }).ToArray();
        s.Db.AuditLogs.AddRange(logs);
        await s.Db.SaveChangesAsync(Ct);
        // Model historical system rows in the disposable fixture without the
        // fixture's request audit context filling in an actor.
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit_logs SET user_id = NULL WHERE id = {logs[2].Id}", Ct);
        Assert.Equal(3, (await s.Monitoring.GetAuditAsync(new(Action: "qa.test", UserId: Guid.Empty), Ct)).TotalCount);
        Assert.Equal(logs[0].Id, Assert.Single((await s.Monitoring.GetAuditAsync(new(Search: "КОД_100%"), Ct)).Items).Id);
        Assert.Single((await s.Monitoring.GetAuditAsync(new(Search: "%"), Ct)).Items);
        Assert.Empty((await s.Monitoring.GetAuditAsync(new(Search: "' OR true --"), Ct)).Items);
    }

    [Fact]
    public async Task InvalidFiltersAreRejected()
    {
        await using var s = await fixture.OpenAsync();
        foreach (var query in new[] { new AuditListQuery(Page: 0), new(PageSize: 201), new(Search: new string('a', 201)), new(Action: new string('a', 101)), new(DateFrom: new(2026, 11, 6), DateTo: new(2026, 11, 5)) })
            await Assert.ThrowsAsync<RequestValidationException>(() => s.Monitoring.GetAuditAsync(query, Ct));
    }

    [Fact]
    public async Task HttpAuditFiltersRequireAdministratorAndBindCalendarDates()
    {
        await using var s = await fixture.OpenAsync();
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        var department = await s.Catalog.CreateDepartmentAsync(new("INV", "Инвентаризация", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("WORKER", "Сотрудник", department.Id, false, true), Ct);
        await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct);
        s.Db.AuditLogs.Add(new AuditLog { Action = "qa.test", EntityType = nameof(Asset), UserId = Guid.Empty, CreatedAt = new(2026, 11, 4, 19, 0, 0, TimeSpan.Zero) });
        await s.Db.SaveChangesAsync(Ct);
        var admin = await s.Auth.LoginAsync(new("admin", "password-123", null), Ct);
        var worker = await s.Auth.LoginAsync(new("worker", "password-123", null), Ct);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddConfiguration(s.Configuration);
        builder.Services.AddInfrastructure(s.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization(); app.MapInventoryEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        const string path = "/api/v1/audit?action=qa.test&dateFrom=2026-11-05&dateTo=2026-11-05";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", worker.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path, Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        var response = (await client.GetFromJsonAsync<PagedResponse<AuditLogResponse>>(path, Ct))!;
        Assert.Single(response.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/audit?dateFrom=2026-11-06&dateTo=2026-11-05", Ct)).StatusCode);
        await app.StopAsync(Ct);
    }
}
