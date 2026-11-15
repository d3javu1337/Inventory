using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetInventory.Api.Endpoints;
using AssetInventory.Api.Middleware;
using AssetInventory.Application.Assets;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure;
using AssetInventory.Infrastructure.Persistence;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class ImportAndDocumentTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Header = "inventoryNumber;name;accountingAccount;serialNumber;manufactureYear;acceptedAt;initialCost;departmentCode;locationCode;molPersonnelNumber\n";

    [Fact]
    public async Task CsvPreviewIsReadOnlyAndQuotedMultilineFieldsCanBeAppliedAndUpdated()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        var importer = s.Services.GetRequiredService<IAssetImportService>();
        var csv = Header + "OS-1;\"Стол; офисный\nс ящиком\";01;;2025;2026-01-01;10,29;D;L;M\n";
        var before = await s.Db.AuditLogs.CountAsync(Ct);
        var preview = await ImportAsync(importer, csv, false);
        Assert.Equal(1, preview.ValidRows); Assert.Equal(0, preview.AppliedRows);
        Assert.Empty(await s.Db.Assets.ToListAsync(Ct));
        Assert.Equal(before, await s.Db.AuditLogs.CountAsync(Ct));
        Assert.Equal(1, (await ImportAsync(importer, csv, true)).AppliedRows);
        var asset = await s.Db.Assets.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("Стол; офисный\nс ящиком", asset.Name);
        Assert.Equal(10.29m, asset.InitialCost);
        await ImportAsync(importer, csv.Replace("10,29", "12.00"), true);
        Assert.Equal(1, await s.Db.Assets.CountAsync(Ct));
        Assert.Equal(12m, (await s.Db.Assets.AsNoTracking().SingleAsync(Ct)).InitialCost);
        Assert.Equal(1, await s.Db.AssetAssignmentHistory.CountAsync(Ct));
    }

    [Fact]
    public async Task ExactAssetCostSurvivesHttpEditAndInventorySnapshot()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        await s.Catalog.CreateUserAsync(new("admin", "password-123", "Administrator", null), Ct);
        var auth = await s.Auth.LoginAsync(new("admin", "password-123", "test"), Ct);
        var department = await s.Db.Departments.SingleAsync(x => x.Code == "D", Ct);
        var location = await s.Db.Locations.SingleAsync(Ct);
        var employee = await s.Db.Employees.SingleAsync(x => x.PersonnelNumber == "M", Ct);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddConfiguration(s.Configuration);
        builder.Logging.ClearProviders();
        builder.Services.AddInfrastructure(s.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseMiddleware<ApplicationExceptionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapAssetEndpoints();
        app.MapInventoryEndpoints();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        const decimal upper = 99999999999999999.99m;
        var payload = new JsonObject
        {
            ["inventoryNumber"] = "EXACT-1", ["name"] = "Исходное ОС", ["accountingAccount"] = "01",
            ["acceptedAt"] = "2026-01-01", ["initialCost"] = "99999999999999999.99",
            ["departmentId"] = department.Id.ToString(), ["locationId"] = location.Id.ToString(),
            ["materiallyResponsibleEmployeeId"] = employee.Id.ToString()
        };
        using var create = await client.PostAsJsonAsync("/api/v1/assets/", payload, Ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(upper, created.GetProperty("initialCost").GetDecimal());
        Assert.Equal("99999999999999999.99", created.GetProperty("initialCostExact").GetString());
        var assetId = created.GetProperty("id").GetGuid();
        var path = $"/api/v1/assets/{assetId}";
        using var current = await client.GetAsync(path, Ct);
        var etag = current.Headers.ETag!;
        client.DefaultRequestHeaders.IfMatch.Add(etag);
        var draft = await s.Inventories.CreateAsync(new("Planned", "AnnualReporting", null, null, DateTimeOffset.UtcNow), auth.User.Id, Ct);
        var active = await s.Inventories.StartAsync(draft.Id, auth.User.Id, Ct);

        payload["name"] = "Уточненное название";
        using var renamed = await client.PutAsJsonAsync(path, payload, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(upper, await s.Db.Assets.AsNoTracking().Where(x => x.Id == assetId).Select(x => x.InitialCost).SingleAsync(Ct));
        client.DefaultRequestHeaders.IfMatch.Clear();
        client.DefaultRequestHeaders.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        payload.Remove("initialCost");
        using var omitted = await client.PutAsJsonAsync(path, payload, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        Assert.Equal(upper, await s.Db.Assets.AsNoTracking().Where(x => x.Id == assetId).Select(x => x.InitialCost).SingleAsync(Ct));

        payload["initialCost"] = upper - 0.01m;
        using var numeric = await client.PutAsJsonAsync(path, payload, Ct);
        Assert.Equal(HttpStatusCode.OK, numeric.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/assets/?search=EXACT-1", Ct);
        Assert.Equal("99999999999999999.98", list.GetProperty("items")[0].GetProperty("initialCostExact").GetString());
        var snapshot = await client.GetFromJsonAsync<JsonElement>($"/api/v1/inventories/{active.Id}?page=1", Ct);
        Assert.Equal("99999999999999999.99", snapshot.GetProperty("items")[0].GetProperty("initialCostExact").GetString());
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task ImportRejectsHeadersDuplicatesPrecisionInactiveMolAndArchivedAssets()
    {
        await using var s = await fixture.OpenAsync(); await SeedAsync(s);
        var importer = s.Services.GetRequiredService<IAssetImportService>();
        await Assert.ThrowsAsync<RequestValidationException>(() => ImportAsync(importer, "bad;header\n", false));
        await Assert.ThrowsAsync<RequestValidationException>(() => ImportAsync(importer, Header, false));
        var row = "OS-1;Стол;01;;2025;2026-01-01;10.00;D;L;M\n";
        Assert.Equal(2, (await ImportAsync(importer, Header + row + row, false)).InvalidRows);
        Assert.Equal(1, (await ImportAsync(importer, Header + row.Replace("10.00", "1.001"), false)).InvalidRows);
        await ImportAsync(importer, Header + row, true);
        await s.Assets.ArchiveAsync((await s.Db.Assets.SingleAsync(Ct)).Id, Ct);
        Assert.Equal(1, (await ImportAsync(importer, Header + row, false)).InvalidRows);
        var mol = await s.Db.Employees.SingleAsync(x => x.PersonnelNumber == "M", Ct);
        mol.IsEmployed = false; await s.Db.SaveChangesAsync(Ct);
        Assert.Equal(1, (await ImportAsync(importer, Header + row.Replace("OS-1", "OS-2"), false)).InvalidRows);
    }

    [Fact]
    public async Task XlsxUsesTypedDatesAndCostsRatherThanLocalizedDisplayStrings()
    {
        await using var s = await fixture.OpenAsync(); await SeedAsync(s);
        using var book = new XLWorkbook(); var sheet = book.Worksheets.Add("ОС");
        var headings = Header.Trim().Split(';');
        for (var i = 0; i < headings.Length; i++) sheet.Cell(1, i + 1).Value = headings[i];
        var values = new[] { "OS-1", "Стол", "01", "", "2025", "", "", "D", "L", "M" };
        for (var i = 0; i < values.Length; i++) sheet.Cell(2, i + 1).Value = values[i];
        sheet.Cell(2, 6).Value = new DateTime(2026, 1, 1); sheet.Cell(2, 6).Style.DateFormat.Format = "dd.MM.yyyy";
        sheet.Cell(2, 7).Value = 10.29; sheet.Cell(2, 7).Style.NumberFormat.Format = "#,##0.00 ₽";
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.xlsx", true, Ct);
        Assert.Equal(1, result.AppliedRows);
        Assert.Equal(10.29m, (await s.Db.Assets.AsNoTracking().SingleAsync(Ct)).InitialCost);
    }

    [Fact]
    public async Task SqlFailureRollsBackAllImportRowsHistoryAndAudit()
    {
        await using var s = await fixture.OpenAsync(); await SeedAsync(s);
        var before = await s.Db.AuditLogs.CountAsync(Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_second_asset() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.inventory_number = 'OS-2' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER fail_second_asset BEFORE INSERT ON assets FOR EACH ROW EXECUTE FUNCTION fail_second_asset();
            """, Ct);
        var importer = s.Services.GetRequiredService<IAssetImportService>();
        var row = "OS-1;Стол;01;;2025;2026-01-01;10.00;D;L;M\n";
        await Assert.ThrowsAsync<DbUpdateException>(() => ImportAsync(importer, Header + row + row.Replace("OS-1", "OS-2"), true));
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await s.Db.AssetAssignmentHistory.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(before, await s.Db.AuditLogs.CountAsync(Ct));
    }

    [Fact]
    public async Task ConfirmationFreezesBothFormatsAndConcurrentDownloadsReturnIdenticalBytes()
    {
        await using var s = await fixture.OpenAsync(); var worker = await SeedAsync(s);
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + "OS-1;Стол;01;SN-1;2025;2026-01-01;100.00;D;L;M\n", true);
        s.Db.Organizations.Add(new Organization { Name = "ООО Кириллица", Inn = "1234567890", Okpo = "00123456" }); await s.Db.SaveChangesAsync(Ct);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        var active = await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        var asset = await s.Db.Assets.SingleAsync(Ct);
        asset.ManufactureYear = 2026;
        await s.Db.SaveChangesAsync(Ct);
        await s.Inventories.ScanAsync(active.Id, new("unknown-qr", "Serviceable", null), worker, Ct);
        await s.Inventories.ScanAsync(active.Id, new(active.Items[0].AssetId.ToString(), "Damaged", "Царапины на столешнице"), worker, Ct);
        await s.Inventories.ConfirmAsync(active.Id, new("password-123"), worker, Ct);
        Assert.Equal(2, await s.Db.GeneratedDocuments.CountAsync(Ct));
        var docs = s.Services.GetRequiredService<IDocumentService>();
        var xlsx = await docs.GetInventoryReportAsync(active.Id, "xlsx", worker, Ct);
        var pdf = await docs.GetInventoryReportAsync(active.Id, "pdf", worker, Ct);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
        using (var book = new XLWorkbook(new MemoryStream(xlsx.Content)))
        {
            var sheet = book.Worksheet("ИНВ-1");
            Assert.Equal("0317001", sheet.Cell("G3").GetString());
            Assert.Equal("ООО Кириллица", sheet.Cell("G4").GetString());
            Assert.Equal("00123456", sheet.Cell("G5").GetString());
            var row = Assert.Single(Inv1AssetRows(sheet));
            Assert.True(row.Cell(3).IsEmpty());
            Assert.True(row.Cell(4).IsEmpty());
            Assert.True(row.Cell(5).IsEmpty());
            Assert.Equal(2025, row.Cell(6).GetValue<int>());
            Assert.Equal("OS-1", row.Cell(7).GetString());
            Assert.Equal("SN-1", row.Cell(8).GetString());
            Assert.True(row.Cell(9).IsEmpty());
            Assert.Equal(1, row.Cell(10).GetValue<int>());
            Assert.Equal(100m, row.Cell(11).GetValue<decimal>());
            Assert.Equal(1, row.Cell(12).GetValue<int>());
            Assert.Equal(100m, row.Cell(13).GetValue<decimal>());
            Assert.Contains("Царапины", book.Worksheet("Расхождения").Cell(2, 5).GetString());
        }
        var org = await s.Db.Organizations.SingleAsync(Ct); org.Name = "Новое название";
        var employee = await s.Db.Employees.SingleAsync(x => x.PersonnelNumber == "W", Ct); employee.FullName = "Другое ФИО";
        asset.Name = "Другой стол"; asset.InitialCost = 999m; await s.Db.SaveChangesAsync(Ct);
        await using (var blockingScope = s.CreateScope())
        {
            var blocker = blockingScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await blocker.Database.BeginTransactionAsync(Ct);
            await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE inventory_asset_snapshots IN ACCESS EXCLUSIVE MODE", Ct);
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM inventories WHERE id = {active.Id} FOR UPDATE", Ct);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            async Task<byte[]> ReadAsync()
            {
                await using var scope = s.CreateScope();
                var reports = scope.ServiceProvider.GetRequiredService<IDocumentService>();
                return (await reports.GetInventoryReportAsync(active.Id, "pdf", worker, readTimeout.Token)).Content;
            }
            Assert.All(await Task.WhenAll(ReadAsync(), ReadAsync(), ReadAsync()), bytes => Assert.Equal(pdf.Content, bytes));
        }
        Assert.Equal(xlsx.Content, (await docs.GetInventoryReportAsync(active.Id, "xlsx", null, Ct)).Content);
        var metadata = await s.Db.GeneratedDocuments.SingleAsync(x => x.Type == DocumentType.InventoryActPdf, Ct);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(pdf.Content)), metadata.Sha256);
        // A stable visual-QA copy, outside the disposable database's storage.
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../tmp/pdfs"));
        Directory.CreateDirectory(output); await File.WriteAllBytesAsync(Path.Combine(output, "inventory-qa.pdf"), pdf.Content, Ct);
    }

    [Fact]
    public async Task RetriedMobileCompletionReturnsTheSameDecisionAndReports()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await SeedAsync(s);
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + "OS-1;Стол;01;;2025;2026-01-01;10.00;D;L;M\n", true);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);

        var finished = await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        var retriedFinish = await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        Assert.Equal("AwaitingConfirmation", retriedFinish.Status);
        Assert.InRange((finished.ScanningFinishedAt!.Value - retriedFinish.ScanningFinishedAt!.Value).Duration().Ticks, 0, 9);
        Assert.Equal("NotFound", Assert.Single(retriedFinish.Items).Result);

        async Task<InventoryDetailResponse> ConfirmAsync()
        {
            await using var scope = s.CreateScope();
            var inventories = scope.ServiceProvider.GetRequiredService<IInventoryService>();
            return await inventories.ConfirmAsync(draft.Id, new("password-123"), worker, Ct);
        }

        var decisions = await Task.WhenAll(ConfirmAsync(), ConfirmAsync());
        Assert.All(decisions, decision => Assert.Equal("Completed", decision.Status));
        Assert.InRange((decisions[0].CompletedAt!.Value - decisions[1].CompletedAt!.Value).Duration().Ticks, 0, 9);
        var signatures = decisions.Select(x => Assert.Single(x.Assignments).ConfirmedAt!.Value).ToArray();
        Assert.InRange((signatures[0] - signatures[1]).Duration().Ticks, 0, 9);

        var reports = await s.Db.GeneratedDocuments.AsNoTracking().OrderBy(x => x.Type).ToArrayAsync(Ct);
        Assert.Equal(2, reports.Length);
        var retriedDecision = await ConfirmAsync();
        Assert.InRange((decisions[0].CompletedAt!.Value - retriedDecision.CompletedAt!.Value).Duration().Ticks, 0, 9);
        var afterRetry = await s.Db.GeneratedDocuments.AsNoTracking().OrderBy(x => x.Type).ToArrayAsync(Ct);
        Assert.Equal(reports.Select(x => (x.Id, x.Sha256)), afterRetry.Select(x => (x.Id, x.Sha256)));

        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Inventories.ConfirmAsync(draft.Id, new("wrong"), worker, Ct));
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Inventories.ConfirmAsync(draft.Id, new("password-123"), TestSession.ActorId, Ct));
        var entityId = draft.Id.ToString();
        Assert.Equal(1, await s.Db.AuditLogs.CountAsync(x => x.EntityId == entityId && x.Action == "inventory.scanning-finished", Ct));
        Assert.Equal(1, await s.Db.AuditLogs.CountAsync(x => x.EntityId == entityId && x.Action == "inventory.confirmed", Ct));
    }

    [Fact]
    public async Task RetriedRejectionKeepsItsReasonAndDoesNotRepeatTheAuditEntry()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await SeedAsync(s);
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + "OS-1;Стол;01;;2025;2026-01-01;10.00;D;L;M\n", true);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);

        const string reason = "Проверить помещение повторно";
        var first = await s.Inventories.RejectAsync(draft.Id, new("password-123", reason), worker, Ct);
        var repeated = await s.Inventories.RejectAsync(draft.Id, new("password-123", $"  {reason}  "), worker, Ct);
        Assert.Equal("ReviewRequired", first.Status);
        Assert.Equal(first.Status, repeated.Status);
        Assert.Equal(reason, Assert.Single(repeated.Assignments).RejectionReason);
        await Assert.ThrowsAsync<UnauthorizedException>(() => s.Inventories.RejectAsync(draft.Id, new("wrong", reason), worker, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => s.Inventories.RejectAsync(draft.Id, new("password-123", "Другая причина"), worker, Ct));
        var entityId = draft.Id.ToString();
        var decisions = s.Db.AuditLogs.Where(x => x.EntityId == entityId && x.Action == "inventory.rejected");
        Assert.Equal(1, await decisions.CountAsync(Ct));

        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        await s.Inventories.RejectAsync(draft.Id, new("password-123", reason), worker, Ct);
        Assert.Equal(2, await decisions.CountAsync(Ct));
    }

    [Fact]
    public async Task BulkFinishRollsBackMissingResultsWhenSavingTheInventoryFails()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await SeedAsync(s);
        const string rows = "OS-1;Стол;01;;2025;2026-01-01;10.00;D;L;M\nOS-2;Стул;01;;2025;2026-01-01;15.00;D;L;M\n";
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + rows, true);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        var active = await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await s.Inventories.ScanAsync(draft.Id, new(active.Items[0].AssetId.ToString(), "Serviceable", null), worker, Ct);
        await s.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_finish_status() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.status = 'AwaitingConfirmation' THEN RAISE EXCEPTION 'test finish failure'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER fail_finish_status BEFORE UPDATE ON inventories FOR EACH ROW EXECUTE FUNCTION fail_finish_status();
            """, Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Inventories.FinishScanningAsync(draft.Id, worker, Ct));

        var saved = await s.Db.Inventories.AsNoTracking().Include(x => x.Items).SingleAsync(x => x.Id == draft.Id, Ct);
        Assert.Equal(InventoryStatus.Active, saved.Status);
        Assert.Null(saved.ScanningFinishedAt);
        Assert.Equal(new[] { ScanResult.Found, ScanResult.Pending }, saved.Items.OrderBy(x => x.InventoryNumber).Select(x => x.Result));
        Assert.False(await s.Db.AuditLogs.AnyAsync(x => x.Action == "inventory.scanning-finished", Ct));
    }

    [Fact]
    public async Task XlsxPreservesCentsEvenAboveSpreadsheetNumericPrecision()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await SeedAsync(s);
        const string rows = "OS-1;Стол;01;;2025;2026-01-01;10.29;D;L;M\nOS-2;Оборудование;01;;2025;2026-01-01;99999999999999999.99;D;L;M\n";
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + rows, true);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        await s.Inventories.ConfirmAsync(draft.Id, new("password-123"), worker, Ct);

        var reports = s.Services.GetRequiredService<IDocumentService>();
        var report = await reports.GetInventoryReportAsync(draft.Id, "xlsx", worker, Ct);
        using var book = new XLWorkbook(new MemoryStream(report.Content));
        var rowsInForm = Inv1AssetRows(book.Worksheet("ИНВ-1"));
        Assert.Equal(XLDataType.Number, rowsInForm[0].Cell(13).DataType);
        Assert.Equal(10.29m, rowsInForm[0].Cell(13).GetValue<decimal>());
        Assert.Equal("99999999999999999.99", rowsInForm[1].Cell(13).GetString());
        Assert.Equal(XLDataType.Text, rowsInForm[1].Cell(13).DataType);
        Assert.All(rowsInForm, row =>
        {
            Assert.Equal(0, row.Cell(10).GetValue<int>());
            Assert.Equal(0m, row.Cell(11).GetValue<decimal>());
        });
    }

    private static async Task<ImportPreviewResponse> ImportAsync(IAssetImportService service, string text, bool apply)
    { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); return await service.ImportAsync(stream, "assets.csv", apply, Ct); }

    private static IXLRow[] Inv1AssetRows(IXLWorksheet sheet) => sheet.RowsUsed()
        .Where(row => row.Cell(1).DataType == XLDataType.Number && row.Cell(7).DataType == XLDataType.Text)
        .ToArray();

    [Fact]
    public async Task Inv1UsesSeparateFormsForDifferentRoomsWithTheSameName()
    {
        await using var s = await fixture.OpenAsync();
        var worker = await SeedAsync(s);
        var location = await s.Db.Locations.SingleAsync(Ct);
        await s.Catalog.CreateLocationAsync(new("L2", location.Name, location.DepartmentId), Ct);
        const string rows = "OS-1;Стол;01;;;2026-01-01;10.29;D;L;M\nOS-2;Стул;01;;2025;2026-01-01;11.30;D;L2;M\n";
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + rows, true);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        await s.Inventories.ConfirmAsync(draft.Id, new("password-123"), worker, Ct);

        var docs = s.Services.GetRequiredService<IDocumentService>();
        var xlsx = await docs.GetInventoryReportAsync(draft.Id, "xlsx", worker, Ct);
        using var book = new XLWorkbook(new MemoryStream(xlsx.Content));
        var formRows = new[] { "ИНВ-1", "ИНВ-1 (2)" }
            .Select(name => Assert.Single(Inv1AssetRows(book.Worksheet(name))))
            .OrderBy(row => row.Cell(7).GetString()).ToArray();
        Assert.Equal(new[] { "OS-1", "OS-2" }, formRows.Select(row => row.Cell(7).GetString()));
        Assert.All(formRows, row => Assert.Equal(1, row.Cell(1).GetValue<int>()));
        Assert.True(formRows[0].Cell(6).IsEmpty());
        Assert.Equal(2025, formRows[1].Cell(6).GetValue<int>());
    }

    [Fact]
    public async Task LongQrLabelAndMultipageCyrillicReportRenderAndTamperedFilesAreRejected()
    {
        await using var s = await fixture.OpenAsync(); var worker = await SeedAsync(s);
        var rows = Enumerable.Range(1, 200).Select(i => $"OS-{i:000};Основное средство с подробным длинным русским наименованием номер {i};01;;2025;2026-01-01;100.00;D;L;M");
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + string.Join('\n', rows), true);
        var asset = await s.Db.Assets.FirstAsync(Ct); asset.InventoryNumber = new string('W', 100); asset.Name = new string('Ж', 500); await s.Db.SaveChangesAsync(Ct);
        var docs = s.Services.GetRequiredService<IDocumentService>();
        var label = await docs.GetQrAsync(asset.Id, Ct);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, label.Content.Take(8).ToArray());
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        await s.Inventories.ConfirmAsync(draft.Id, new("password-123"), worker, Ct);
        var report = await docs.GetInventoryReportAsync(draft.Id, "pdf", worker, Ct);
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../tmp/pdfs")); Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, "inventory-multipage.pdf"), report.Content, Ct);
        var spreadsheet = await docs.GetInventoryReportAsync(draft.Id, "xlsx", worker, Ct);
        using (var book = new XLWorkbook(new MemoryStream(spreadsheet.Content)))
        {
            var sheet = book.Worksheet("ИНВ-1");
            var formRows = Inv1AssetRows(sheet);
            Assert.Equal(200, formRows.Length);
            Assert.Equal(Enumerable.Range(1, 200), formRows.Select(row => row.Cell(1).GetValue<int>()));
            Assert.True(formRows[0].Cell(2).Style.Alignment.WrapText);
            Assert.True(formRows[^1].Height > 100);
            Assert.Equal(20000m, formRows.Sum(row => row.Cell(13).GetValue<decimal>()));
            Assert.Equal(formRows[0].Cell(7).GetString(), book.Worksheet("Расхождения").Cell("A2").GetString());
            Assert.True(sheet.PageSetup.RowBreaks.Count > 2);
            Assert.Equal(sheet.PageSetup.RowBreaks.Count - 1,
                sheet.Column(1).CellsUsed().Count(cell => cell.GetString() == "Итого по странице:"));
        }
        await File.WriteAllBytesAsync(Path.Combine(output, "inventory-multipage.xlsx"), spreadsheet.Content, Ct);
        await File.WriteAllBytesAsync(Path.Combine(output, "qr-long.png"), label.Content, Ct);
        var metadata = await s.Db.GeneratedDocuments.SingleAsync(x => x.Type == DocumentType.InventoryActPdf, Ct);
        var path = Path.Combine(s.Configuration["FileStorage:RootPath"]!, metadata.StorageKey);
        await File.WriteAllTextAsync(path, "changed", Ct);
        await Assert.ThrowsAsync<AssetInventory.Application.Common.ConflictException>(() => docs.GetInventoryReportAsync(draft.Id, "pdf", worker, Ct));
        File.Delete(path);
        await Assert.ThrowsAsync<AssetInventory.Application.Common.ConflictException>(() => docs.GetInventoryReportAsync(draft.Id, "pdf", worker, Ct));
    }

    [Fact]
    public async Task ThousandAssetsAndMaximumCommentsKeepAllTextInBothReports()
    {
        await using var s = await fixture.OpenAsync(); var worker = await SeedAsync(s);
        var rows = Enumerable.Range(1, 1000).Select(i => $"LOAD-{i:0000};Тестовое устройство {i};01;;2025;2026-01-01;10.29;D;L;M");
        await ImportAsync(s.Services.GetRequiredService<IAssetImportService>(), Header + string.Join('\n', rows), true);
        var draft = await s.Inventories.CreateAsync(new("Manual", "AnnualReporting", null, null, DateTimeOffset.UtcNow), TestSession.ActorId, Ct);
        var active = await s.Inventories.StartAsync(draft.Id, TestSession.ActorId, Ct);
        var continuous = new string('Ж', 1985) + " КОНЕЦ-ПЕРВОГО";
        var multiline = string.Concat(Enumerable.Repeat("Строка\n", 280)) + "КОНЕЦ-ВТОРОГО";
        await s.Inventories.ScanAsync(draft.Id, new(active.Items[0].AssetId.ToString(), "Damaged", continuous), worker, Ct);
        await s.Inventories.ScanAsync(draft.Id, new(active.Items[1].AssetId.ToString(), "Unusable", multiline), worker, Ct);
        await s.Inventories.FinishScanningAsync(draft.Id, worker, Ct);
        var reportTimer = System.Diagnostics.Stopwatch.StartNew();
        await s.Inventories.ConfirmAsync(draft.Id, new("password-123"), worker, Ct);
        reportTimer.Stop();
        var docs = s.Services.GetRequiredService<IDocumentService>();
        var xlsx = await docs.GetInventoryReportAsync(draft.Id, "xlsx", worker, Ct);
        var pdf = await docs.GetInventoryReportAsync(draft.Id, "pdf", worker, Ct);
        using (var book = new XLWorkbook(new MemoryStream(xlsx.Content)))
        {
            var sheet = book.Worksheet("ИНВ-1");
            var formRows = Inv1AssetRows(sheet);
            Assert.Equal(1000, formRows.Length);
            Assert.Equal(10290m, formRows.Sum(row => row.Cell(13).GetValue<decimal>()));
            Assert.Equal(2025, formRows[0].Cell(6).GetValue<int>());
            Assert.Equal("10.29", formRows[0].Cell(13).GetFormattedString(CultureInfo.InvariantCulture));
            Assert.Equal(2, formRows.Sum(row => row.Cell(10).GetValue<int>()));
            Assert.Equal(20.58m, formRows.Sum(row => row.Cell(11).GetValue<decimal>()));
            Assert.Contains("Комментарии", book.Worksheet("Расхождения").Cell("E2").GetString());
            var comments = book.Worksheet("Комментарии");
            foreach (var (number, expected) in new[] { (active.Items[0].InventoryNumber, continuous), (active.Items[1].InventoryNumber, multiline) })
                Assert.Equal(expected, string.Concat(comments.RowsUsed().Skip(1).Where(row => row.Cell(1).GetString() == number).Select(row => row.Cell(3).GetString())));
            Assert.All(comments.RowsUsed(), row => Assert.InRange(row.Height, 20, 200));
        }
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../tmp/pdfs")); Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, "inventory-1000.xlsx"), xlsx.Content, Ct);
        await File.WriteAllBytesAsync(Path.Combine(output, "inventory-1000.pdf"), pdf.Content, Ct);
        await File.WriteAllTextAsync(Path.Combine(output, "inventory-1000-performance.json"),
            System.Text.Json.JsonSerializer.Serialize(new { assets = 1000, confirmationSeconds = reportTimer.Elapsed.TotalSeconds, xlsxBytes = xlsx.Content.Length, pdfBytes = pdf.Content.Length }), Ct);
    }

    private static async Task<Guid> SeedAsync(TestSession s)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("D", "Имущество", false), Ct);
        await s.Catalog.CreateLocationAsync(new("L", "Кабинет", dep.Id), Ct);
        await s.Catalog.CreateEmployeeAsync(new("M", "МОЛ", dep.Id, true, true), Ct);
        var workerDep = await s.Catalog.CreateDepartmentAsync(new("INV", "Инвентаризация", true), Ct);
        var employee = await s.Catalog.CreateEmployeeAsync(new("W", "Иванов Иван Иванович", workerDep.Id, false, true), Ct);
        return (await s.Catalog.CreateUserAsync(new("worker", "password-123", "InventoryEmployee", employee.Id), Ct)).Id;
    }
}
