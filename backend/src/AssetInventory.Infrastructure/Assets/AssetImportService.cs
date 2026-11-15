using System.Globalization;
using System.IO.Compression;
using System.Text;
using AssetInventory.Application.Assets;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Assets;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace AssetInventory.Infrastructure.Assets;

internal sealed class AssetImportService(AppDbContext db, IAssetService assets) : IAssetImportService
{
    private static readonly string[] Headers = ["inventoryNumber", "name", "accountingAccount", "serialNumber", "manufactureYear", "acceptedAt", "initialCost", "departmentCode", "locationCode", "molPersonnelNumber"];
    private const int MaxUploadBytes = 20 * 1024 * 1024;
    private const int MaxExpandedXlsxBytes = 128 * 1024 * 1024;
    private const int MaxXlsxEntries = 4096;
    private sealed record RawRow(int Number, string[] Values, string? Error = null);

    public async Task<ImportPreviewResponse> ImportAsync(Stream stream, string fileName, bool apply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx")) throw new RequestValidationException("Поддерживаются только CSV и XLSX.");
        List<RawRow> raw;
        try { raw = extension == ".xlsx" ? await ReadXlsxAsync(stream, ct) : ReadCsv(stream, ct); }
        catch (MalformedLineException exception)
        { throw new RequestValidationException($"CSV, строка {exception.LineNumber}: проверьте кавычки и разделители."); }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not AssetInventory.Application.Common.ApplicationException)
        { throw new RequestValidationException("Файл поврежден или не соответствует CSV/XLSX шаблону."); }
        if (raw.Count == 0) throw new RequestValidationException("Файл не содержит строк данных.");
        if (raw.Count > 10_000) throw new RequestValidationException("В одном файле допускается не более 10 000 строк.");

