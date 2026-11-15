using System.Security.Cryptography;
using System.Text.Json;
using AssetInventory.Application.Common;
using AssetInventory.Application.Inventories;
using AssetInventory.Contracts.Inventories;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using AssetInventory.Infrastructure.Storage;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AssetInventory.Infrastructure.Documents;

internal sealed class DocumentService(AppDbContext db, IOptions<FileStorageOptions> storageOptions) : IDocumentService
{
    private readonly string root = Path.GetFullPath(storageOptions.Value.RootPath);

    public async Task GenerateInventoryReportsAsync(Guid inventoryId, CancellationToken ct)
    {
        await GetInventoryReportAsync(inventoryId, "xlsx", null, ct);
        await GetInventoryReportAsync(inventoryId, "pdf", null, ct);
    }

    public async Task<GeneratedFile> GetQrAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, ct) ?? throw new NotFoundException("Основное средство не найдено.");
        using var data = QRCodeGenerator.GenerateQrCode(asset.Id.ToString("D"), QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var png = code.GetGraphic(12, [0, 0, 0], [255, 255, 255], true);
        QuestPDF.Settings.License = LicenseType.Community;
        var label = Document.Create(doc => doc.Page(page =>
        {
            page.Size(240, 360); page.Margin(12); page.PageColor(Colors.White);
            page.Content().Column(column =>
            {
                column.Item().Height(210).AlignCenter().Image(png).FitArea();
                column.Item().AlignCenter().Text(asset.InventoryNumber).Bold().FontSize(12);
                column.Item().AlignCenter().Text(asset.Name.Length > 60 ? asset.Name[..57] + "..." : asset.Name).FontSize(9);
            });
        })).GenerateImages().Single();
        return new(label, "image/png", $"QR-{SafeName(asset.InventoryNumber)}-{SafeName(asset.Name)}.png");
    }

    public async Task<GeneratedFile> GetInventoryReportAsync(Guid inventoryId, string format, Guid? assignedUserId, CancellationToken ct)
    {
        var normalized = format.Trim('.').ToLowerInvariant();
        DocumentType? requestedType = normalized switch
        {
            "xlsx" => DocumentType.InventoryActXlsx,
            "pdf" => DocumentType.InventoryActPdf,
            _ => null
        };
        if (requestedType is { } requested)
        {
            var available = await db.GeneratedDocuments.AsNoTracking().SingleOrDefaultAsync(d =>
                d.InventoryId == inventoryId && d.Type == requested && db.Inventories.Any(i =>
                    i.Id == inventoryId && i.Status == InventoryStatus.Completed &&
                    (assignedUserId == null || i.Assignments.Any(member => member.UserId == assignedUserId))), ct);
            if (available is not null) return await ReadStoredAsync(available, ct);
        }

        // Only legacy creation (or an in-flight confirmation) needs serialization.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var inventory = await db.Inventories.FromSqlInterpolated($"SELECT * FROM inventories WHERE id = {inventoryId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Инвентаризация не найдена.");
        if (assignedUserId is { } userId && !await db.InventoryAssignments.AnyAsync(x => x.InventoryId == inventoryId && x.UserId == userId, ct))
            throw new NotFoundException("Инвентаризация не найдена.");
        if (inventory.Status != InventoryStatus.Completed) throw new ConflictException("Отчет доступен после подтверждения назначенным сотрудником.");
        var type = requestedType ?? throw new RequestValidationException("Поддерживаются форматы xlsx и pdf.");
        var metadata = await db.GeneratedDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.InventoryId == inventoryId && x.Type == type, ct);
        if (metadata is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(ct);
            return await ReadStoredAsync(metadata, ct);
        }

        // The snapshot is needed only when creating a report, not when reading the frozen file.
        inventory = await db.Inventories.AsNoTracking().Include(x => x.Items).Include(x => x.Assignments)
            .ThenInclude(x => x.User).ThenInclude(x => x.Employee).SingleAsync(x => x.Id == inventoryId, ct);
        ReportContext context;
        if (inventory.ReportContextJson is null)
        {
            var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(ct);
            var unexpected = await db.UnexpectedScans.AsNoTracking().Where(x => x.InventoryId == inventoryId).OrderBy(x => x.ScannedAt)
                .Select(x => new UnexpectedObservation(x.RawValue, x.Type.ToString(), x.ScannedAt)).ToListAsync(ct);
            context = new(organization, inventory.Assignments.ToDictionary(x => x.UserId, x => x.User.Employee?.FullName ?? x.User.Login), unexpected);
            var json = JsonSerializer.Serialize(context);
            await db.Inventories.Where(x => x.Id == inventoryId).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReportContextJson, json), ct);
        }
        else context = JsonSerializer.Deserialize<ReportContext>(inventory.ReportContextJson) ?? throw new InvalidOperationException("Отсутствует снимок реквизитов отчета.");
        var content = type == DocumentType.InventoryActXlsx ? BuildXlsx(inventory, context) : BuildPdf(inventory, context);
        var fileName = $"INV-1-{SafeName(inventory.Number)}.{normalized}"; var storageKey = Path.Combine("reports", inventory.Id.ToString("N"), fileName);
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, content, ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var hash = Convert.ToHexString(SHA256.HashData(content));
        db.GeneratedDocuments.Add(new GeneratedDocument { InventoryId = inventoryId, Type = type, StorageKey = storageKey.Replace('\\', '/'), FileName = fileName, ContentType = ContentType(type), Sha256 = hash, Size = content.LongLength });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(content, ContentType(type), fileName);
    }

    private async Task<GeneratedFile> ReadStoredAsync(GeneratedDocument metadata, CancellationToken ct)
    {
        byte[] content;
        try { content = await File.ReadAllBytesAsync(ResolvePath(metadata.StorageKey), ct); }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { throw new ConflictException("Сохраненный отчет отсутствует. Восстановите файл из резервной копии."); }
        if (!Convert.ToHexString(SHA256.HashData(content)).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Контрольная сумма отчета не совпадает. Восстановите файл из резервной копии.");
        return new(content, metadata.ContentType, metadata.FileName);
    }

    private string ResolvePath(string key)
    {
        var path = Path.GetFullPath(Path.Combine(root, key));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Некорректный путь документа.");
        return path;
    }
    private sealed record UnexpectedObservation(string RawValue, string Type, DateTimeOffset ScannedAt);
    private sealed record ReportContext(Organization? Organization, Dictionary<Guid, string> Assignees, List<UnexpectedObservation> Unexpected);
    private static string ResultLabel(ScanResult result) => result == ScanResult.Found ? "Найдено" : result == ScanResult.NotFound ? "Не найдено" : "Ожидает";
    private static string ConditionLabel(AssetCondition? condition) => condition == AssetCondition.Serviceable ? "Исправно" : condition == AssetCondition.Damaged ? "Повреждено" : condition == AssetCondition.Unusable ? "Непригодно" : "-";
    private static byte[] BuildXlsx(Inventory x, ReportContext context)
    {
        using var book = new XLWorkbook();
        var groups = Inv1Form.Groups(x);
        for (var index = 0; index < groups.Length; index++)
            Inv1Form.AddWorksheet(book, x, context.Organization, context.Assignees, groups[index], index + 1);
        var discrepancies = book.Worksheets.Add("Расхождения");
        discrepancies.Cell(1, 1).Value = "Недостачи, состояние ОС и замечания";
        var d = 2;
        foreach (var i in Discrepancies(x))
        { discrepancies.Cell(d, 1).Value = i.InventoryNumber; discrepancies.Cell(d, 2).Value = i.Name; discrepancies.Cell(d, 3).Value = ResultLabel(i.Result); discrepancies.Cell(d, 4).Value = ConditionLabel(i.Condition); discrepancies.Cell(d++, 5).Value = SpreadsheetComment(i.Comment); }
        discrepancies.Cell(d++, 1).Value = "Неожиданные QR";
        foreach (var scan in context.Unexpected) { discrepancies.Cell(d, 1).Value = scan.RawValue; discrepancies.Cell(d++, 2).Value = scan.Type == "UnknownQr" ? "Неизвестный QR" : "Вне области"; }
        // Double arguments select width bounds; integers select the start/end row overload.
        discrepancies.Columns().AdjustToContents(8d, 45d);
        discrepancies.RangeUsed()!.Style.Alignment.WrapText = true;
        discrepancies.RangeUsed()!.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        FitWrappedRows(discrepancies);
        var longComments = x.Items.Where(i => NeedsCommentAppendix(i.Comment)).OrderBy(i => i.InventoryNumber).ToList();
        if (longComments.Count > 0)
        {
            var comments = book.Worksheets.Add("Комментарии");
            comments.Cell(1, 1).Value = "Инв. номер"; comments.Cell(1, 2).Value = "Наименование"; comments.Cell(1, 3).Value = "Полный комментарий (продолжения идут следующей строкой)";
            comments.Row(1).Style.Font.Bold = true;
            var commentRow = 2;
            foreach (var item in longComments)
                foreach (var part in CommentParts(item.Comment!))
                {
                    comments.Cell(commentRow, 1).Value = item.InventoryNumber;
                    comments.Cell(commentRow, 2).Value = item.Name;
                    comments.Cell(commentRow++, 3).Value = part;
                }
            comments.Column(1).Width = 20; comments.Column(2).Width = 45; comments.Column(3).Width = 90;
            comments.RangeUsed()!.Style.Alignment.WrapText = true;
            comments.RangeUsed()!.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            FitWrappedRows(comments);
            comments.SheetView.FreezeRows(1); comments.PageSetup.SetRowsToRepeatAtTop(1, 1);
            comments.PageSetup.PageOrientation = XLPageOrientation.Landscape; comments.PageSetup.FitToPages(1, 0);
        }
        using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
    }

    private static bool NeedsCommentAppendix(string? text) => text is not null && (text.Length > 350 || text.Count(c => c == '\n') >= 8);
    private static string? SpreadsheetComment(string? text) => NeedsCommentAppendix(text) ? "Полный текст: лист «Комментарии»." : text;
    private static IEnumerable<string> CommentParts(string text)
    {
        // Preserve every character, including line breaks. Each part fits an Excel row.
        for (var offset = 0; offset < text.Length;)
        {
            var end = Math.Min(text.Length, offset + 160);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            var lines = 0;
            for (var index = offset; index < end; index++)
                if (text[index] == '\n' && ++lines == 6) { end = index + 1; break; }
            yield return text[offset..end];
            offset = end;
        }
    }

    private static void FitWrappedRows(IXLWorksheet sheet)
    {
        // ClosedXML's automatic row fit does not account for wrapping at bounded widths.
        foreach (var row in sheet.RowsUsed())
        {
            var height = 20d;
            foreach (var cell in row.CellsUsed())
            {
                var width = cell.IsMerged()
                    ? cell.MergedRange().Columns().Sum(column => sheet.Column(column.ColumnNumber()).Width)
                    : sheet.Column(cell.Address.ColumnNumber).Width;
                var charactersPerLine = Math.Max(1, Math.Floor(width / 1.3));
                var lines = cell.GetFormattedString().Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / charactersPerLine)));
                height = Math.Max(height, lines * cell.Style.Font.FontSize * 1.4 + 4);
            }
            row.Height = Math.Min(409, height);
        }
    }

    private static IEnumerable<InventoryAssetSnapshot> Discrepancies(Inventory inventory) =>
        inventory.Items.Where(i => i.Result == ScanResult.NotFound ||
            i.Condition is AssetCondition.Damaged or AssetCondition.Unusable || !string.IsNullOrEmpty(i.Comment))
            .OrderBy(i => i.InventoryNumber, StringComparer.Ordinal);

    private static byte[] BuildPdf(Inventory x, ReportContext context)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(document =>
        {
            foreach (var group in Inv1Form.Groups(x))
                Inv1Form.AddPages(document, x, context.Organization, context.Assignees, group);
            var discrepancies = Discrepancies(x).ToArray();
            if (discrepancies.Length == 0 && context.Unexpected.Count == 0) return;
            document.Page(page =>
            {
                Inv1Form.ConfigurePage(page);
                page.Header().PaddingBottom(8).Text($"Приложение к описи № {x.Number}: расхождения, замечания и неожиданные QR").Bold();
                page.Content().Column(column =>
                {
                    foreach (var i in discrepancies)
                        column.Item().Text($"{i.InventoryNumber}: {i.Name} — {ResultLabel(i.Result)}, {ConditionLabel(i.Condition)}. {PdfComment(i.Comment)}");
                    foreach (var scan in context.Unexpected)
                        column.Item().Text($"{(scan.Type == "UnknownQr" ? "Неизвестный QR" : "Вне области")}: {scan.RawValue}");
                    if (discrepancies.Any(i => NeedsCommentAppendix(i.Comment)))
                    {
                        column.Item().PaddingTop(8).Text("Полные комментарии").Bold();
                        foreach (var item in discrepancies.Where(i => NeedsCommentAppendix(i.Comment)))
                        {
                            column.Item().PaddingTop(6).Text($"{item.InventoryNumber}: {item.Name}").Bold();
                            foreach (var part in CommentParts(item.Comment!)) column.Item().Text(part);
                        }
                    }
                });
            });
        }).GeneratePdf();
    }

    private static string? PdfComment(string? text) => NeedsCommentAppendix(text) ? "Полный текст: приложение с комментариями." : text;
    private static string ContentType(DocumentType type) => type == DocumentType.InventoryActXlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/pdf";
    private static string SafeName(string value) => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
