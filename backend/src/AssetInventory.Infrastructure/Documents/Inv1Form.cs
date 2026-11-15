using System.Globalization;
using AssetInventory.Domain;

namespace AssetInventory.Infrastructure.Documents;

internal static partial class Inv1Form
{
    private const string FormTitle = "ИНВЕНТАРИЗАЦИОННАЯ ОПИСЬ ОСНОВНЫХ СРЕДСТВ";
    private const string Approval = "Утверждена постановлением Госкомстата России от 18.08.98 № 88";
    private const string Receipt = "К началу проведения инвентаризации все расходные и приходные документы на основные средства сданы в бухгалтерию и все основные средства, поступившие на мою (нашу) ответственность, оприходованы, а выбывшие списаны в расход.";
    private const string Responsible = "Лицо, ответственное за сохранность основных средств";
    private const string Checked = "Все подсчёты итогов по строкам, страницам и в целом по инвентаризационной описи основных средств проверены.";
    private const string SignatureDate = "Дата: «____» ____________________ ______ г.";
    private const string RentalNote = "* Заполняется по основным средствам, полученным по договору аренды.";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly TimeZoneInfo LocalZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yekaterinburg");
    private static readonly int[] ColumnWidths = [28, 151, 49, 35, 40, 46, 68, 65, 59, 44, 75, 44, 75];
    private static readonly (int Column, int Span, bool BothRows, string Text)[] Headings =
    [
        (1, 1, true, "№\nп/п"),
        (2, 1, true, "Наименование, назначение и краткая характеристика объекта"),
        (3, 3, false, "Документ, подтверждающий принятие объекта на ответственное хранение (аренду)"),
        (6, 1, true, "Год выпуска\n(постройки,\nприобре-\nтения)"),
        (7, 3, false, "Номер"),
        (10, 2, false, "Фактическое наличие"),
        (12, 2, false, "По данным бухгалтерского учёта"),
    ];
    private static readonly (int Column, string Text)[] Subheadings =
    [
        (3, "наимено-\nвание"), (4, "дата"), (5, "номер"), (7, "инвентарный"), (8, "заводской"),
        (9, "паспорта (документа о регистрации)"), (10, "кол-во,\nшт."), (11, "стоимость, руб. коп."),
        (12, "кол-во,\nшт."), (13, "стоимость, руб. коп."),
    ];

    internal sealed record Group(string Department, string Location, string ResponsibleName, InventoryAssetSnapshot[] Assets);
    private sealed record AssetRow(int Number, InventoryAssetSnapshot Asset)
    {
        public int ActualQuantity => Asset.Result == ScanResult.Found ? 1 : 0;
        public decimal ActualCost => ActualQuantity * Asset.InitialCost;
        public string Name => ForTable(Asset.Name);
        public string InventoryNumber => ForTable(Asset.InventoryNumber);
        public string? SerialNumber => Asset.SerialNumber is { } serial ? ForTable(serial) : null;
        public int Height => 6 + 11 * Math.Max(
            TableLines(Name, ColumnWidths[1]),
            Math.Max(TableLines(InventoryNumber, ColumnWidths[6]), TableLines(SerialNumber, ColumnWidths[7])));
    }

    private sealed record Totals(int Rows, int ActualQuantity, decimal ActualCost, decimal BookCost)
    {
        public static Totals From(IEnumerable<AssetRow> rows) =>
            new(rows.Count(), rows.Sum(r => r.ActualQuantity), rows.Sum(r => r.ActualCost), rows.Sum(r => r.Asset.InitialCost));
    }

    public static Group[] Groups(Inventory inventory) => inventory.Items
        .GroupBy(i => new { i.DepartmentId, i.DepartmentName, i.LocationId, i.LocationName, i.MateriallyResponsibleEmployeeId, i.MateriallyResponsibleFullName })
        .OrderBy(g => g.Key.DepartmentName, StringComparer.Ordinal)
        .ThenBy(g => g.Key.LocationName, StringComparer.Ordinal)
        .ThenBy(g => g.Key.MateriallyResponsibleFullName, StringComparer.Ordinal)
        .ThenBy(g => g.Key.LocationId)
        .ThenBy(g => g.Key.MateriallyResponsibleEmployeeId)
        .Select(g => new Group(g.Key.DepartmentName, g.Key.LocationName, g.Key.MateriallyResponsibleFullName,
            g.OrderBy(i => i.InventoryNumber, StringComparer.Ordinal).ToArray()))
        .ToArray();

    private static AssetRow[] Rows(Group group) => group.Assets.Select((asset, index) => new AssetRow(index + 1, asset)).ToArray();

    private static IEnumerable<List<AssetRow>> TablePages(AssetRow[] rows)
    {
        const int detailHeight = 345;
        var page = new List<AssetRow>();
        var used = 0;
        foreach (var row in rows)
        {
            if (page.Count > 0 && used + row.Height > detailHeight)
            {
                yield return page;
                page = [];
                used = 0;
            }
            page.Add(row);
            used += row.Height;
        }
        if (page.Count > 0) yield return page;
    }

