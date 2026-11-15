namespace AssetInventory.Infrastructure.Inventories;

internal static class ScheduleCalendar
{
    private static readonly TimeSpan OrganizationOffset = TimeSpan.FromHours(5);

    public static DateTimeOffset Local(DateTimeOffset instant) => instant.ToOffset(OrganizationOffset);

    public static DateTimeOffset? Next(DateTimeOffset instant, int intervalMonths, int dayOfMonth)
    {
        try
        {
            var local = Local(instant);
            var month = new DateTime(local.Year, local.Month, 1).AddMonths(intervalMonths);
            var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(month.Year, month.Month));
            var next = month.AddDays(day - 1).Add(local.TimeOfDay);
            return new DateTimeOffset(next, OrganizationOffset).ToUniversalTime();
        }
        catch (ArgumentException) { return null; }
    }
}
