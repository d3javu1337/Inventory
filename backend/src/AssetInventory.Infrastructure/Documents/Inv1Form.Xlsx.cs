using AssetInventory.Domain;
using ClosedXML.Excel;

namespace AssetInventory.Infrastructure.Documents;

internal static partial class Inv1Form
{
    public static void AddWorksheet(IXLWorkbook book, Inventory inventory, Organization? organization,
        IReadOnlyDictionary<Guid, string> assignees, Group group, int index)
    {
        var sheet = book.Worksheets.Add(index == 1 ? "ИНВ-1" : $"ИНВ-1 ({index})");
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontSize = 9;
        sheet.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Style.Alignment.WrapText = true;
        sheet.ShowGridLines = false;
        for (var c = 1; c <= ColumnWidths.Length; c++)
            sheet.Column(c).Width = (ColumnWidths[c - 1] / 0.75 - 5) / 7;
        var setup = sheet.PageSetup;
        setup.PaperSize = XLPaperSize.A4Paper;
        setup.PageOrientation = XLPageOrientation.Landscape;
        setup.Margins.Left = setup.Margins.Right = 0.25;
        setup.Margins.Top = setup.Margins.Bottom = 0.32;
        setup.Margins.Header = 0.15;
        setup.Margins.Footer = 0.15;
        // A fixed scale lets Excel honor the manual breaks while keeping all 13 columns on one page.
        setup.Scale = 88;
        setup.Footer.Center.AddText(XLHFPredefinedText.PageNumber);

        var row = 1;
        Line("Унифицированная форма № ИНВ-1", 18, bold: true, right: true);
        Line(Approval, 16, right: true);
        foreach (var (label, value) in CoverFields(inventory, organization, group))
        {
            if (label == "Основные средства") Line(FormTitle, 27, bold: true, center: true, fontSize: 13);
            var caption = sheet.Range(row, 1, row, 6).Merge();
            caption.FirstCell().Value = label;
            var field = sheet.Range(row, 7, row, 13).Merge();
            field.FirstCell().Value = value ?? "";
            field.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            sheet.Row(row++).Height = Math.Max(17, 4 + 12 * Math.Max(WrappedLines(label, 340), WrappedLines(value, 410)));
        }
        Line("РАСПИСКА", 24, bold: true, center: true);
        Line(Receipt, 34);
        Sign(Responsible, group.ResponsibleName);
        Line(RentalNote, 16);

        var rows = Rows(group);
        foreach (var page in TablePages(rows))
        {
            setup.AddHorizontalPageBreak(row - 1);
            Line($"Вкладной лист формы № ИНВ-1 · {inventory.Number} · {group.Location}", 24, bold: true, right: true);
            var header = row;
            foreach (var (column, span, bothRows, text) in Headings)
            {
                var range = sheet.Range(header, column, header + (bothRows ? 1 : 0), column + span - 1);
                if (bothRows || span != 1) range.Merge();
                range.FirstCell().Value = text;
                Border(range);
            }
            foreach (var (column, text) in Subheadings)
                sheet.Cell(header + 1, column).Value = text;
            sheet.Range(header, 1, header + 2, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Range(header, 1, header + 2, 13).Style.Font.Bold = true;
            sheet.Range(header, 1, header + 2, 13).Style.Font.FontSize = 8;
            sheet.Row(row++).Height = 34;
            sheet.Row(row++).Height = 34;
            for (var c = 1; c <= 13; c++) sheet.Cell(row, c).Value = c;
            sheet.Row(row++).Height = 16;
            foreach (var entry in page)
            {
                var asset = entry.Asset;
                sheet.Cell(row, 1).Value = entry.Number;
                sheet.Cell(row, 2).Value = entry.Name;
                if (asset.ManufactureYear is int year) sheet.Cell(row, 6).Value = year;
                sheet.Cell(row, 7).Value = asset.InventoryNumber;
                sheet.Cell(row, 8).Value = asset.SerialNumber ?? "";
                sheet.Cell(row, 7).Style.Alignment.WrapText = asset.InventoryNumber == entry.InventoryNumber;
                sheet.Cell(row, 8).Style.Alignment.WrapText = asset.SerialNumber == entry.SerialNumber;
                WriteNumbers(row, entry.ActualQuantity, entry.ActualCost, 1, asset.InitialCost);
                sheet.Row(row++).Height = entry.Height;
            }
            var subtotal = Totals.From(page);
            sheet.Range(row, 1, row, 9).Merge().FirstCell().Value = "Итого";
            WriteNumbers(row, subtotal.ActualQuantity, subtotal.ActualCost, subtotal.Rows, subtotal.BookCost);
            sheet.Row(row).Height = 20;
            sheet.Range(row, 1, row, 13).Style.Font.Bold = true;
            var table = sheet.Range(header, 1, row++, 13);
            Border(table);
            table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            Line("Итого по странице:", 18, bold: true);
            foreach (var total in TotalsInWords(subtotal)) Line(total, 24);
        }

        setup.AddHorizontalPageBreak(row - 1);
        Line($"Итоговый лист формы № ИНВ-1 · {inventory.Number} · {group.Location}", 24, bold: true, right: true);
        Line("Итого по описи:", 24, bold: true);
        foreach (var total in TotalsInWords(Totals.From(rows))) Line(total, 24);
        Line(Checked, 26);
        Sign("Председатель комиссии");
        Sign("Члены комиссии");
        foreach (var assignment in inventory.Assignments.OrderBy(a => a.Role))
        {
            Sign("Исполнитель инвентаризации", assignees.GetValueOrDefault(assignment.UserId));
            if (Confirmation(assignment) is { } confirmation) Line(confirmation, 17);
        }
        Line(CustodyStatement(rows.Length), 38);
        Sign(Responsible, group.ResponsibleName);
        Line(SignatureDate, 20);
        Sign("Указанные в настоящей описи данные и расчёты проверил");
        Line(SignatureDate, 20);
        setup.PrintAreas.Add(1, 1, row - 1, 13);

        void Line(string text, double height, bool bold = false, bool center = false, bool right = false, double fontSize = 9)
        {
            var range = sheet.Range(row, 1, row, 13).Merge();
            range.FirstCell().Value = text;
            range.Style.Font.Bold = bold;
            range.Style.Font.FontSize = fontSize;
            if (center) range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (right) range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Row(row++).Height = height;
        }

        void Sign(string role, string? name = null)
        {
            Line(role, 19, bold: true);
            SignatureField(1, 4, "Должность", null);
            SignatureField(5, 7, "Подпись", null);
            SignatureField(8, 13, "Расшифровка подписи", name);
            sheet.Row(row++).Height = Math.Max(22, 4 + 12 * WrappedLines(name, 380));
            sheet.Row(row++).Height = 14;
        }

        void SignatureField(int first, int last, string label, string? value)
        {
            var field = sheet.Range(row, first, row, last).Merge();
            field.FirstCell().Value = value ?? "";
            field.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            var caption = sheet.Range(row + 1, first, row + 1, last).Merge();
            caption.FirstCell().Value = label;
            caption.Style.Font.FontSize = 8;
            caption.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        void WriteNumbers(int r, int actualQuantity, decimal actualCost, int bookQuantity, decimal bookCost)
        {
            sheet.Cell(r, 10).Value = actualQuantity;
            WriteMoney(sheet.Cell(r, 11), actualCost);
            sheet.Cell(r, 12).Value = bookQuantity;
            WriteMoney(sheet.Cell(r, 13), bookCost);
        }
    }

    private static void Border(IXLRange range) => range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

    private static void WriteMoney(IXLCell cell, decimal value)
    {
        // Excel stores only 15 significant digits.
        cell.Value = value >= AssetCosts.XlsxTextMinimum ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : XLCellValue.FromObject(value);
        cell.Style.NumberFormat.Format = "#,##0.00";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        cell.Style.Alignment.WrapText = false;
        cell.Style.Alignment.ShrinkToFit = true;
    }
}