    private static int WrappedLines(string? value, int width) =>
        value?.Split('\n').Sum(s => Math.Max(1, (s.Length + width / 8 - 1) / (width / 8))) ?? 1;

    private static string ForTable(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static int TableLines(string? value, int columnWidth)
    {
        var lines = 1;
        var used = 0d;
        var available = columnWidth - 6;
        foreach (var word in (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = word.Sum(ch => "ЖШЩЮМФЫДжшщюмфыдWw".Contains(ch) ? 8d : 5.4);
            if (used > 0 && used + 3 + width > available)
            {
                lines++;
                used = 0;
            }
            while (width > available)
            {
                lines++;
                width -= available;
            }
            used += (used > 0 ? 3 : 0) + width;
        }
        return lines;
    }

    private static (string Label, string? Value)[] CoverFields(Inventory inventory, Organization? org, Group group) =>
    [
        ("Форма по ОКУД", "0317001"), ("Организация", org?.Name), ("Код по ОКПО", org?.Okpo),
        ("Структурное подразделение", group.Department), ("Вид деятельности", null),
        ("Основание для проведения инвентаризации (приказ, постановление, распоряжение)", null),
        ("Номер документа-основания", null), ("Дата документа-основания", null),
        ("Дата начала инвентаризации", LocalDate(inventory.StartedAt)),
        ("Дата окончания инвентаризации", LocalDate(inventory.ScanningFinishedAt ?? inventory.CompletedAt)),
        ("Вид операции", null), ("Номер документа", inventory.Number),
        ("Дата составления", LocalDate(inventory.CompletedAt)),
        ("Основные средства", null), ("Находящиеся (собственность, хранение, аренда)", null),
        ("Местонахождение", group.Location), ("Арендодатель *", null),
    ];

    private static string? LocalDate(DateTimeOffset? value) =>
        value is { } date ? TimeZoneInfo.ConvertTime(date, LocalZone).ToString("dd.MM.yyyy", Russian) : null;

    private static string? Confirmation(InventoryAssignment assignment) =>
        assignment.ConfirmedAt is { } timestamp
            ? $"Подтверждено в системе {TimeZoneInfo.ConvertTime(timestamp, LocalZone).ToString("dd.MM.yyyy HH:mm", Russian)}"
            : null;

    private static string CustodyStatement(int rows) =>
        $"Основные средства с № 1 по № {rows} проверены в моём (нашем) присутствии и внесены в опись. Претензий к проверке не имею (не имеем). Перечисленные основные средства находятся на моём (нашем) ответственном хранении.";

    private static IEnumerable<string> TotalsInWords(Totals totals)
    {
        yield return $"а) Количество порядковых номеров: {InWords(totals.Rows)}";
        yield return $"б) Общее количество единиц фактически: {InWords(totals.ActualQuantity)}";
        yield return $"в) На сумму фактически: {InWords(decimal.Truncate(totals.ActualCost))} руб. {totals.ActualCost % 1 * 100:00} коп.";
    }

    private static readonly string[] Units = ["", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] Teens = ["десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    private static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    private static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];
    private static readonly string[][] Magnitudes =
    [
        [], ["тысяча", "тысячи", "тысяч"], ["миллион", "миллиона", "миллионов"],
        ["миллиард", "миллиарда", "миллиардов"], ["триллион", "триллиона", "триллионов"],
        ["квадриллион", "квадриллиона", "квадриллионов"], ["квинтиллион", "квинтиллиона", "квинтиллионов"],
        ["секстиллион", "секстиллиона", "секстиллионов"], ["септиллион", "септиллиона", "септиллионов"],
        ["октиллион", "октиллиона", "октиллионов"], ["нониллион", "нониллиона", "нониллионов"],
    ];

    private static string InWords(decimal value)
    {
        if (value == 0) return "ноль";
        var groups = new Stack<string>();
        for (var magnitude = 0; value > 0; magnitude++, value = decimal.Truncate(value / 1000))
        {
            var triplet = (int)(value % 1000);
            if (triplet == 0) continue;
            var lastTwo = triplet % 100;
            var last = triplet % 10;
            var words = new List<string> { Hundreds[triplet / 100] };
            if (lastTwo is >= 10 and <= 19) words.Add(Teens[lastTwo - 10]);
            else
            {
                words.Add(Tens[lastTwo / 10]);
                words.Add(magnitude == 1 ? last switch { 1 => "одна", 2 => "две", _ => Units[last] } : Units[last]);
            }
            if (magnitude > 0)
            {
                var inflection = lastTwo is >= 11 and <= 14 ? 2 : last switch { 1 => 0, >= 2 and <= 4 => 1, _ => 2 };
                words.Add(Magnitudes[magnitude][inflection]);
            }
            groups.Push(string.Join(' ', words.Where(w => w.Length > 0)));
        }
        return string.Join(' ', groups);
    }
}
