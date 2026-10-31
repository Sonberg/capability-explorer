using System.Globalization;

namespace Sales.Domain;

public static class IsoWeeks
{
    public static (int Year, int Week) Of(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(dateTime), ISOWeek.GetWeekOfYear(dateTime));
    }

    public static int CountInPeriod(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return 0;
        }

        var weeks = new HashSet<(int Year, int Week)>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            weeks.Add(Of(day));
        }

        return weeks.Count;
    }
}
