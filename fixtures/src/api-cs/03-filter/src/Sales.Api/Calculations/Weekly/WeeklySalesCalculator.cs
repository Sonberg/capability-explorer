using Sales.Domain;

namespace Sales.Api.Calculations;

public sealed class WeeklySalesCalculator : IWeeklySalesCalculator
{
    public decimal CalendarWeekAverage(IReadOnlyList<SaleRow> rows, DateOnly from, DateOnly to)
    {
        var weeks = IsoWeeks.CountInPeriod(from, to);
        if (weeks == 0)
        {
            return 0m;
        }

        var total = rows
            .Where(r => r.SoldOn >= from && r.SoldOn <= to && !r.IsReturn)
            .Sum(r => r.Quantity ?? 0m);

        return Math.Round(total / weeks, 2);
    }
}
