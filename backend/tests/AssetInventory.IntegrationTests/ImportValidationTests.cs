using System.IO.Compression;
using System.Text;
using AssetInventory.Application.Assets;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Inventories;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetInventory.IntegrationTests;

public sealed class ImportValidationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Header = "inventoryNumber;name;accountingAccount;serialNumber;manufactureYear;acceptedAt;initialCost;departmentCode;locationCode;molPersonnelNumber\n";
    private sealed record References(Guid Department, Guid Location, Guid Employee);

    private static string Row(string number, string department = "DEPT", string location = "ROOM", string employee = "EMP") =>
        $"{number};Стол;01;;2025;2026-01-01;100.00;{department};{location};{employee}\n";

    private static async Task<References> SeedAsync(TestSession s)
    {
        var dep = await s.Catalog.CreateDepartmentAsync(new("DEPT", "Отдел", false), Ct);
        var location = await s.Catalog.CreateLocationAsync(new("ROOM", "Комната", dep.Id), Ct);
        var mol = await s.Catalog.CreateEmployeeAsync(new("EMP", "МОЛ", dep.Id, true, true), Ct);
        return new(dep.Id, location.Id, mol.Id);
    }

    private static async Task<References> AddCaseVariantAsync(TestSession s, string kind, References first)
    {
        if (kind == "department") {
            var dep = await s.Catalog.CreateDepartmentAsync(new("dept", "Другой отдел", false), Ct);
            var location = await s.Catalog.CreateLocationAsync(new("ROOM", "Другая комната", dep.Id), Ct);
            return first with { Department = dep.Id, Location = location.Id };
        }
        if (kind == "location") return first with { Location = (await s.Catalog.CreateLocationAsync(new("room", "Другая комната", first.Department), Ct)).Id };
        return first with { Employee = (await s.Catalog.CreateEmployeeAsync(new("emp", "Другой МОЛ", first.Department, true, true), Ct)).Id };
    }

    private static async Task<ImportPreviewResponse> CsvAsync(TestSession s, string contents, bool apply)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents));
        return await s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.csv", apply, Ct);
    }

    [Fact]
    public async Task BrokenCsvQuotesReportThePhysicalLineAfterMultilineData()
    {
        await using var s = await fixture.OpenAsync();
        var multiline = Row("OS-1").Replace("Стол", "\"первая\nвторая\"");
        var malformed = Row("OS-2").Replace("Стол", "\"закрытые кавычки\"лишнее");
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => CsvAsync(s, Header + multiline + malformed, false));
        Assert.Contains("строка 4", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("кавыч", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidRowStillMarksItsValidCounterpartAsADuplicate()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        var csv = Header + Row("OS-1") + Row("os-1").Replace("100.00", "дорого") + Row("OS-2");
        var preview = await CsvAsync(s, csv, false);
        Assert.Equal(1, preview.ValidRows);
        Assert.Contains("Дублирующийся", preview.Rows[0].Error);
        Assert.Contains("Стоимость", preview.Rows[1].Error);
        Assert.True(preview.Rows[2].IsValid);
        await Assert.ThrowsAsync<RequestValidationException>(() => CsvAsync(s, csv, true));
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task NullCharacterIsAValidationErrorBeforeWritingAnyRows()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        var csv = Header + Row("OS-1") + Row("OS-2").Replace("Стол", "Ст\0ол");
        var preview = await CsvAsync(s, csv, false);
        Assert.Equal(1, preview.ValidRows);
        Assert.Contains("нулевой символ", preview.Rows[1].Error);
        await Assert.ThrowsAsync<RequestValidationException>(() => CsvAsync(s, csv, true));
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
    }

    [Theory]
    [InlineData("department")]
    [InlineData("location")]
    [InlineData("employee")]
    public async Task ExactReferenceCodesDistinguishRowsDifferingOnlyInCase(string kind)
    {
        await using var s = await fixture.OpenAsync();
        var first = await SeedAsync(s);
        var second = await AddCaseVariantAsync(s, kind, first);
        var csv = Header + Row("OS-1") + Row("OS-2", kind == "department" ? "dept" : "DEPT", kind == "location" ? "room" : "ROOM", kind == "employee" ? "emp" : "EMP");
        var preview = await CsvAsync(s, csv, false);
        Assert.Equal(2, preview.ValidRows); Assert.Equal(0, preview.InvalidRows);
        Assert.Equal(2, (await CsvAsync(s, csv, true)).AppliedRows);
        var rows = await s.Db.Assets.AsNoTracking().OrderBy(x => x.InventoryNumber).ToListAsync(Ct);
        Assert.Equal(first, new References(rows[0].DepartmentId, rows[0].LocationId, rows[0].MateriallyResponsibleEmployeeId));
        Assert.Equal(second, new References(rows[1].DepartmentId, rows[1].LocationId, rows[1].MateriallyResponsibleEmployeeId));
    }

    [Theory]
    [InlineData("department")]
    [InlineData("location")]
    [InlineData("employee")]
    public async Task AmbiguousCaseInsensitiveReferenceIsARowErrorAndPreventsApply(string kind)
    {
        await using var s = await fixture.OpenAsync();
        await AddCaseVariantAsync(s, kind, await SeedAsync(s));
        var csv = Header + Row("OS-1", kind == "department" ? "Dept" : "DEPT", kind == "location" ? "Room" : "ROOM", kind == "employee" ? "Emp" : "EMP") + Row("OS-2");
        var auditCount = await s.Db.AuditLogs.CountAsync(Ct);
        var preview = await CsvAsync(s, csv, false);
        Assert.Equal(1, preview.ValidRows); Assert.Equal(1, preview.InvalidRows);
        Assert.Contains("неоднознач", preview.Rows.Single(x => !x.IsValid).Error!.ToLowerInvariant());
        await Assert.ThrowsAsync<RequestValidationException>(() => CsvAsync(s, csv, true));
        Assert.Empty(await s.Db.Assets.ToListAsync(Ct));
        Assert.Equal(auditCount, await s.Db.AuditLogs.CountAsync(Ct));
    }

    [Fact]
    public async Task UnambiguousReferenceStillSupportsCaseInsensitiveLookup()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        Assert.Equal(1, (await CsvAsync(s, Header + Row("OS-1", "dept", "room", "emp"), true)).AppliedRows);
    }

    [Theory]
    [InlineData("department")]
    [InlineData("location")]
    [InlineData("employee")]
    public async Task ArchivedExactCodeNeverFallsBackToAnotherActiveReference(string kind)
    {
        await using var s = await fixture.OpenAsync();
        var first = await SeedAsync(s);
        var second = await AddCaseVariantAsync(s, kind, first);
        if (kind == "location") await s.Catalog.ArchiveLocationAsync(first.Location, Ct);
        else if (kind == "employee") await s.Catalog.ArchiveEmployeeAsync(first.Employee, Ct);
        else {
            await s.Catalog.ArchiveLocationAsync(first.Location, Ct);
            await s.Catalog.ArchiveEmployeeAsync(first.Employee, Ct);
            await s.Catalog.ArchiveDepartmentAsync(first.Department, Ct);
            await s.Catalog.CreateEmployeeAsync(new("emp", "Другой МОЛ", second.Department, true, true), Ct);
        }
        var csv = Header + Row("OS-1");
        var result = await CsvAsync(s, csv, false);
        Assert.Equal(1, result.InvalidRows); Assert.Equal(0, result.ValidRows);
        Assert.Contains("архив", Assert.Single(result.Rows).Error!.ToLowerInvariant());
        await Assert.ThrowsAsync<RequestValidationException>(() => CsvAsync(s, csv, true));
        Assert.Empty(await s.Db.Assets.ToListAsync(Ct));
    }

    private static MemoryStream Workbook(bool extraHeader = false, bool extraData = false, bool overflowingCost = false,
        bool fractionalCost = false, bool largeNumericCost = false, bool largeTextCost = false)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("ОС");
        var headings = Header.Trim().Split(';');
        for (var column = 0; column < headings.Length; column++) sheet.Cell(1, column + 1).Value = headings[column];
        if (extraHeader) sheet.Cell(1, 11).Value = "unexpected";
        foreach (var index in new[] { 2, 3 }) {
            var values = Row("OS-" + index).TrimEnd('\n').Split(';');
            for (var column = 0; column < values.Length; column++) sheet.Cell(index, column + 1).Value = values[column];
        }
        if (extraData) sheet.Cell(3, 12).Value = "Нельзя молча отбросить";
        if (overflowingCost) sheet.Cell(3, 7).Value = 1e100;
        if (fractionalCost) sheet.Cell(3, 7).Value = 0.1 + 0.2;
        if (largeNumericCost) sheet.Cell(2, 7).Value = 10000000000000.01d;
        if (largeTextCost) sheet.Cell(3, 7).Value = "99999999999999999.99";
        var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task LargeXlsxCostsRequireTextWithoutLosingKopecks()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        var importer = s.Services.GetRequiredService<IAssetImportService>();
        using var mixed = Workbook(largeNumericCost: true, largeTextCost: true);
        var preview = await importer.ImportAsync(mixed, "assets.xlsx", false, Ct);
        var error = Assert.Single(preview.Rows, row => !row.IsValid);
        Assert.Equal(2, error.RowNumber);
        Assert.Contains("текст", error.Error!);
        Assert.True(preview.Rows[1].IsValid);
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
        using var corrected = Workbook(largeTextCost: true);
        Assert.Equal(2, (await importer.ImportAsync(corrected, "assets.xlsx", true, Ct)).AppliedRows);
        Assert.Equal(99999999999999999.99m, (await s.Db.Assets.AsNoTracking().SingleAsync(x => x.InventoryNumber == "OS-3", Ct)).InitialCost);
    }

    [Fact]
    public async Task CsvDoesNotRoundFractionsBelowDecimalPrecisionDownToZero()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        var csv = Header + Row("OS-1").Replace("100.00", "0.0000000000000000000000000000001") + Row("OS-2");
        var preview = await CsvAsync(s, csv, false);
        Assert.Equal(1, preview.InvalidRows);
        Assert.Equal("OS-1", Assert.Single(preview.Rows, row => !row.IsValid).InventoryNumber);
        await Assert.ThrowsAsync<RequestValidationException>(() => CsvAsync(s, csv, true));
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task XlsxRejectsAnOversizedExpandedArchiveBeforeParsingRows()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        using var original = Workbook();
        using var template = new ZipArchive(original, ZipArchiveMode.Read);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in template.Entries)
            {
                await using var source = entry.Open();
                await using var destination = archive.CreateEntry(entry.FullName).Open();
                await source.CopyToAsync(destination, Ct);
            }
            var extra = archive.CreateEntry("unused/oversized.bin", CompressionLevel.Fastest);
            await using var content = extra.Open();
            var buffer = new byte[1024 * 1024];
            for (var i = 0; i < 129; i++) await content.WriteAsync(buffer, Ct);
        }
        Assert.True(stream.Length < 20 * 1024 * 1024);
        stream.Position = 0;
        var importer = s.Services.GetRequiredService<IAssetImportService>();
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => importer.ImportAsync(stream, "assets.xlsx", true, Ct));
        Assert.Contains("после распаковки", error.Message);
        Assert.Empty(await s.Db.Assets.AsNoTracking().ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtraDataColumnsAreNotSilentlyDiscarded(bool xlsx)
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        async Task<ImportPreviewResponse> Read(bool apply)
        {
            if (!xlsx) return await CsvAsync(s, Header + Row("OS-1") + Row("OS-2").TrimEnd('\n') + ";extra\n", apply);
            using var stream = Workbook(extraData: true);
            return await s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.xlsx", apply, Ct);
        }
        var preview = await Read(false);
        Assert.Equal(1, preview.ValidRows); Assert.Equal(1, preview.InvalidRows);
        Assert.Equal(3, preview.Rows.Single(x => !x.IsValid).RowNumber);
        Assert.Contains("столбц", preview.Rows.Single(x => !x.IsValid).Error!.ToLowerInvariant());
        await Assert.ThrowsAsync<RequestValidationException>(() => Read(true));
        Assert.Empty(await s.Db.Assets.ToListAsync(Ct));
    }

    [Fact]
    public async Task ExtraXlsxHeaderIsRejectedAsAChangedTemplate()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        using var stream = Workbook(extraHeader: true);
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.xlsx", false, Ct));
    }

    [Fact]
    public async Task OverflowingXlsxCostIsARowErrorRatherThanACorruptFile()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        using var stream = Workbook(overflowingCost: true);
        var preview = await s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.xlsx", false, Ct);
        Assert.Equal(1, preview.ValidRows); Assert.Equal(1, preview.InvalidRows);
        Assert.Equal(3, preview.Rows.Single(x => !x.IsValid).RowNumber);
        Assert.Contains("стоимость", preview.Rows.Single(x => !x.IsValid).Error!.ToLowerInvariant());
        Assert.Empty(await s.Db.Assets.ToListAsync(Ct));
    }

    [Fact]
    public async Task OrdinaryXlsxCurrencyConversionKeepsDecimalValuesWithoutBinaryNoise()
    {
        await using var s = await fixture.OpenAsync();
        await SeedAsync(s);
        using var stream = Workbook(fractionalCost: true);
        var result = await s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.xlsx", true, Ct);
        Assert.Equal(2, result.AppliedRows);
        Assert.Equal(0.3m, (await s.Db.Assets.AsNoTracking().SingleAsync(x => x.InventoryNumber == "OS-3", Ct)).InitialCost);
    }

    [Fact]
    public async Task CancelledXlsxImportDoesNotStartReadingTheWorkbook()
    {
        await using var s = await fixture.OpenAsync();
        using var stream = Workbook();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Services.GetRequiredService<IAssetImportService>().ImportAsync(stream, "assets.xlsx", false, cancellation.Token));
        Assert.Equal(0, stream.Position);
    }
}
