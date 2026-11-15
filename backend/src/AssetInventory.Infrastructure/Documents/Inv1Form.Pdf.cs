using AssetInventory.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AssetInventory.Infrastructure.Documents;

internal static partial class Inv1Form
{
    public static void AddPages(IDocumentContainer document, Inventory inventory, Organization? organization,
        IReadOnlyDictionary<Guid, string> assignees, Group group)
    {
        document.Page(page =>
        {
            ConfigurePage(page);
            page.Content().Column(column =>
            {
                column.Spacing(5);
                column.Item().AlignRight().Text("Унифицированная форма № ИНВ-1").Bold();
                column.Item().AlignRight().Text(Approval);
                foreach (var (label, value) in CoverFields(inventory, organization, group))
                {
                    if (label == "Основные средства")
                        column.Item().PaddingVertical(6).AlignCenter().Text(FormTitle).Bold().FontSize(14);
                    column.Item().Row(row =>
                    {
                        row.RelativeItem(5).Text(label);
                        row.RelativeItem(6).MinHeight(13).BorderBottom(0.5f).Text(value ?? "");
                    });
                }
                column.Item().PaddingTop(8).AlignCenter().Text("РАСПИСКА").Bold().FontSize(12);
                column.Item().Text(Receipt);
                AddSignature(column, Responsible, group.ResponsibleName);
                column.Item().Text(RentalNote).FontSize(8);
            });
        });

        var rows = Rows(group);
        foreach (var entries in TablePages(rows))
        {
            document.Page(page =>
            {
                ConfigurePage(page);
                page.Content().Column(column =>
                {
                    column.Item().AlignRight().PaddingBottom(8)
                        .Text($"Вкладной лист формы № ИНВ-1 · {inventory.Number} · {group.Location}").Bold();
                    var totals = Totals.From(entries);
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            foreach (var width in ColumnWidths) c.RelativeColumn(width);
                        });
                        table.Header(header =>
                        {
                            foreach (var (number, span, bothRows, title) in Headings)
                                Cell(header.Cell().Row(1).Column((uint)number).ColumnSpan((uint)span).RowSpan(bothRows ? 2u : 1u))
                                    .AlignCenter().Text(title).Bold();
                            foreach (var (number, title) in Subheadings)
                                Cell(header.Cell().Row(2).Column((uint)number)).AlignCenter().Text(title).Bold();
                            for (uint c = 1; c <= 13; c++)
                                Cell(header.Cell().Row(3).Column(c)).AlignCenter().Text(c.ToString(Russian));
                        });
                        foreach (var entry in entries)
                        {
                            var asset = entry.Asset;
                            string?[] values =
                            [
                                entry.Number.ToString(Russian), entry.Name, null, null, null,
                                asset.ManufactureYear?.ToString(Russian), entry.InventoryNumber, entry.SerialNumber, null,
                                entry.ActualQuantity.ToString(Russian), Money(entry.ActualCost), "1", Money(asset.InitialCost),
                            ];
                            for (var c = 0; c < values.Length; c++)
                            {
                                var cell = Cell(table.Cell(), entry.Height);
                                cell = c >= 9 ? cell.AlignRight() : c == 1 ? cell : cell.AlignCenter();
                                cell.Text(values[c] ?? "");
                            }
                        }
                        Cell(table.Cell().ColumnSpan(9)).Text("Итого").Bold();
                        Cell(table.Cell()).AlignRight().Text(totals.ActualQuantity.ToString(Russian)).Bold();
                        Cell(table.Cell()).AlignRight().Text(Money(totals.ActualCost)).Bold();
                        Cell(table.Cell()).AlignRight().Text(totals.Rows.ToString(Russian)).Bold();
                        Cell(table.Cell()).AlignRight().Text(Money(totals.BookCost)).Bold();
                    });
                    column.Item().PaddingTop(8).Text("Итого по странице:").Bold();
                    foreach (var total in TotalsInWords(totals)) column.Item().PaddingTop(3).Text(total);
                });
            });
        }

        document.Page(page =>
        {
            ConfigurePage(page);
            page.Content().Column(column =>
            {
                column.Spacing(7);
                column.Item().AlignRight().Text($"Итоговый лист формы № ИНВ-1 · {inventory.Number} · {group.Location}").Bold();
                column.Item().Text("Итого по описи:").Bold().FontSize(12);
                foreach (var total in TotalsInWords(Totals.From(rows))) column.Item().Text(total);
                column.Item().Text(Checked);
                AddSignature(column, "Председатель комиссии");
                AddSignature(column, "Члены комиссии");
                foreach (var assignment in inventory.Assignments.OrderBy(a => a.Role))
                {
                    AddSignature(column, "Исполнитель инвентаризации", assignees.GetValueOrDefault(assignment.UserId));
                    if (Confirmation(assignment) is { } confirmation) column.Item().Text(confirmation).FontSize(8);
                }
                column.Item().Text(CustodyStatement(rows.Length));
                AddSignature(column, Responsible, group.ResponsibleName);
                column.Item().Text(SignatureDate);
                AddSignature(column, "Указанные в настоящей описи данные и расчёты проверил");
                column.Item().Text(SignatureDate);
            });
        });
    }

    internal static void ConfigurePage(PageDescriptor page)
    {
        page.Size(PageSizes.A4.Landscape());
        page.Margin(24);
        page.DefaultTextStyle(s => s.FontSize(9));
        page.Footer().PaddingTop(6).AlignRight().Text(t =>
        {
            t.Span("Страница ");
            t.CurrentPageNumber();
            t.Span(" из ");
            t.TotalPages();
        });
    }

    private static IContainer Cell(IContainer container, int height = 0) =>
        container.MinHeight(height).Border(0.5f).Padding(2).AlignMiddle().DefaultTextStyle(s => s.FontSize(8));

    private static string Money(decimal amount) => amount.ToString("0.00", Russian);

    private static void AddSignature(ColumnDescriptor column, string role, string? name = null)
    {
        column.Item().PaddingTop(3).Text(role).Bold();
        column.Item().Row(row =>
        {
            row.Spacing(14);
            Field(row.RelativeItem(3), "Должность");
            Field(row.RelativeItem(2), "Подпись");
            Field(row.RelativeItem(4), "Расшифровка подписи", name);
        });

        static void Field(IContainer container, string label, string? value = null)
        {
            container.Column(field =>
            {
                field.Item().MinHeight(14).BorderBottom(0.5f).Text(value ?? "");
                field.Item().AlignCenter().Text(label).FontSize(8);
            });
        }
    }
}