        await using var tx = apply ? await db.Database.BeginTransactionAsync(ct) : null;
        if (tx is not null)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(736412889)", ct);
            // Resolve codes only after pending catalog changes have committed.
            await RegistryWriteGuard.BeginAsync(db, true, ct);
        }

        // Preserve exact, case-sensitive catalog identities. A unique fallback
        // keeps existing case-insensitive files working without guessing a MOL.
        // Archived exact matches must remain visible to reject them explicitly.
        var departments = (await db.Departments.AsNoTracking().ToListAsync(ct)).ToLookup(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var locations = (await db.Locations.AsNoTracking().ToListAsync(ct)).GroupBy(x => x.DepartmentId)
            .ToDictionary(g => g.Key, g => g.ToLookup(x => x.Code, StringComparer.OrdinalIgnoreCase));
        var employees = (await db.Employees.AsNoTracking().ToListAsync(ct)).ToLookup(x => x.PersonnelNumber, StringComparer.OrdinalIgnoreCase);
        var checkedRows = new List<(int Row, UpsertAssetRequest? Request, string Number, string? Error)>();
        var archivedNumbers = (await db.Assets.AsNoTracking().Where(x => x.ArchivedAt != null || x.Status == AssetStatus.Archived).Select(x => x.InventoryNumber).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var input in raw)
        {
            ct.ThrowIfCancellationRequested();
            var (row, values, inputError) = input;
            var number = Get(values, 0);
            try
            {
                if (values.Length != Headers.Length) throw new FormatException($"Ожидается ровно {Headers.Length} столбцов по шаблону.");
                if (inputError is not null) throw new FormatException(inputError);
                if (values.Any(value => value.Contains('\0'))) throw new FormatException("Строка содержит недопустимый нулевой символ.");
                if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(Get(values, 1)) || string.IsNullOrWhiteSpace(Get(values, 2))) throw new FormatException("Инвентарный номер, наименование и счет обязательны.");
                var department = ResolveReference(departments[Get(values, 7)], Get(values, 7), x => x.Code, "Подразделение");
                if (department.IsArchived) throw new FormatException("Подразделение архивировано.");
                if (!locations.TryGetValue(department.Id, out var departmentLocations)) throw new FormatException("Помещение не найдено в подразделении.");
                var location = ResolveReference(departmentLocations[Get(values, 8)], Get(values, 8), x => x.Code, "Помещение в выбранном подразделении");
                if (location.IsArchived) throw new FormatException("Помещение архивировано.");
                var employee = ResolveReference(employees[Get(values, 9)], Get(values, 9), x => x.PersonnelNumber, "МОЛ");
                if (employee.IsArchived || !employee.IsMateriallyResponsible || !employee.IsEmployed) throw new FormatException("Сотрудник не является работающим неархивным МОЛ.");
                if (!DateOnly.TryParseExact(Get(values, 5), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new FormatException("Некорректная дата учета, ожидается YYYY-MM-DD.");
                var costText = Get(values, 6).Replace(',', '.');
                if (HasFractionOfKopeck(costText)
                    || !decimal.TryParse(costText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cost)
                    || !AssetCosts.IsSupported(cost))
                    throw new FormatException("Стоимость должна быть неотрицательной, с максимум двумя знаками после запятой и помещаться в numeric(19,2).");
                if (number.Length > 100 || Get(values, 1).Length > 500 || Get(values, 2).Length > 50 || Get(values, 3).Length > 200) throw new FormatException("Превышена допустимая длина поля ОС.");
                if (archivedNumbers.Contains(number)) throw new FormatException("Архивное ОС нельзя обновить импортом.");
                int? year = null; if (!string.IsNullOrWhiteSpace(Get(values, 4))) { if (!int.TryParse(Get(values, 4), out var parsed) || parsed < 1900 || parsed > DateTimeOffset.UtcNow.Year + 1) throw new FormatException("Некорректный год выпуска."); year = parsed; }
                checkedRows.Add((row, new(number, Get(values, 1), Get(values, 2), Null(Get(values, 3)), year, date, cost, department.Id, location.Id, employee.Id), number, null));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException) { checkedRows.Add((row, null, number, ex.Message)); }
        }
        var duplicates = checkedRows.Where(x => x.Number.Length > 0).GroupBy(x => x.Number, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        checkedRows = checkedRows.Select(x => x.Request is not null && duplicates.Contains(x.Number)
            ? (x.Row, null, x.Number, (string?)"Дублирующийся инвентарный номер в файле.") : x).ToList();
        var valid = checkedRows.Where(x => x.Request is not null).ToList(); var applied = 0;
        if (tx is not null)
        {
            if (checkedRows.Any(x => x.Error is not null)) throw new RequestValidationException("Импорт не применен: исправьте ошибки, найденные при dry-run.");
            try
            {
                foreach (var row in valid)
                {
                    var existing = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.InventoryNumber == row.Number, ct);
                    if (existing is null) await assets.CreateAsync(row.Request!, ct); else await assets.UpdateAsync(existing.Id, existing.Version, row.Request!, ct);
                    applied++;
                    // Each row is already saved inside the same transaction.
                    // Avoid scanning the tracked entities of all earlier rows on every save.
                    db.ChangeTracker.Clear();
                }
                await tx.CommitAsync(ct);
            }
            catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
        }
        return new(Guid.CreateVersion7(), checkedRows.Count, valid.Count, checkedRows.Count - valid.Count, applied, checkedRows.Select(x => new ImportPreviewRow(x.Row, x.Number, x.Error is null, x.Error)).ToList());
    }

    public byte[] GetCsvTemplate() => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join(';', Headers) + "\r\nOS-001;Ноутбук;01.01;SN123;2025;2026-01-01;100000.00;DEP-01;ROOM-01;EMP-001\r\n")).ToArray();
    private static void ValidateHeaders(string[] values)
    {
        if (!values.SequenceEqual(Headers, StringComparer.OrdinalIgnoreCase)) throw new RequestValidationException("Заголовки и порядок столбцов должны соответствовать опубликованному шаблону.");
    }
    private static List<RawRow> ReadCsv(Stream stream, CancellationToken ct)
    {
        using var parser = new TextFieldParser(stream, new UTF8Encoding(false, true), true, true) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(";");
        ValidateHeaders(parser.ReadFields() ?? []);
        var result = new List<RawRow>();
        while (!parser.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            var line = checked((int)parser.LineNumber);
            result.Add(new(line, parser.ReadFields() ?? []));
            if (result.Count > 10_000) throw new RequestValidationException("В одном файле допускается не более 10 000 строк.");
        }
        return result;
    }
    private static async Task<List<RawRow>> ReadXlsxAsync(Stream stream, CancellationToken ct)
    {
        using var buffered = stream.CanSeek ? null : new MemoryStream();
        if (buffered is not null)
        {
            var buffer = new byte[32 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (count > MaxUploadBytes - buffered.Length)
                    throw new RequestValidationException("Файл превышает 20 МБ.");
                await buffered.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            buffered.Position = 0;
            stream = buffered;
        }
        await ValidateXlsxArchiveAsync(stream, ct);
        using var book = new XLWorkbook(stream); var sheet = book.Worksheet(1);
        if ((sheet.Row(1).LastCellUsed(XLCellsUsedOptions.Contents)?.Address.ColumnNumber ?? 0) > Headers.Length)
            throw new RequestValidationException("В заголовке есть лишние столбцы. Используйте опубликованный шаблон.");
        ValidateHeaders(Enumerable.Range(1, Headers.Length).Select(c => sheet.Cell(1, c).GetString().Trim()).ToArray());
        var result = new List<RawRow>();
        foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() > 1))
        {
            ct.ThrowIfCancellationRequested();
            var width = (row.LastCellUsed(XLCellsUsedOptions.Contents)?.Address.ColumnNumber ?? 0) > Headers.Length ? Headers.Length + 1 : Headers.Length;
            var values = Enumerable.Range(1, width).Select(c => row.Cell(c).GetString().Trim()).ToArray();
            string? error = null;
            if (row.Cell(6).DataType == XLDataType.DateTime) values[5] = row.Cell(6).GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (row.Cell(7).DataType == XLDataType.Number)
            {
                var cost = row.Cell(7).GetDouble();
                if (Math.Abs(cost) >= (double)AssetCosts.XlsxTextMinimum)
                    error = "Стоимость с 14 и более цифрами до запятой укажите в XLSX как текст.";
                else values[6] = row.Cell(7).GetValue<decimal>().ToString(CultureInfo.InvariantCulture);
            }
            result.Add(new(row.RowNumber(), values, error));
            if (result.Count > 10_000) throw new RequestValidationException("В одном файле допускается не более 10 000 строк.");
        }
        return result;
    }

    private static async Task ValidateXlsxArchiveAsync(Stream stream, CancellationToken ct)
    {
        var start = stream.Position;
        const string tooLarge = "Содержимое XLSX после распаковки превышает 128 МБ.";
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > MaxXlsxEntries)
                throw new RequestValidationException("Внутри XLSX слишком много файлов.");
            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Length > MaxExpandedXlsxBytes - declared) throw new RequestValidationException(tooLarge);
                declared += entry.Length;
            }
            var buffer = new byte[32 * 1024];
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                using var content = entry.Open();
                int count;
                while ((count = await content.ReadAsync(buffer, ct)) > 0)
                {
                    if (count > MaxExpandedXlsxBytes - expanded) throw new RequestValidationException(tooLarge);
                    expanded += count;
                }
            }
        }
        stream.Position = start;
    }

    private static bool HasFractionOfKopeck(string value)
    {
        var point = value.IndexOf('.');
        return point >= 0 && value.Length > point + 3 && value.AsSpan(point + 3).ContainsAnyExcept('0');
    }
    private static T ResolveReference<T>(IEnumerable<T> matches, string code, Func<T, string> key, string name) where T : class
    {
        var candidates = matches.ToArray();
        var exact = candidates.SingleOrDefault(x => string.Equals(key(x), code, StringComparison.Ordinal));
        if (exact is not null) return exact;
        if (candidates.Length == 1) return candidates[0];
        throw new FormatException(candidates.Length == 0 ? $"{name}: код не найден."
            : $"{name}: неоднозначный код «{code}». Укажите точный регистр кода из справочника.");
    }
    private static string Get(string[] values, int index) => index < values.Length ? values[index].Trim() : "";
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
